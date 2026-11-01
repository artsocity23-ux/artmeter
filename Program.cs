// ArtMeter: a light resource monitor for Windows. A small always-on-top widget shows CPU, RAM and every NVIDIA GPU
// (load, used / free VRAM, which card) plus the programs eating the most RAM and VRAM; the tray icon shows three bars.
// Build: build.cmd (csc from .NET Framework 4, no dependencies). `ArtMeter.exe --dump <prefix>` writes one sample
// as text and both widget layouts as PNG, for checking numbers without the UI.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

static class Program {
  [STAThread]
  static void Main(string[] args) {
    Native.SetProcessDPIAware();
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    if (args.Length == 2 && args[0] == "--dump") {
      Dump(args[1]);
      return;
    }
    bool first;
    using (var single = new Mutex(true, @"Local\ArtMeter.Single", out first))
    using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ArtMeter.Show")) {
      if (!first) {
        show.Set(); // launching again (Start menu, pinned shortcut) brings back the running widget
        return;
      }
      Application.ThreadException += (s, e) => Log.Write("ERROR " + e.Exception);
      AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("FATAL " + e.ExceptionObject);
      Application.Run(new App(show));
    }
  }

  static void Dump(string prefix) {
    var cfg = new Settings();
    using (var data = new Collector()) {
      data.Init();
      data.Rows = cfg.Rows;
      data.AppsEveryMs = 0; // every sample refreshes the top lists
      data.Sample(true);
      Thread.Sleep(1000);
      data.Sample(true);
      var me = Process.GetCurrentProcess();

      var sb = new StringBuilder();
      sb.AppendLine("CPU " + data.Cpu.ToString("0.0") + "% · " + data.Cores.Length + " threads · " + data.CpuName);
      sb.AppendLine("cores " + string.Join(" ", Array.ConvertAll(data.Cores, v => v.ToString("0"))));
      sb.AppendLine("RAM used " + Fmt.Mb(data.RamUsed) + " avail " + Fmt.Mb(data.RamAvail) + " total " + Fmt.Mb(data.RamTotal) +
        " commit " + Fmt.Mb(data.Commit) + " / " + Fmt.Mb(data.CommitLimit));
      foreach (var g in data.Gpus) {
        sb.AppendLine("GPU" + g.Index + " " + g.FullName + " bus " + g.Bus.ToString("X2") + (g.Display ? " display" : "") +
          " util " + g.Util + "% used " + Fmt.Mb(g.Used) + " free " + Fmt.Mb(g.Free) + " reserved " + Fmt.Mb(g.Reserved) +
          " total " + Fmt.Mb(g.Total) + " " + g.Temp + "C " + g.Watts.ToString("0.0") + "W");
      }
      sb.AppendLine("top RAM:");
      foreach (var a in data.TopRam) sb.AppendLine("  " + a.Name + " x" + a.Pids.Count + "  " + Fmt.Mb(a.Ram) + "  cpu " + a.Cpu.ToString("0.00") + "%  " + a.Path);
      sb.AppendLine("top VRAM:");
      foreach (var a in data.TopVram) {
        sb.AppendLine("  " + a.Name + " x" + a.Pids.Count + "  " + Fmt.Mb(a.Vram) + "  GPU" + a.Gpu + " " + a.GpuUtil.ToString("0.0") + "%  pids " +
          string.Join(",", a.Pids));
      }
      using (var widget = new Widget(data, cfg, null)) {
        // Cost of each part, averaged over repeated runs.
        Action<string, Action> time = (label, action) => {
          action();
          var cpu = me.TotalProcessorTime;
          var sw = Stopwatch.StartNew();
          for (int i = 0; i < 40; i++) action();
          me.Refresh();
          sb.AppendLine("cost " + label + ": " + (sw.Elapsed.TotalMilliseconds / 40).ToString("0.00") + " ms wall, " +
            ((me.TotalProcessorTime - cpu).TotalMilliseconds / 40).ToString("0.00") + " ms CPU");
        };
        time("sample, widget hidden", () => data.Sample(false));
        time("sample with top lists", () => data.Sample(true));
        data.AppsEveryMs = 60000;
        time("sample, top lists not due", () => data.Sample(true));
        time("paint", () => widget.Snapshot().Dispose());
        time("tray icon", () => Widget.TrayBitmap(16, 50, 50, 50).Dispose());
        File.WriteAllText(prefix + ".txt", sb.ToString());
        using (var bmp = widget.Snapshot()) bmp.Save(prefix + ".png", ImageFormat.Png);
        cfg.Compact = true;
        using (var bmp = widget.Snapshot()) bmp.Save(prefix + "-compact.png", ImageFormat.Png);
      }
    }
  }
}

