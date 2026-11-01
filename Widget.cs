// The always-on-top panel: CPU with a per-thread grid, RAM, each GPU, and the programs using the most RAM and VRAM.
// Painted by hand once a second (GDI+ shapes, GDI text), never takes focus, drags from anywhere and snaps to screen edges.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Theme {
  public static readonly Color Bg = Hex("#15171c"), Panel = Hex("#1c1f26"), Line = Hex("#2a2e37"), Hi = Hex("#282c35"),
    Track = Hex("#262a33"), Text = Hex("#e7e9ee"), Dim = Hex("#8b91a0"), Accent = Hex("#ff5a4e"),
    Cpu = Hex("#5aa9ff"), Ram = Hex("#b48cff"), Gpu = Hex("#3ddc97");

  static Color Hex(string h) { return ColorTranslator.FromHtml(h); }

  public static Color Mix(Color a, Color b, double t) {
    t = Math.Max(0, Math.Min(1, t));
    return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
  }

  // Memory bars turn red from 90%: that is when Nuke or a render starts swapping or runs out of VRAM.
  public static Color Fill(Color c, double pct) { return pct >= 90 ? Accent : c; }
}

static class Fmt {
  const double GB = 1 << 30, MB = 1 << 20;

  public static string Pct(double v) { return Math.Round(v).ToString("0") + "%"; }
  public static string Gb1(double b) { return (b / GB).ToString("0.0"); }
  public static string Gb0(double b) { return (b / GB).ToString(b >= 100 * GB ? "0" : "0.#"); }
  public static string Bytes(double b) { return b >= GB ? (b / GB).ToString(b >= 100 * GB ? "0" : "0.0") + " ГБ" : (b / MB).ToString("0") + " МБ"; }
  public static string Mb(double b) { return (b / MB).ToString("#,0") + " МБ"; }
  public static string Cpu(double v) { return v < 0.05 ? "" : v < 10 ? v.ToString("0.0") + "%" : v.ToString("0") + "%"; }
}

sealed class Widget : Form {
  const int W = 320, WCompact = 252, Pad = 12, HeaderH = 30, BlockH = 56, BlockGap = 14, RowH = 20, CompactRowH = 22;

  readonly Collector data;
  readonly Settings cfg;
  readonly ContextMenuStrip menu;
  readonly ToolTip tip = new ToolTip { OwnerDraw = true, InitialDelay = 500, ReshowDelay = 100, AutoPopDelay = 30000 };
  readonly List<KeyValuePair<Rectangle, string>> hints = new List<KeyValuePair<Rectangle, string>>();
  readonly Font fTitle, fLabel, fBig, fUnit, fSmall, fRow, fCaps;
  readonly SolidBrush brush = new SolidBrush(Color.Black);
  readonly float k, dpi;
  Rectangle btnFold, btnHide;
  int hot; // 1 fold, 2 hide
  string tipText;

  public event EventHandler FoldClicked, HideClicked, Moved;

  public Widget(Collector data, Settings cfg, ContextMenuStrip menu) {
    this.data = data;
    this.cfg = cfg;
    this.menu = menu;
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    StartPosition = FormStartPosition.Manual;
    BackColor = Theme.Bg;
    Text = "ArtMeter";
    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    using (var g = CreateGraphics()) dpi = g.DpiY;
    k = dpi / 96f;
    fTitle = new Font("Segoe UI Semibold", 9f);
    fLabel = new Font("Segoe UI", 8.25f);
    fBig = new Font("Segoe UI Semibold", 13f);
    fUnit = new Font("Segoe UI", 9f);
    fSmall = new Font("Segoe UI", 8.25f);
    fRow = new Font("Segoe UI", 8.75f);
    fCaps = new Font("Segoe UI Semibold", 7.5f);
    try {
      Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
    } catch {
      // the default icon is fine
    }
    tip.Popup += (s, e) => {
      var sz = TextRenderer.MeasureText(tipText ?? "", fSmall, Size.Empty, TextFormatFlags.NoPrefix);
      e.ToolTipSize = new Size(sz.Width + S(18), sz.Height + S(12));
    };
    tip.Draw += (s, e) => {
      e.Graphics.Clear(Theme.Panel);
      using (var pen = new Pen(Theme.Line)) e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
      TextRenderer.DrawText(e.Graphics, e.ToolTipText, fSmall, new Point(S(9), S(6)), Theme.Text, TextFormatFlags.NoPrefix);
    };
  }

