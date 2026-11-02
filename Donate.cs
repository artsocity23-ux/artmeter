// "Підтримати автора": crypto addresses with a QR code each (for a phone wallet) and Ko-fi / Buy Me a Coffee / PayPal links.
// The list is donate.json, embedded into the exe by build.cmd. Entries with an empty address or url are not shown, and
// with nothing filled in the tray menu has no donate item at all.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

sealed class DonateInfo {
  public sealed class Coin {
    public string Name, Network, Address;
  }

  public sealed class Link {
    public string Name, Url;
  }

  public readonly List<Coin> Crypto = new List<Coin>();
  public readonly List<Link> Links = new List<Link>();
  public bool Any { get { return Crypto.Count + Links.Count > 0; } }

  public static DonateInfo Load() {
    var d = new DonateInfo();
    try {
      string json;
      using (var s = typeof(DonateInfo).Assembly.GetManifestResourceStream("donate.json")) {
        if (s == null) return d;
        using (var r = new StreamReader(s)) json = r.ReadToEnd();
      }
      foreach (string o in Objects(json, "crypto")) {
        var c = new Coin { Name = Str(o, "name"), Network = Str(o, "network"), Address = Regex.Replace(Str(o, "address"), "\\s", "") };
        if (c.Address.Length > 0) d.Crypto.Add(c);
      }
      foreach (string o in Objects(json, "links")) {
        var l = new Link { Name = Str(o, "name"), Url = Str(o, "url") };
        if (l.Url.Length == 0) continue;
        Uri uri;
        if (Uri.TryCreate(l.Url, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps) d.Links.Add(l);
        else Log.Write("donate.json: not an https link, skipped: " + l.Url);
      }
    } catch (Exception e) {
      Log.Write("donate.json: " + e.Message);
    }
    return d;
  }

  // The schema is fixed and flat, so a regex per array and per string field is enough.
  static IEnumerable<string> Objects(string json, string array) {
    var m = Regex.Match(json, "\"" + array + "\"\\s*:\\s*\\[([^\\]]*)\\]");
    if (!m.Success) yield break;
    foreach (Match o in Regex.Matches(m.Groups[1].Value, "\\{[^{}]*\\}")) yield return o.Value;
  }

  static string Str(string obj, string key) {
    var m = Regex.Match(obj, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
    return m.Success ? Regex.Unescape(m.Groups[1].Value).Trim() : "";
  }
}

sealed class Donate : Form {
  const int W = 440, Pad = 14, HeaderH = 30, CapH = 22, QrBox = 120, BtnH = 28, CopyW = 118;
  const int HitClose = 0, HitCopy = 1, HitAddr = 100, HitLink = 200; // hit-test ids
  const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

  readonly DonateInfo info;
  readonly bool[][,] qr;
  readonly Font fTitle, fThanks, fText, fName, fSmall, fCaps, fMono, fButton;
  readonly SolidBrush brush = new SolidBrush(Color.Black);
  readonly Timer copiedTimer = new Timer { Interval = 1600 };
  readonly float k;
  readonly string intro = "Він безкоштовний. Якщо став тобі в пригоді, можна підтримати автора — " +
    "переказом на криптогаманець або через один із сервісів нижче.";
  Rectangle btnClose, introRect;
  Rectangle[] cards, qrBoxes, addrRects, copyBtns, linkBtns;
  string[][] addrLines;
  int thanksY, capCrypto, capLinks, hot = -1, copied = -1;

  public Donate(DonateInfo info) {
    this.info = info;
    FormBorderStyle = FormBorderStyle.None;
    StartPosition = FormStartPosition.Manual;
    BackColor = Theme.Bg;
    Text = "Підтримати ArtMeter";
    KeyPreview = true;
    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    using (var g = CreateGraphics()) k = g.DpiY / 96f;
    fTitle = new Font("Segoe UI Semibold", 9f);
    fThanks = new Font("Segoe UI Semibold", 10.5f);
    fText = new Font("Segoe UI", 9f);
    fName = new Font("Segoe UI Semibold", 10.5f);
    fSmall = new Font("Segoe UI", 8.25f);
    fCaps = new Font("Segoe UI Semibold", 7.5f);
    fMono = new Font("Consolas", 9.75f);
    fButton = new Font("Segoe UI", 9f);
    try {
      Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
    } catch {
      // the default icon is fine
    }
    qr = info.Crypto.ConvertAll(c => Qr.Encode(c.Address)).ToArray();
    copiedTimer.Tick += (s, e) => {
      copiedTimer.Stop();
      copied = -1;
      Invalidate();
    };

    var size = Arrange();
    var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
    Bounds = new Rectangle(wa.Left + (wa.Width - size.Width) / 2, wa.Top + Math.Max(0, (wa.Height - size.Height) / 2), size.Width, size.Height);
  }

  int S(float v) { return (int)Math.Round(v * k); }

  protected override void OnHandleCreated(EventArgs e) {
    base.OnHandleCreated(e);
    int round = 2, border = ColorTranslator.ToWin32(Theme.Line);
    Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
    Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref border, 4);
  }

  // ---------- layout: computed once, the content never changes ----------

  Size Arrange() {
    int w = S(W), x0 = S(Pad), inner = w - 2 * x0, y = S(HeaderH);
    btnClose = new Rectangle(w - S(HeaderH), 0, S(HeaderH), S(HeaderH));
    y += S(14);
    thanksY = y;
    y += LineH(fThanks) + S(4);
    introRect = new Rectangle(x0, y, inner, TextRenderer.MeasureText(intro, fText, new Size(inner, 0), Flags | TextFormatFlags.WordBreak).Height);
    y = introRect.Bottom + S(14);

    int n = info.Crypto.Count;
    cards = new Rectangle[n];
    qrBoxes = new Rectangle[n];
    addrRects = new Rectangle[n];
    copyBtns = new Rectangle[n];
    addrLines = new string[n][];
    if (n > 0) {
      capCrypto = y;
      y += S(CapH);
    }
    int charW = TextRenderer.MeasureText("0000000000", fMono, Size.Empty, Flags).Width / 10;
    for (int i = 0; i < n; i++) {
      qrBoxes[i] = new Rectangle(x0 + S(10), y + S(10), S(QrBox), S(QrBox));
      int tx = qrBoxes[i].Right + S(14), tw = x0 + inner - S(12) - tx, ty = y + S(10);
      ty += LineH(fName) + LineH(fSmall) + S(8);
      addrLines[i] = Wrap(info.Crypto[i].Address, Math.Max(8, tw / charW));
      addrRects[i] = new Rectangle(tx - S(5), ty - S(3), tw + S(5), addrLines[i].Length * LineH(fMono) + S(6));
      int by = Math.Max(addrRects[i].Bottom + S(8), qrBoxes[i].Bottom - S(BtnH));
      copyBtns[i] = new Rectangle(tx, by, S(CopyW), S(BtnH));
      cards[i] = new Rectangle(x0, y, inner, Math.Max(qrBoxes[i].Bottom, copyBtns[i].Bottom) + S(10) - y);
      y = cards[i].Bottom + S(10);
    }

    linkBtns = new Rectangle[info.Links.Count];
    if (info.Links.Count > 0) {
      if (n > 0) y += S(4);
      capLinks = y;
      y += S(CapH);
      int lx = x0;
      for (int j = 0; j < linkBtns.Length; j++) {
        int bw = TextRenderer.MeasureText(info.Links[j].Name + "  ↗", fButton, Size.Empty, Flags).Width + S(28);
        if (lx > x0 && lx + bw > x0 + inner) {
          lx = x0;
          y += S(BtnH + 8);
        }
        linkBtns[j] = new Rectangle(lx, y, bw, S(BtnH));
        lx += bw + S(8);
      }
      y += S(BtnH);
    }
    return new Size(w, y + S(Pad + 4));
  }

  int LineH(Font f) { return TextRenderer.MeasureText("Ag", f, Size.Empty, Flags).Height; }

  // Splits an address into lines of equal length rather than leaving a short tail.
  static string[] Wrap(string s, int perLine) {
    int lines = (s.Length + perLine - 1) / perLine, per = (s.Length + lines - 1) / lines;
    var r = new string[lines];
    for (int i = 0; i < lines; i++) r[i] = s.Substring(i * per, Math.Min(per, s.Length - i * per));
    return r;
  }

  // ---------- input ----------

  int HitTest(Point p) {
    if (btnClose.Contains(p)) return HitClose;
    for (int i = 0; i < copyBtns.Length; i++) {
      if (copyBtns[i].Contains(p)) return HitCopy + i;
      if (addrRects[i].Contains(p)) return HitAddr + i;
    }
    for (int j = 0; j < linkBtns.Length; j++) {
      if (linkBtns[j].Contains(p)) return HitLink + j;
    }
    return -1;
  }

  protected override void OnMouseMove(MouseEventArgs e) {
    base.OnMouseMove(e);
    int h = HitTest(e.Location);
    if (h == hot) return;
    hot = h;
    Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
    Invalidate();
  }

  protected override void OnMouseLeave(EventArgs e) {
    base.OnMouseLeave(e);
    if (hot == -1) return;
    hot = -1;
    Invalidate();
  }

  protected override void OnMouseDown(MouseEventArgs e) {
    base.OnMouseDown(e);
    if (e.Button != MouseButtons.Left || HitTest(e.Location) >= 0) return;
    Native.ReleaseCapture();
    Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
  }

  protected override void OnMouseUp(MouseEventArgs e) {
    base.OnMouseUp(e);
    if (e.Button != MouseButtons.Left) return;
    int h = HitTest(e.Location);
    if (h == HitClose) Close();
    else if (h >= HitLink) Open(info.Links[h - HitLink].Url);
    else if (h >= HitAddr) Copy(h - HitAddr);
    else if (h >= HitCopy) Copy(h - HitCopy);
  }

  protected override void OnKeyDown(KeyEventArgs e) {
    base.OnKeyDown(e);
    if (e.KeyCode == Keys.Escape) Close();
  }

  void Copy(int i) {
    try {
      Clipboard.SetDataObject(info.Crypto[i].Address, true, 5, 100);
      copied = i;
      copiedTimer.Stop();
      copiedTimer.Start();
      Invalidate();
    } catch (Exception e) {
      Log.Write("clipboard: " + e.Message);
    }
  }

  static void Open(string url) {
    try {
      Process.Start(url); // only https links get this far, see DonateInfo.Load
    } catch (Exception e) {
      Log.Write("cannot open " + url + ": " + e.Message);
    }
  }

  // ---------- painting ----------

  protected override void OnPaint(PaintEventArgs e) {
    Render(e.Graphics, ClientSize);
  }

  public Bitmap Snapshot() {
    var bmp = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
    using (var g = Graphics.FromImage(bmp)) Render(g, Size);
    return bmp;
  }

  void Render(Graphics g, Size size) {
    g.Clear(Theme.Bg);
    int w = size.Width, x0 = S(Pad), hh = S(HeaderH);
    Fill(g, Theme.Panel, new Rectangle(0, 0, w, hh));
    Fill(g, Theme.Line, new Rectangle(0, hh - 1, w, 1));
    TextRenderer.DrawText(g, "Підтримати автора", fTitle, new Rectangle(x0, 0, w, hh), Theme.Text,
      Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    CloseButton(g, btnClose, hot == HitClose);

    const string thanks = "Дякую, що користуєшся ArtMeter";
    TextRenderer.DrawText(g, thanks, fThanks, new Point(x0, thanksY), Theme.Text, Flags);
    int heartX = x0 + TextRenderer.MeasureText(thanks + "♥", fThanks, Size.Empty, Flags).Width - TextRenderer.MeasureText("♥", fThanks, Size.Empty, Flags).Width + S(4);
    TextRenderer.DrawText(g, "♥", fThanks, new Point(heartX, thanksY), Theme.Accent, Flags);
    TextRenderer.DrawText(g, intro, fText, introRect, Theme.Dim, Flags | TextFormatFlags.WordBreak);

    if (cards.Length > 0) Caption(g, capCrypto, w, "КРИПТОВАЛЮТА");
    for (int i = 0; i < cards.Length; i++) {
      var c = info.Crypto[i];
      FillRound(g, Theme.Panel, cards[i], S(8));
      QrCode(g, qrBoxes[i], qr[i]);
      int tx = copyBtns[i].X, ty = cards[i].Y + S(10);
      TextRenderer.DrawText(g, c.Name, fName, new Point(tx, ty), Theme.Text, Flags);
      ty += LineH(fName);
      TextRenderer.DrawText(g, c.Network, fSmall, new Point(tx, ty), Theme.Dim, Flags);
      if (hot == HitAddr + i || hot == HitCopy + i) FillRound(g, Theme.Hi, addrRects[i], S(4));
      for (int l = 0; l < addrLines[i].Length; l++) {
        TextRenderer.DrawText(g, addrLines[i][l], fMono, new Point(tx, addrRects[i].Y + S(3) + l * LineH(fMono)), Theme.Text, Flags);
      }
      if (copied == i) Button(g, copyBtns[i], "Скопійовано ✓", false, Theme.Gpu);
      else Button(g, copyBtns[i], "Копіювати", hot == HitCopy + i || hot == HitAddr + i, Theme.Text);
    }

    if (linkBtns.Length > 0) Caption(g, capLinks, w, cards.Length > 0 ? "АБО ЧЕРЕЗ СЕРВІС" : "СЕРВІСИ");
    for (int j = 0; j < linkBtns.Length; j++) Button(g, linkBtns[j], info.Links[j].Name + "  ↗", hot == HitLink + j, Theme.Text);
  }

  void Caption(Graphics g, int y, int w, string text) {
    int x0 = S(Pad), h = S(CapH);
    var sz = TextRenderer.MeasureText(text, fCaps, Size.Empty, Flags);
    TextRenderer.DrawText(g, text, fCaps, new Point(x0, y + (h - sz.Height) / 2), Theme.Dim, Flags);
    Fill(g, Theme.Line, new Rectangle(x0 + sz.Width + S(8), y + h / 2, w - x0 - (x0 + sz.Width + S(8)), 1));
  }

  // Black on white with a quiet zone of at least four modules, whole pixels per module so phones read it reliably.
  void QrCode(Graphics g, Rectangle box, bool[,] m) {
    FillRound(g, Color.White, box, S(6));
    int n = m.GetLength(0), mod = Math.Max(1, box.Width / (n + 8)), off = (box.Width - mod * n) / 2;
    brush.Color = Color.Black;
    for (int y = 0; y < n; y++) {
      for (int x = 0; x < n; x++) {
        if (m[y, x]) g.FillRectangle(brush, box.X + off + x * mod, box.Y + off + y * mod, mod, mod);
      }
    }
  }

  void Button(Graphics g, Rectangle r, string text, bool isHot, Color color) {
    FillRound(g, isHot ? Theme.Accent : Theme.Hi, r, S(5));
    TextRenderer.DrawText(g, text, fButton, r, isHot ? Color.White : color,
      Flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
  }

  void CloseButton(Graphics g, Rectangle r, bool isHot) {
    if (isHot) Fill(g, Theme.Hi, r);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, a = 4 * k;
    using (var pen = new Pen(isHot ? Theme.Text : Theme.Dim, 1.5f * k)) {
      g.DrawLine(pen, cx - a, cy - a, cx + a, cy + a);
      g.DrawLine(pen, cx - a, cy + a, cx + a, cy - a);
    }
    g.SmoothingMode = SmoothingMode.None;
  }

  void Fill(Graphics g, Color c, Rectangle r) {
    brush.Color = c;
    g.FillRectangle(brush, r);
  }

  void FillRound(Graphics g, Color c, Rectangle r, int radius) {
    brush.Color = c;
    g.SmoothingMode = SmoothingMode.AntiAlias;
    using (var path = Widget.Round(r, radius)) g.FillPath(brush, path);
    g.SmoothingMode = SmoothingMode.None;
  }

  protected override void Dispose(bool disposing) {
    if (disposing) {
      copiedTimer.Dispose();
      brush.Dispose();
      foreach (var f in new[] { fTitle, fThanks, fText, fName, fSmall, fCaps, fMono, fButton }) f.Dispose();
    }
    base.Dispose(disposing);
  }
}