sealed class App : ApplicationContext {
  public static readonly string Dir = Path.GetDirectoryName(Application.ExecutablePath);
  const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

  readonly Settings cfg = Settings.Load();
  readonly Collector data = new Collector();
  readonly ContextMenuStrip menu;
  readonly ToolStripMenuItem miShow, miCompact, miTop, miOpacity, miAuto;
  readonly Widget widget;
  readonly NotifyIcon tray;
  readonly Timer timer;
  IntPtr trayIcon;
  int trayKey = -1;
  string lastError;

  public App(EventWaitHandle showSignal) {
    data.Init();
    data.Rows = cfg.Rows;
    data.AppsEveryMs = cfg.AppsInterval;
    data.Sample(cfg.Visible && !cfg.Compact);

    menu = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
    miShow = Item("Сховати віджет", (s, e) => SetVisible(!widget.Visible));
    miShow.Font = new Font(menu.Font, FontStyle.Bold);
    miCompact = Item("Компактний вигляд", (s, e) => SetCompact(!cfg.Compact));
    miTop = Item("Поверх усіх вікон", (s, e) => {
      cfg.TopMost = !cfg.TopMost;
      widget.TopMost = cfg.TopMost;
      cfg.Save();
    });
    miOpacity = Item("Прозорість", null);
    var drop = (ToolStripDropDownMenu)miOpacity.DropDown;
    drop.Renderer = menu.Renderer;
    drop.ShowImageMargin = false;
    drop.ShowCheckMargin = true;
    foreach (int o in new[] { 100, 95, 90, 80, 70 }) {
      int value = o;
      var it = Item(o + "%", (s, e) => {
        cfg.Opacity = value;
        widget.Opacity = value / 100.0;
        cfg.Save();
      });
      it.Tag = o;
      drop.Items.Add(it);
    }
    var miReset = Item("Повернути в правий верхній кут", (s, e) => {
      widget.ResetPosition();
      SetVisible(true);
      SavePosition();
    });
    var miTaskmgr = Item("Диспетчер завдань", (s, e) => Start("taskmgr.exe"));
    var miLog = Item("Відкрити лог", (s, e) => Start(Log.FilePath));
    miAuto = Item("Запускати разом з Windows", (s, e) => SetAutostart(!GetAutostart()));
    var miExit = Item("Вийти", (s, e) => Quit());
    menu.Items.AddRange(new ToolStripItem[] {
      miShow, miCompact, miTop, miOpacity, miReset, new ToolStripSeparator(), miTaskmgr, miLog, miAuto, new ToolStripSeparator(), miExit,
    });
    menu.Opening += (s, e) => {
      miShow.Text = widget.Visible ? "Сховати віджет" : "Показати віджет";
      miCompact.Checked = cfg.Compact;
      miTop.Checked = cfg.TopMost;
      miAuto.Checked = GetAutostart();
      foreach (ToolStripMenuItem it in drop.Items) it.Checked = (int)it.Tag == cfg.Opacity;
    };

    widget = new Widget(data, cfg, menu);
    widget.FoldClicked += (s, e) => SetCompact(!cfg.Compact);
    widget.HideClicked += (s, e) => SetVisible(false);
    widget.Moved += (s, e) => SavePosition();
    widget.PlaceInitial();
    widget.CreateControl(); // a handle to BeginInvoke on even while the widget is hidden

    tray = new NotifyIcon { ContextMenuStrip = menu, Text = "ArtMeter" };
    tray.MouseClick += (s, e) => {
      if (e.Button == MouseButtons.Left) SetVisible(!widget.Visible);
    };
    UpdateTray();
    tray.Visible = true;

    if (cfg.FirstRun) {
      SetAutostart(true);
      cfg.Save();
    }
    if (cfg.Visible) widget.ShowWidget();

    timer = new Timer { Interval = Math.Max(250, cfg.Interval) };
    timer.Tick += Tick;
    timer.Start();
    ThreadPool.RegisterWaitForSingleObject(showSignal, (state, timedOut) => widget.BeginInvoke((MethodInvoker)(() => SetVisible(true))), null, -1, false);
    Log.Write("started: " + data.CpuName + ", " + data.Cores.Length + " threads, " + data.Gpus.Count + " NVIDIA GPU(s), driver " + data.Driver);
  }

