using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace PhasmoSound;

/// The phrase wheel: a focusable transparent window over the game. Scroll turns the category,
/// hover highlights a line, click says it. Taking focus frees the mouse from the game's lock.
public partial class WheelWindow : Window
{
    readonly Config cfg;
    readonly Phrases phrases;
    readonly WheelElement el;
    IntPtr gameHwnd;
    public event Action<int>? PhraseChosen;   // absolute index into phrases.Items
    public bool IsOpen => IsVisible;

    public WheelWindow(Config cfg, Phrases phrases)
    {
        this.cfg = cfg; this.phrases = phrases;
        InitializeComponent();
        el = new WheelElement(cfg, phrases);
        Root.Children.Add(el);
        el.MouseMove += (_, e) => { el.Hover(e.GetPosition(el)); };
        el.MouseLeftButtonDown += (_, e) =>
        {
            var pos = e.GetPosition(el);
            int line = el.LineAt(pos);
            if (line >= 0) { int idx = el.Page * 9 + line; Close_(); PhraseChosen?.Invoke(idx); return; }
            int slice = el.SliceAt(pos);
            if (slice >= 0) el.SetPage(slice);
            else Close_();
        };
        el.MouseRightButtonDown += (_, _) => Close_();
        el.MouseWheel += (_, e) => el.SetPage(el.Page + (e.Delta > 0 ? -1 : 1));
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close_(); e.Handled = true; }
            else if (e.Key >= Key.D1 && e.Key <= Key.D9) { int idx = el.Page * 9 + (e.Key - Key.D1); if (idx < phrases.Items.Count) { Close_(); PhraseChosen?.Invoke(idx); } e.Handled = true; }
        };
        Deactivated += (_, _) => { if (IsVisible) Hide(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    public void Open(int left, int top, int right, int bottom, IntPtr game)
    {
        gameHwnd = game;
        phrases.Reload();
        el.SetPage(0);
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(hwnd, HWND_TOPMOST, left, top, right - left, bottom - top, SWP_SHOWWINDOW);
        Show();
        Activate();
        ClipCursor(IntPtr.Zero);
        SetCursorPos((left + right) / 2, (top + bottom) / 2);
        el.Focus();
        Keyboard.Focus(el);
    }

    public void Close_()
    {
        Hide();
        if (gameHwnd != IntPtr.Zero) SetForegroundWindow(gameHwnd);
    }

    public void Toggle(int l, int t, int r, int b, IntPtr game) { if (IsVisible) Close_(); else Open(l, t, r, b, game); }

    const int GWL_EXSTYLE = -20; const long WS_EX_TOOLWINDOW = 0x80;
    const uint SWP_SHOWWINDOW = 0x40; static readonly IntPtr HWND_TOPMOST = new(-1);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ClipCursor(IntPtr rect);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
}

/// Draws the wheel and the line list, and hit-tests them.
public sealed class WheelElement : FrameworkElement
{
    readonly Config cfg; readonly Phrases phrases;
    readonly Typeface font = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    readonly Typeface fontBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    public int Page { get; private set; }
    int hoverLine = -1, hoverSlice = -1;
    readonly List<Rect> lineRects = new();
    double wcx, wcy, wr;

    public WheelElement(Config cfg, Phrases phrases) { this.cfg = cfg; this.phrases = phrases; Focusable = true; }

    int Pages => Math.Max(1, (phrases.Items.Count + 8) / 9);
    List<string> Lines => phrases.Items.Skip(Page * 9).Take(9).Select(p => p.Text).ToList();
    string PageName(int i) => i < (cfg.PageNames?.Length ?? 0) ? cfg.PageNames![i] : "Page " + (i + 1);

    public void SetPage(int p) { int n = Pages; Page = ((p % n) + n) % n; hoverLine = -1; InvalidateVisual(); }

    public void Hover(Point pos)
    {
        int l = LineAt(pos), s = SliceAt(pos);
        if (l != hoverLine || s != hoverSlice) { hoverLine = l; hoverSlice = s; InvalidateVisual(); }
    }

    public int LineAt(Point p) { for (int i = 0; i < lineRects.Count; i++) if (lineRects[i].Contains(p)) return i; return -1; }

    public int SliceAt(Point p)
    {
        double dx = p.X - wcx, dy = p.Y - wcy, d = Math.Sqrt(dx * dx + dy * dy);
        if (d < wr * 0.55 || d > wr + 6) return -1;
        double ang = Math.Atan2(dx, -dy) * 180 / Math.PI; if (ang < 0) ang += 360;
        int n = Pages; double half = 180.0 / n;
        return (int)Math.Floor(((ang + half) % 360) / (360.0 / n));
    }

    static Point P(double cx, double cy, double r, double deg) { double a = deg * Math.PI / 180.0; return new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a)); }

    FormattedText FT(string s, double size, Color color, bool bold) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? fontBold : font, size, new SolidColorBrush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);

    double Text(DrawingContext dc, string s, double x, double y, double size, Color color, bool bold = false, int align = 0)
    {
        var ft = FT(s, size, color, bold);
        if (align == 1) x -= ft.Width / 2; else if (align == 2) x -= ft.Width;
        var sh = FT(s, size, Color.FromArgb((byte)(color.A * 0.9), 0, 0, 0), bold);
        dc.DrawText(sh, new Point(x + 1.5, y + 1.5)); dc.DrawText(sh, new Point(x - 1.0, y + 1.0)); dc.DrawText(ft, new Point(x, y));
        return ft.Width;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight; if (w < 10 || h < 10) return;
        double s = cfg.UiScale > 0 ? cfg.UiScale : Math.Max(0.6, h / 720.0);
        // invisible full-size hit surface so the window gets mouse events everywhere
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, new Rect(0, 0, w, h));
        var lines = Lines; int n = Pages;
        var accent = Color.FromRgb(127, 179, 255);
        wr = Math.Min(w, h) * 0.19; wcx = w / 2 - Math.Min(w * 0.22, 330 * s); wcy = h / 2;
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(200, 18, 18, 22)), new Pen(new SolidColorBrush(Color.FromArgb(120, 127, 179, 255)), 2), new Point(wcx, wcy), wr + 6 * s, wr + 6 * s);
        for (int i = 0; i < n; i++)
        {
            double a0 = i * 360.0 / n - 180.0 / n, a1 = a0 + 360.0 / n;
            bool sel = i == Page, hov = i == hoverSlice;
            var segCol = sel ? accent : hov ? Color.FromArgb(120, 127, 179, 255) : Color.FromArgb(60, 255, 255, 255);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(P(wcx, wcy, wr * 0.55, a0 + 2), true, true);
                c.LineTo(P(wcx, wcy, wr, a0 + 2), true, false);
                c.ArcTo(P(wcx, wcy, wr, a1 - 2), new Size(wr, wr), 0, false, SweepDirection.Clockwise, true, false);
                c.LineTo(P(wcx, wcy, wr * 0.55, a1 - 2), true, false);
                c.ArcTo(P(wcx, wcy, wr * 0.55, a0 + 2), new Size(wr * 0.55, wr * 0.55), 0, false, SweepDirection.Counterclockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(new SolidColorBrush(segCol), new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1), g);
            var mid = P(wcx, wcy, wr * 0.78, (a0 + a1) / 2);
            Text(dc, PageName(i), mid.X, mid.Y - 8 * s, 13 * s, sel ? Color.FromRgb(16, 18, 24) : Colors.White, sel, 1);
        }
        Text(dc, PageName(Page), wcx, wcy - 22 * s, 20 * s, accent, true, 1);
        Text(dc, "scroll = turn", wcx, wcy + 4 * s, 12 * s, Color.FromArgb(170, 255, 255, 255), false, 1);
        Text(dc, "click a line to say it", wcx, wcy + 20 * s, 12 * s, Color.FromArgb(170, 255, 255, 255), false, 1);

        double fs = 19 * s, row = fs * 1.9, pad = 14 * s;
        double bw = Math.Min(w * 0.40, 600 * s), bh = pad * 2 + row * Math.Max(1, lines.Count) + 30 * s;
        double bx = wcx + wr + 40 * s, by = h / 2 - bh / 2;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(215, 20, 20, 24)), new Pen(new SolidColorBrush(accent), 2), new Rect(bx, by, bw, bh), 12 * s, 12 * s);
        Text(dc, "Point and click   ·   right click or Esc closes", bx + pad, by + pad * 0.6, 13 * s, Color.FromArgb(200, 176, 200, 255), true);
        lineRects.Clear();
        for (int i = 0; i < lines.Count; i++)
        {
            double y = by + pad + 22 * s + i * row;
            var rect = new Rect(bx + pad * 0.5, y - 4 * s, bw - pad, row - 2 * s);
            lineRects.Add(rect);
            if (i == hoverLine) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(70, 127, 179, 255)), null, rect, 8 * s, 8 * s);
            dc.DrawRoundedRectangle(new SolidColorBrush(i == hoverLine ? Colors.White : accent), null, new Rect(bx + pad, y + 2 * s, fs * 1.5, fs * 1.5), 6 * s, 6 * s);
            Text(dc, (i + 1).ToString(), bx + pad + fs * 0.75, y + 2 * s, fs, Color.FromRgb(20, 20, 24), true, 1);
            string line = lines[i];
            double maxW = bw - pad * 2 - fs * 2.2;
            if (FT(line, fs, Colors.White, false).Width > maxW) { while (line.Length > 8 && FT(line + "…", fs, Colors.White, false).Width > maxW) line = line[..^1]; line += "…"; }
            Text(dc, line, bx + pad + fs * 2.2, y, fs, i == hoverLine ? Colors.White : Color.FromRgb(230, 234, 240), i == hoverLine);
        }
    }
}