  int S(float v) { return (int)Math.Round(v * k); }

  protected override CreateParams CreateParams {
    get {
      var cp = base.CreateParams;
      cp.ExStyle |= 0x80 | 0x08000000; // WS_EX_TOOLWINDOW (no Alt+Tab), WS_EX_NOACTIVATE (clicks don't steal focus from Nuke)
      return cp;
    }
  }

  protected override bool ShowWithoutActivation { get { return true; } }

  protected override void OnHandleCreated(EventArgs e) {
    base.OnHandleCreated(e);
    int round = 2, border = ColorTranslator.ToWin32(Theme.Line);
    Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
    Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref border, 4);
  }

  // ---------- placement ----------

  public Size Measure() {
    int gpus = data.Gpus.Count;
    if (cfg.Compact) return new Size(S(WCompact), S(HeaderH + 6 + (2 + gpus) * CompactRowH + 6));
    int h = HeaderH + 10;
    h += 30 + GridH() + BlockGap;
    h += (1 + gpus) * (BlockH + BlockGap);
    h += 2 * (22 + cfg.Rows * RowH) + 8 + 6;
    return new Size(S(W), S(h));
  }

  int GridRows() { return data.Cores.Length > 32 ? 2 : 1; }
  int GridH() { return GridRows() * 8 - 2; }

  public void PlaceInitial() {
    var size = Measure();
    var wa = Screen.PrimaryScreen.WorkingArea;
    var at = new Point(wa.Right - size.Width - S(12), wa.Top + S(12));
    if (cfg.X != int.MinValue) {
      var saved = new Rectangle(cfg.X, cfg.Y, size.Width, S(HeaderH));
      foreach (var s in Screen.AllScreens) {
        if (s.WorkingArea.IntersectsWith(saved)) {
          at = saved.Location;
          break;
        }
      }
    }
    Bounds = new Rectangle(at, size);
  }

  public void ResetPosition() {
    cfg.X = int.MinValue;
    PlaceInitial();
  }

  public void ShowWidget() {
    TopMost = cfg.TopMost;
    Opacity = cfg.Opacity / 100.0;
    UpdateView();
    if (!Visible) Show();
  }

  // Resizes for the current mode, keeping whichever corner is nearest the screen edge in place, and repaints.
  public void UpdateView() {
    var want = Measure();
    if (Size != want) {
      var wa = Screen.FromControl(this).WorkingArea;
      var b = Bounds;
      int x = b.X + b.Width / 2 > wa.X + wa.Width / 2 ? b.Right - want.Width : b.X;
      int y = b.Y + b.Height / 2 > wa.Y + wa.Height / 2 ? b.Bottom - want.Height : b.Y;
      Bounds = new Rectangle(x, y, want.Width, want.Height);
    }
    Invalidate();
  }

  protected override void WndProc(ref Message m) {
    if (m.Msg == Native.WM_MOVING) {
      Snap(m.LParam);
      m.Result = (IntPtr)1;
      return;
    }
    base.WndProc(ref m);
    if (m.Msg == Native.WM_EXITSIZEMOVE && Moved != null) Moved(this, EventArgs.Empty);
  }

  void Snap(IntPtr lParam) {
    var r = (Native.RECT)Marshal.PtrToStructure(lParam, typeof(Native.RECT));
    var wa = Screen.FromRectangle(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)).WorkingArea;
    int d = S(14), dx = 0, dy = 0;
    if (Math.Abs(r.Left - wa.Left) < d) dx = wa.Left - r.Left;
    else if (Math.Abs(r.Right - wa.Right) < d) dx = wa.Right - r.Right;
    if (Math.Abs(r.Top - wa.Top) < d) dy = wa.Top - r.Top;
    else if (Math.Abs(r.Bottom - wa.Bottom) < d) dy = wa.Bottom - r.Bottom;
    r.Left += dx;
    r.Right += dx;
    r.Top += dy;
    r.Bottom += dy;
    Marshal.StructureToPtr(r, lParam, false);
  }

  // ---------- mouse ----------

  protected override void OnMouseMove(MouseEventArgs e) {
    base.OnMouseMove(e);
    int h = btnFold.Contains(e.Location) ? 1 : btnHide.Contains(e.Location) ? 2 : 0;
    if (h != hot) {
      hot = h;
      Invalidate(new Rectangle(0, 0, Width, S(HeaderH)));
    }
    string t = null;
    foreach (var hint in hints) {
      if (hint.Key.Contains(e.Location)) {
        t = hint.Value;
        break;
      }
    }
    if (t != tipText) {
      tipText = t;
      tip.SetToolTip(this, t);
    }
  }

  protected override void OnMouseLeave(EventArgs e) {
    base.OnMouseLeave(e);
    if (hot != 0) {
      hot = 0;
      Invalidate(new Rectangle(0, 0, Width, S(HeaderH)));
    }
  }

  protected override void OnMouseDown(MouseEventArgs e) {
    base.OnMouseDown(e);
    if (e.Button != MouseButtons.Left || btnFold.Contains(e.Location) || btnHide.Contains(e.Location)) return;
    tip.Hide(this);
    Native.ReleaseCapture();
    Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
  }

  protected override void OnMouseUp(MouseEventArgs e) {
    base.OnMouseUp(e);
    if (e.Button == MouseButtons.Right && menu != null) menu.Show(Cursor.Position);
    if (e.Button != MouseButtons.Left) return;
    EventHandler handler = btnFold.Contains(e.Location) ? FoldClicked : btnHide.Contains(e.Location) ? HideClicked : null;
    if (handler != null) handler(this, EventArgs.Empty);
  }

  // ---------- painting ----------

  protected override void OnPaint(PaintEventArgs e) {
    Render(e.Graphics, ClientSize);
  }

  public Bitmap Snapshot() {
    var size = Measure();
    var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
    using (var g = Graphics.FromImage(bmp)) Render(g, size);
    return bmp;
  }

  void Render(Graphics g, Size size) {
    hints.Clear();
    g.Clear(Theme.Bg);
    int w = size.Width, hh = S(HeaderH);
    Fill(g, Theme.Panel, new Rectangle(0, 0, w, hh));
    Fill(g, Theme.Line, new Rectangle(0, hh - 1, w, 1));
    Draw("ArtMeter", fTitle, Theme.Text, S(Pad), Mid(fTitle, 0, hh));
    btnHide = new Rectangle(w - hh, 0, hh, hh);
    btnFold = new Rectangle(w - 2 * hh, 0, hh, hh);
    HeaderButton(g, btnFold, hot == 1, cfg.Compact ? 'v' : '^');
    HeaderButton(g, btnHide, hot == 2, 'x');
    Hint(btnFold, cfg.Compact ? "Розгорнути" : "Згорнути до компактного вигляду");
    Hint(btnHide, "Сховати віджет (ArtMeter лишається в треї)");
    if (cfg.Compact) RenderCompact(g, w, hh);
    else RenderFull(g, w, hh);
    FlushText(g);
  }

  void HeaderButton(Graphics g, Rectangle r, bool isHot, char glyph) {
    if (isHot) Fill(g, Theme.Hi, r);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, a = 4 * k;
    using (var pen = new Pen(isHot ? Theme.Text : Theme.Dim, 1.5f * k)) {
      if (glyph == 'x') {
        g.DrawLine(pen, cx - a, cy - a, cx + a, cy + a);
        g.DrawLine(pen, cx - a, cy + a, cx + a, cy - a);
      } else {
        float s = glyph == '^' ? -1 : 1;
        g.DrawLines(pen, new[] { new PointF(cx - a, cy - s * a / 2), new PointF(cx, cy + s * a / 2), new PointF(cx + a, cy - s * a / 2) });
      }
    }
    g.SmoothingMode = SmoothingMode.None;
  }

  void RenderFull(Graphics g, int w, int hh) {
    int x0 = S(Pad), inner = w - 2 * x0, y = hh + S(10);

    BlockHead(g, y, w, "CPU", Fmt.Pct(data.Cpu), Theme.Text, null, null, data.CpuHistory, Theme.Cpu);
    Hint(new Rectangle(0, y, w, S(30) + S(GridH())), data.CpuName + "\n" + data.Cores.Length +
      " логічних процесорів, кожна клітинка — один потік.\nЗавантаження рахується як у Диспетчері завдань.");
    CoreGrid(g, x0, y + S(30), inner);
    y += S(30) + S(GridH()) + S(BlockGap);

    BlockHead(g, y, w, "RAM", Fmt.Gb1(data.RamUsed), Theme.Text, " / " + Fmt.Gb0(data.RamTotal) + " ГБ", fUnit, data.RamHistory, Theme.Ram);
    Bar(g, new Rectangle(x0, y + S(30), inner, S(6)), data.RamPercent, Theme.Ram);
    int cy = y + S(40), ch = S(16);
    Draw("доступно " + Fmt.Gb1(data.RamAvail) + " ГБ", fSmall, Theme.Dim, x0, Mid(fSmall, cy, ch));
    DrawRight(Fmt.Pct(data.RamPercent), fSmall, Theme.Dim, w - x0, Mid(fSmall, cy, ch));
    Hint(new Rectangle(0, y, w, S(BlockH)), "Використовується " + Fmt.Gb1(data.RamUsed) + " з " + Fmt.Gb1(data.RamTotal) +
      " ГБ (як «Використовується» в Диспетчері завдань)\nДоступно " + Fmt.Gb1(data.RamAvail) + " ГБ · виділено (commit) " +
      Fmt.Gb0(data.Commit) + " з " + Fmt.Gb0(data.CommitLimit) + " ГБ");
    y += S(BlockH + BlockGap);

    foreach (var gpu in data.Gpus) {
      BlockHead(g, y, w, "GPU " + gpu.Index, Fmt.Pct(gpu.Util), Theme.Text, "  " + gpu.Name + (gpu.Display ? " · екран" : ""), fSmall, gpu.UtilHistory, Theme.Gpu);
      Bar(g, new Rectangle(x0, y + S(30), inner, S(6)), gpu.MemPercent, Theme.Gpu);
      cy = y + S(40);
      int bl = Mid(fSmall, cy, ch);
      int x = x0 + Draw("VRAM ", fSmall, Theme.Dim, x0, bl);
      Draw(Fmt.Gb1(gpu.Used) + " / " + Fmt.Gb0(gpu.Total) + " ГБ", fSmall, Theme.Fill(Theme.Text, gpu.MemPercent), x, bl);
      string right = "вільно " + Fmt.Gb1(gpu.Free);
      if (gpu.Temp >= 0) right += " · " + gpu.Temp + "°";
      if (gpu.Watts >= 0) right += " · " + Math.Round(gpu.Watts) + " Вт";
      DrawRight(right, fSmall, Theme.Dim, w - x0, bl);
      Hint(new Rectangle(0, y, w, S(BlockH)), gpu.FullName + " · PCI " + gpu.Bus.ToString("X2") + ":00.0 · драйвер " + data.Driver +
        (gpu.Display ? "\nДо цієї карти під'єднано монітор." : "") +
        "\nVRAM: зайнято " + Fmt.Mb(gpu.Used) + ", вільно " + Fmt.Mb(gpu.Free) + ", резерв драйвера " + Fmt.Mb(gpu.Reserved) +
        "\nЗавантаження і пам'ять — ті самі числа, що в nvidia-smi.");
      y += S(BlockH + BlockGap);
    }

    y = AppList(g, y, w, "ТОП RAM", "CPU", data.TopRam, true, Theme.Ram);
    y += S(8);
    AppList(g, y, w, "ТОП VRAM", data.Gpus.Count > 1 ? "GPU" : "", data.TopVram, false, Theme.Gpu);
  }

  // Label, big value with an optional unit after it, and a sparkline on the right.
  void BlockHead(Graphics g, int y, int w, string label, string value, Color valueColor, string unit, Font unitFont, History hist, Color color) {
    int x0 = S(Pad), sparkW = S(92), sparkX = w - x0 - sparkW, bl = y + S(19);
    Draw(label, fLabel, Theme.Dim, x0, bl);
    int x = x0 + S(44);
    x += Draw(value, fBig, valueColor, x, bl);
    if (unit != null) Draw(unit, unitFont, Theme.Dim, x, bl, sparkX - S(8) - x);
    Spark(g, new Rectangle(sparkX, y + S(3), sparkW, S(21)), hist, color);
  }

  void CoreGrid(Graphics g, int x0, int y, int inner) {
    int n = data.Cores.Length;
    if (n == 0) return;
    int rows = GridRows(), perRow = (n + rows - 1) / rows, gap = S(2), ch = S(6);
    int cw = Math.Max(1, (inner + gap) / perRow - gap);
    int gx = x0 + (inner - (perRow * (cw + gap) - gap)) / 2;
    for (int i = 0; i < n; i++) {
      double v = data.Cores[i];
      brush.Color = v < 0.5 ? Theme.Track : Theme.Mix(Theme.Track, Theme.Cpu, 0.18 + 0.82 * v / 100);
      g.FillRectangle(brush, gx + i % perRow * (cw + gap), y + i / perRow * (ch + gap), cw, ch);
    }
  }

  int AppList(Graphics g, int y, int w, string title, string column, List<AppRow> rows, bool ram, Color color) {
    int x0 = S(Pad), right = w - x0, hy = S(22);
    int bl = Mid(fCaps, y, hy);
    int tw = Draw(title, fCaps, Theme.Dim, x0, bl);
    int colW = column.Length > 0 ? DrawRight(column, fCaps, Theme.Dim, right - S(6), bl) + S(14) : S(6);
    Fill(g, Theme.Line, new Rectangle(x0 + tw + S(8), y + hy / 2, right - colW - (x0 + tw + S(8)), 1));
    y += hy;

    int valRight = right - S(72), rh = S(RowH);
    double max = rows.Count > 0 ? Math.Max(1, ram ? rows[0].Ram : rows[0].Vram) : 1;
    for (int i = 0; i < cfg.Rows; i++) {
      var r = new Rectangle(x0, y + i * rh, right - x0, rh);
      int rbl = Mid(fRow, r.Y, rh);
      if (i >= rows.Count) {
        if (i == 0) Draw("—", fRow, Theme.Dim, r.X + S(6), rbl);
        continue;
      }
      var a = rows[i];
      double v = ram ? a.Ram : a.Vram;
      g.SmoothingMode = SmoothingMode.AntiAlias;
      brush.Color = Color.FromArgb(34, color);
      using (var path = Round(new Rectangle(r.X, r.Y + S(2), Math.Max(S(6), (int)(r.Width * v / max)), rh - S(4)), S(3))) g.FillPath(brush, path);
      g.SmoothingMode = SmoothingMode.None;

      DrawRight(Fmt.Bytes(v), fRow, Theme.Text, valRight, rbl);
      string col = ram ? Fmt.Cpu(a.Cpu) : ((data.Gpus.Count > 1 ? "GPU" + a.Gpu : "") + (a.GpuUtil >= 1 ? "  " + Fmt.Pct(a.GpuUtil) : "")).Trim();
      DrawRight(col, fRow, Theme.Dim, right - S(6), rbl);

      string count = a.Pids.Count > 1 ? " ×" + a.Pids.Count : "";
      int nameX = r.X + S(6), nameMax = valRight - S(60) - nameX;
      int cw = count.Length > 0 ? TextW(count, fSmall) : 0;
      int nw = Math.Min(TextW(a.Name, fRow), nameMax - cw);
      Draw(a.Name, fRow, Theme.Text, nameX, rbl, nw);
      if (cw > 0) Draw(count, fSmall, Theme.Dim, nameX + nw, rbl);

      string t = a.Path + "\n" + (a.Pids.Count == 1 ? "PID " + a.Pids[0] : a.Pids.Count + " процесів") + " · RAM " + Fmt.Bytes(a.Ram);
      if (a.Vram > 0) t += " · VRAM " + Fmt.Bytes(a.Vram);
      if (a.Cpu >= 0.05) t += " · CPU " + Fmt.Cpu(a.Cpu);
      if (a.Vram > 0 && a.VramOn.Length > 1) {
        t += "\n";
        for (int gi = 0; gi < a.VramOn.Length; gi++) t += (gi > 0 ? " · " : "") + "GPU" + gi + ": " + Fmt.Bytes(a.VramOn[gi]) + ", " + Fmt.Pct(Math.Min(100, a.UtilOn[gi]));
      }
      Hint(r, t);
    }
    return y + cfg.Rows * rh;
  }

  void RenderCompact(Graphics g, int w, int hh) {
    int y = hh + S(6);
    CompactRow(g, y, w, "CPU", data.Cpu, Theme.Cpu, Fmt.Pct(data.Cpu), false);
    y += S(CompactRowH);
    CompactRow(g, y, w, "RAM", data.RamPercent, Theme.Ram, Fmt.Gb1(data.RamUsed) + " / " + Fmt.Gb0(data.RamTotal) + " ГБ", true);
    y += S(CompactRowH);
    foreach (var gpu in data.Gpus) {
      CompactRow(g, y, w, "GPU" + gpu.Index, gpu.MemPercent, Theme.Gpu, Fmt.Gb1(gpu.Used) + "/" + Fmt.Gb0(gpu.Total) + " ГБ · " + Fmt.Pct(gpu.Util), true);
      Hint(new Rectangle(0, y, w, S(CompactRowH)), gpu.FullName + (gpu.Display ? " (екран)" : "") + "\nсмужка — зайнята VRAM, відсоток — завантаження GPU");
      y += S(CompactRowH);
    }
  }

  void CompactRow(Graphics g, int y, int w, string label, double pct, Color color, string text, bool memory) {
    int x0 = S(Pad), h = S(CompactRowH);
    Draw(label, fLabel, Theme.Dim, x0, Mid(fLabel, y, h));
    Bar(g, new Rectangle(x0 + S(40), y + (h - S(6)) / 2, S(84), S(6)), pct, color, memory);
    DrawRight(text, fSmall, Theme.Text, w - x0, Mid(fSmall, y, h));
  }

  void Bar(Graphics g, Rectangle r, double pct, Color color, bool memory = true) {
    g.SmoothingMode = SmoothingMode.AntiAlias;
    brush.Color = Theme.Track;
    using (var path = Round(r, r.Height / 2)) g.FillPath(brush, path);
    if (pct > 0.2) {
      brush.Color = memory ? Theme.Fill(color, pct) : color;
      int fw = Math.Max(r.Height, (int)Math.Round(r.Width * Math.Min(100, pct) / 100));
      using (var path = Round(new Rectangle(r.X, r.Y, fw, r.Height), r.Height / 2)) g.FillPath(brush, path);
    }
    g.SmoothingMode = SmoothingMode.None;
  }

  void Spark(Graphics g, Rectangle r, History h, Color color) {
    Fill(g, Theme.Track, new Rectangle(r.X, r.Bottom - 1, r.Width, 1));
    if (h.Count < 2) return;
    float step = (float)r.Width / (h.Length - 1);
    var pts = new PointF[h.Count + 2];
    for (int i = 0; i < h.Count; i++) {
      float v = Math.Max(0, Math.Min(100, h[i]));
      pts[i] = new PointF(r.Right - (h.Count - 1 - i) * step, r.Bottom - 1 - v / 100f * (r.Height - 2));
    }
    pts[h.Count] = new PointF(pts[h.Count - 1].X, r.Bottom);
    pts[h.Count + 1] = new PointF(pts[0].X, r.Bottom);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    brush.Color = Color.FromArgb(48, color);
    g.FillPolygon(brush, pts);
    var line = new PointF[h.Count];
    Array.Copy(pts, line, h.Count);
    using (var pen = new Pen(color, 1.25f * k)) g.DrawLines(pen, line);
    g.SmoothingMode = SmoothingMode.None;
  }

  // ---------- drawing helpers ----------

  void Hint(Rectangle r, string text) { hints.Add(new KeyValuePair<Rectangle, string>(r, text)); }

  void Fill(Graphics g, Color c, Rectangle r) {
    brush.Color = c;
    g.FillRectangle(brush, r);
  }

  static GraphicsPath Round(Rectangle r, int radius) {
    var p = new GraphicsPath();
    int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
    if (d < 2) {
      p.AddRectangle(r);
      return p;
    }
    p.AddArc(r.X, r.Y, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
  }

  // ---------- text: measured on a memory DC, queued, then drawn in one GDI pass ----------
  // TextRenderer switches between GDI+ and GDI on every call, which made a frame cost ~30 ms; this costs ~1 ms.

  struct TextOp {
    public string S;
    public IntPtr Font;
    public int Color, X, Top;
  }

  readonly List<TextOp> ops = new List<TextOp>();
  readonly Dictionary<Font, IntPtr> hfonts = new Dictionary<Font, IntPtr>();
  readonly Dictionary<Font, int> ascents = new Dictionary<Font, int>();
  IntPtr measureDc;

  IntPtr Select(Font f) {
    IntPtr h;
    if (!hfonts.TryGetValue(f, out h)) {
      h = f.ToHfont();
      hfonts[f] = h;
      if (measureDc == IntPtr.Zero) measureDc = Native.CreateCompatibleDC(IntPtr.Zero);
      Native.SelectObject(measureDc, h);
      Native.TEXTMETRIC tm;
      Native.GetTextMetricsW(measureDc, out tm);
      ascents[f] = tm.tmAscent;
    }
    Native.SelectObject(measureDc, h);
    return h;
  }

  float Em(Font f) { return f.SizeInPoints * dpi / 72f; }

  // Baseline that centres the cap height of f in the band [y, y + h).
  int Mid(Font f, int y, int h) { return y + (int)Math.Round(h / 2f + Em(f) * 0.35f); }

  int TextW(string s, Font f) {
    Select(f);
    Native.SIZE size;
    Native.GetTextExtentPoint32W(measureDc, s, s.Length, out size);
    return size.cx;
  }

  // Queues s with its baseline at `baseline`, cut with an ellipsis to maxW when given; returns the width used.
  int Draw(string s, Font f, Color c, int x, int baseline, int maxW = 0) {
    if (string.IsNullOrEmpty(s)) return 0;
    int tw = TextW(s, f);
    if (maxW > 0 && tw > maxW) {
      int fit;
      Native.SIZE size;
      int room = Math.Max(0, maxW - TextW("…", f));
      Native.GetTextExtentExPointW(measureDc, s, s.Length, room, out fit, null, out size);
      s = s.Substring(0, fit).TrimEnd() + "…";
      tw = TextW(s, f);
    }
    ops.Add(new TextOp { S = s, Font = hfonts[f], Color = ColorTranslator.ToWin32(c), X = x, Top = baseline - ascents[f] });
    return tw;
  }

  int DrawRight(string s, Font f, Color c, int right, int baseline) {
    if (string.IsNullOrEmpty(s)) return 0;
    int tw = TextW(s, f);
    Draw(s, f, c, right - tw, baseline);
    return tw;
  }

  void FlushText(Graphics g) {
    IntPtr hdc = g.GetHdc();
    try {
      Native.SetBkMode(hdc, 1); // transparent
      IntPtr old = IntPtr.Zero;
      foreach (var op in ops) {
        IntPtr prev = Native.SelectObject(hdc, op.Font);
        if (old == IntPtr.Zero) old = prev;
        Native.SetTextColor(hdc, op.Color);
        Native.ExtTextOutW(hdc, op.X, op.Top, 0, IntPtr.Zero, op.S, (uint)op.S.Length, IntPtr.Zero);
      }
      if (old != IntPtr.Zero) Native.SelectObject(hdc, old);
    } finally {
      g.ReleaseHdc(hdc);
      ops.Clear();
    }
  }

  protected override void Dispose(bool disposing) {
    foreach (var h in hfonts.Values) Native.DeleteObject(h);
    hfonts.Clear();
    if (measureDc != IntPtr.Zero) Native.DeleteDC(measureDc);
    measureDc = IntPtr.Zero;
    base.Dispose(disposing);
  }

  // Tray icon: three bars, CPU / RAM / the fullest GPU's VRAM; memory bars go red from 90%.
  public static Bitmap TrayBitmap(int size, double cpu, double ram, double vram) {
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    float u = size / 16f;
    double[] values = { cpu, ram, vram };
    Color[] colors = { Theme.Cpu, Theme.Ram, Theme.Gpu };
    using (var g = Graphics.FromImage(bmp))
    using (var track = new SolidBrush(Color.FromArgb(80, 255, 255, 255)))
    using (var fill = new SolidBrush(Color.Black)) {
      g.Clear(Color.Transparent);
      int top = (int)Math.Round(u), h = size - 2 * top, bw = Math.Max(1, (int)Math.Round(4 * u));
      for (int i = 0; i < 3; i++) {
        int x = (int)Math.Round(i * 6 * u);
        g.FillRectangle(track, x, top, bw, h);
        int fh = TrayLevel(values[i], h);
        fill.Color = i == 0 ? colors[i] : Theme.Fill(colors[i], values[i]);
        g.FillRectangle(fill, x, top + h - fh, bw, fh);
      }
    }
    return bmp;
  }

  public static int TrayLevel(double pct, int h) {
    int fh = (int)Math.Round(h * Math.Max(0, Math.Min(100, pct)) / 100);
    return pct > 0.5 ? Math.Max(1, fh) : fh;
  }
}