  static ToolStripMenuItem Item(string text, EventHandler click) {
    var it = new ToolStripMenuItem(text);
    if (click != null) it.Click += click;
    return it;
  }

  static void Start(string what) {
    try {
      Process.Start(what);
    } catch (Exception e) {
      Log.Write("cannot open " + what + ": " + e.Message);
    }
  }

  void Tick(object sender, EventArgs e) {
    try {
      data.Sample(widget.Visible && !cfg.Compact);
      if (widget.Visible) widget.UpdateView();
      UpdateTray();
    } catch (Exception ex) {
      string msg = ex.ToString();
      if (msg != lastError) Log.Write("ERROR " + msg);
      lastError = msg;
    }
  }

  void SetVisible(bool visible) {
    cfg.Visible = visible;
    if (visible) {
      if (!cfg.Compact) data.Sample(true); // the top lists are stale while hidden
      widget.ShowWidget();
    } else {
      widget.Hide();
    }
    cfg.Save();
  }

  void SetCompact(bool compact) {
    cfg.Compact = compact;
    if (!compact) data.Sample(true);
    widget.UpdateView();
    SavePosition();
  }

  void SavePosition() {
    cfg.X = widget.Left;
    cfg.Y = widget.Top;
    cfg.Save();
  }

  void UpdateTray() {
    double vram = 0;
    var text = new StringBuilder("CPU " + Fmt.Pct(data.Cpu) + "  RAM " + Fmt.Gb0(data.RamUsed) + "/" + Fmt.Gb0(data.RamTotal) + " ГБ");
    foreach (var g in data.Gpus) {
      vram = Math.Max(vram, g.MemPercent);
      text.Append("\nGPU" + g.Index + " " + Fmt.Pct(g.Util) + "  " + Fmt.Gb1(g.Used) + "/" + Fmt.Gb0(g.Total));
    }
    tray.Text = text.Length > 63 ? text.ToString(0, 63) : text.ToString(); // NotifyIcon refuses longer tooltips

    int size = SystemInformation.SmallIconSize.Width, h = size - 2 * (int)Math.Round(size / 16f);
    int key = Widget.TrayLevel(data.Cpu, h) | Widget.TrayLevel(data.RamPercent, h) << 8 | Widget.TrayLevel(vram, h) << 16 |
      (data.RamPercent >= 90 ? 1 << 24 : 0) | (vram >= 90 ? 1 << 25 : 0);
    if (key == trayKey) return;
    trayKey = key;
    using (var bmp = Widget.TrayBitmap(size, data.Cpu, data.RamPercent, vram)) {
      IntPtr handle = bmp.GetHicon();
      tray.Icon = Icon.FromHandle(handle);
      if (trayIcon != IntPtr.Zero) Native.DestroyIcon(trayIcon);
      trayIcon = handle;
    }
  }

  static bool GetAutostart() {
    using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) return key != null && key.GetValue("ArtMeter") != null;
  }

  static void SetAutostart(bool on) {
    try {
      using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) {
        if (on) key.SetValue("ArtMeter", "\"" + Application.ExecutablePath + "\"");
        else key.DeleteValue("ArtMeter", false);
      }
      Log.Write("autostart " + (on ? "on" : "off"));
    } catch (Exception e) {
      Log.Write("autostart: " + e.Message);
    }
  }

  void Quit() {
    timer.Stop();
    tray.Visible = false;
    tray.Dispose();
    widget.Close();
    data.Dispose();
    if (trayIcon != IntPtr.Zero) Native.DestroyIcon(trayIcon);
    Log.Write("exit");
    ExitThread();
  }
}

sealed class Settings {
  public int X = int.MinValue, Y = int.MinValue, Opacity = 100, Rows = 5, Interval = 1000, AppsInterval = 2000;
  public bool Visible = true, Compact, TopMost = true, FirstRun = true;

  static string FilePath { get { return Path.Combine(App.Dir, "config.json"); } }

  public static Settings Load() {
    var s = new Settings();
    try {
      if (!File.Exists(FilePath)) return s;
      s.FirstRun = false;
      foreach (Match m in Regex.Matches(File.ReadAllText(FilePath), "\"(\\w+)\"\\s*:\\s*(-?\\d+|true|false)")) {
        string v = m.Groups[2].Value;
        switch (m.Groups[1].Value) {
          case "x": s.X = int.Parse(v); break;
          case "y": s.Y = int.Parse(v); break;
          case "opacity": s.Opacity = Math.Max(30, Math.Min(100, int.Parse(v))); break;
          case "rows": s.Rows = Math.Max(1, Math.Min(15, int.Parse(v))); break;
          case "intervalMs": s.Interval = int.Parse(v); break;
          case "appsIntervalMs": s.AppsInterval = int.Parse(v); break;
          case "visible": s.Visible = v == "true"; break;
          case "compact": s.Compact = v == "true"; break;
          case "topMost": s.TopMost = v == "true"; break;
        }
      }
    } catch (Exception e) {
      Log.Write("config.json: " + e.Message);
    }
    return s;
  }

  public void Save() {
    FirstRun = false;
    var sb = new StringBuilder("{\n");
    if (X != int.MinValue) sb.Append("  \"x\": " + X + ",\n  \"y\": " + Y + ",\n");
    sb.Append("  \"visible\": " + Bool(Visible) + ",\n");
    sb.Append("  \"compact\": " + Bool(Compact) + ",\n");
    sb.Append("  \"topMost\": " + Bool(TopMost) + ",\n");
    sb.Append("  \"opacity\": " + Opacity + ",\n");
    sb.Append("  \"rows\": " + Rows + ",\n");
    sb.Append("  \"intervalMs\": " + Interval + ",\n");
    sb.Append("  \"appsIntervalMs\": " + AppsInterval + "\n}\n");
    try {
      File.WriteAllText(FilePath, sb.ToString());
    } catch (Exception e) {
      Log.Write("config.json: " + e.Message);
    }
  }

  static string Bool(bool b) { return b ? "true" : "false"; }
}

static class Log {
  public static readonly string FilePath = Path.Combine(App.Dir, "artmeter.log");

  public static void Write(string msg) {
    try {
      var fi = new FileInfo(FilePath);
      if (fi.Exists && fi.Length > 256 * 1024) {
        File.Delete(FilePath + ".old");
        File.Move(FilePath, FilePath + ".old");
      }
      File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + Environment.NewLine);
    } catch {
      // logging is best effort
    }
  }
}

// Dark tray / right-click menu to match the widget.
sealed class DarkColors : ProfessionalColorTable {
  public override Color ToolStripDropDownBackground { get { return Theme.Panel; } }
  public override Color ImageMarginGradientBegin { get { return Theme.Panel; } }
  public override Color ImageMarginGradientMiddle { get { return Theme.Panel; } }
  public override Color ImageMarginGradientEnd { get { return Theme.Panel; } }
  public override Color MenuBorder { get { return Theme.Line; } }
  public override Color MenuItemBorder { get { return Theme.Hi; } }
  public override Color MenuItemSelected { get { return Theme.Hi; } }
  public override Color MenuItemSelectedGradientBegin { get { return Theme.Hi; } }
  public override Color MenuItemSelectedGradientEnd { get { return Theme.Hi; } }
  public override Color MenuItemPressedGradientBegin { get { return Theme.Hi; } }
  public override Color MenuItemPressedGradientEnd { get { return Theme.Hi; } }
  public override Color SeparatorDark { get { return Theme.Line; } }
  public override Color SeparatorLight { get { return Theme.Line; } }
  public override Color CheckBackground { get { return Theme.Panel; } }
  public override Color CheckSelectedBackground { get { return Theme.Hi; } }
  public override Color CheckPressedBackground { get { return Theme.Hi; } }
}

sealed class DarkRenderer : ToolStripProfessionalRenderer {
  public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

  protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
    e.TextColor = Theme.Text;
    base.OnRenderItemText(e);
  }

  protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) {
    e.ArrowColor = Theme.Dim;
    base.OnRenderArrow(e);
  }

  protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) {
    var r = e.ImageRectangle;
    float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
    using (var pen = new Pen(Theme.Accent, 2f)) {
      e.Graphics.DrawLines(pen, new[] { new PointF(cx - 4, cy), new PointF(cx - 1, cy + 3), new PointF(cx + 4, cy - 3) });
    }
  }
}
