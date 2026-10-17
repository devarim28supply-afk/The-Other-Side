using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace PhasmoSound;

/// Draws the whole overlay: direction (screen-edge glows or a ring), sudden-sound flashes,
/// current sounds, and the event log.
public sealed class RadarElement : FrameworkElement
{
    readonly Config cfg;
    readonly OverlayState state;
    readonly Typeface font = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    readonly Typeface fontBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    double ppd = 1.0;

    public RadarElement(Config cfg, OverlayState state)
    {
        this.cfg = cfg; this.state = state;
        IsHitTestVisible = false;
        Loaded += (_, _) => ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
    }

    static readonly Dictionary<string, Color> CatColor = new()
    {
        ["move"] = Color.FromRgb(255, 150, 40),
        ["door"] = Color.FromRgb(255, 225, 70),
        ["object"] = Color.FromRgb(150, 235, 120),
        ["ghost"] = Color.FromRgb(255, 80, 130),
        ["voice"] = Color.FromRgb(120, 175, 255),
        ["alert"] = Color.FromRgb(210, 120, 255),
        ["env"] = Color.FromRgb(90, 205, 205),
        ["music"] = Color.FromRgb(160, 160, 200),
        ["other"] = Color.FromRgb(180, 180, 180),
        ["custom"] = Color.FromRgb(250, 240, 190),
        ["game"] = Color.FromRgb(255, 215, 0),
        ["Player"] = Color.FromRgb(60, 140, 255),          // players' footsteps (Kaalob 2026-10-17: blue), GroupColors can override
        ["GhostSteps"] = Color.FromRgb(255, 35, 35),       // ghost footsteps (Kaalob 2026-10-17: red)
    };

    readonly Dictionary<string, Color> groupCol = new(StringComparer.OrdinalIgnoreCase);

    /// A sound group's color from config (GroupColors), else the built-in category color.
    Color ColorOf(string cat)
    {
        if (groupCol.TryGetValue(cat, out var c)) return c;
        // rows made before a sound is named (e.g. "LOUD !") still carry a detector category
        if (cfg.CategoryGroups.TryGetValue(cat, out var grp) && grp != cat) return groupCol[cat] = ColorOf(grp);
        if (cfg.GroupColors.TryGetValue(cat, out var hex))
        {
            try { c = (Color)ColorConverter.ConvertFromString(hex); }
            catch { c = CatColor["other"]; }
        }
        else c = CatColor.TryGetValue(cat, out var cc) ? cc : CatColor["other"];
        groupCol[cat] = c;
        return c;
    }

    float Level(float db) => Math.Clamp((db - cfg.MinDb) / (cfg.MaxDb - cfg.MinDb), 0f, 1f);

    static Color Lerp(Color a, Color b, float t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    static Color LevelColor(float t)
    {
        var lo = Color.FromRgb(170, 215, 255); var mid = Color.FromRgb(255, 230, 80); var hi = Color.FromRgb(255, 50, 40);
        return t < 0.55f ? Lerp(lo, mid, t / 0.55f) : Lerp(mid, hi, (t - 0.55f) / 0.45f);
    }

    static Point P(double cx, double cy, double r, double deg)
    {
        double a = deg * Math.PI / 180.0;
        return new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a));
    }

    static void Arc(DrawingContext dc, double cx, double cy, double r, double a0, double a1, Color color, double thick)
    {
        if (a1 - a0 >= 359.9) { dc.DrawEllipse(null, new Pen(new SolidColorBrush(color), thick), new Point(cx, cy), r, r); return; }
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(P(cx, cy, r, a0), false, false);
            c.ArcTo(P(cx, cy, r, a1), new Size(r, r), 0, a1 - a0 > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
    }

    FormattedText FT(string s, double size, Color color, bool bold) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? fontBold : font, size, new SolidColorBrush(color), ppd);

    /// align: 0 left, 1 center, 2 right. Draws a dark shadow under the text for readability over any game scene.
    double Text(DrawingContext dc, string s, double x, double y, double size, Color color, bool bold = false, int align = 0)
    {
        var ft = FT(s, size, color, bold);
        if (align == 1) x -= ft.Width / 2; else if (align == 2) x -= ft.Width;
        var sh = FT(s, size, Color.FromArgb((byte)(color.A * 0.9), 0, 0, 0), bold);
        dc.DrawText(sh, new Point(x + 1.5, y + 1.5));
        dc.DrawText(sh, new Point(x - 1.0, y + 1.0));
        dc.DrawText(ft, new Point(x, y));
        return ft.Width;
    }

    /// Text with a solid black outline (GroupOutline groups), for light colors over bright game scenes.
    double OutlinedText(DrawingContext dc, string s, double x, double y, double size, Color color, bool bold = false)
    {
        var ft = FT(s, size, color, bold);
        var geo = ft.BuildGeometry(new Point(x, y));
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(color.A, 0, 0, 0)), Math.Max(2, size * 0.16)) { LineJoin = PenLineJoin.Round }, geo);
        dc.DrawGeometry(new SolidColorBrush(color), null, geo);
        return ft.Width;
    }

    /// How see-through a sound's name is drawn: GroupOpacity for its group, else SoundOpacity.
    double OpacityOf(string cat)
    {
        string g = cfg.CategoryGroups.TryGetValue(cat, out var m) ? m : cat;
        return Math.Clamp(cfg.GroupOpacity.TryGetValue(g, out var o) ? o : cfg.SoundOpacity, 0.05, 1);
    }

    bool Outlined(string cat) => cfg.GroupOutline.Contains(cfg.CategoryGroups.TryGetValue(cat, out var g) ? g : cat, StringComparer.OrdinalIgnoreCase);

    /// Group-colored label: outlined for GroupOutline groups, shadowed otherwise.
    double GroupText(DrawingContext dc, string cat, string s, double x, double y, double size, Color color) =>
        Outlined(cat) ? OutlinedText(dc, s, x, y, size, color, true) : Text(dc, s, x, y, size, color, true);

    static string ArrowFor(float angle, bool surround)
    {
        if (!surround)
            return angle < -20 ? "←" : angle > 20 ? "→" : "↑";
        int i = ((int)Math.Round(angle / 45.0) % 8 + 8) % 8;
        return i switch { 0 => "↑", 1 => "↗", 2 => "→", 3 => "↘", 4 => "↓", 5 => "↙", 6 => "←", _ => "↖" };
    }

    // ---------------------------------------------------------------- edge geometry

    /// Where a ray from the screen center at this angle hits the screen border.
    /// edge: 0 top, 1 right, 2 bottom, 3 left.
    static (Point pt, int edge) EdgeHit(double w, double h, double deg)
    {
        double a = deg * Math.PI / 180.0;
        double dx = Math.Sin(a), dy = -Math.Cos(a);
        double cx = w / 2, cy = h / 2;
        double tx = Math.Abs(dx) < 1e-6 ? double.PositiveInfinity : (dx > 0 ? (w - cx) / dx : -cx / dx);
        double ty = Math.Abs(dy) < 1e-6 ? double.PositiveInfinity : (dy > 0 ? (h - cy) / dy : -cy / dy);
        double t = Math.Min(tx, ty);
        var pt = new Point(cx + dx * t, cy + dy * t);
        int edge = ty <= tx ? (dy < 0 ? 0 : 2) : (dx > 0 ? 1 : 3);
        return (pt, edge);
    }

    /// Soft glow hugging one edge: an ellipse centered on the border point, long along the edge, fading inward.
    static void EdgeGlow(DrawingContext dc, Point pt, int edge, double halfLen, double thick, Color color)
    {
        if (color.A == 0) return;
        bool horizontal = edge == 0 || edge == 2;
        double rx = horizontal ? halfLen : thick, ry = horizontal ? thick : halfLen;
        var brush = new RadialGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B))
        {
            Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5), RadiusX = 0.5, RadiusY = 0.5,
        };
        brush.GradientStops.Insert(1, new GradientStop(Color.FromArgb((byte)(color.A * 0.55), color.R, color.G, color.B), 0.45));
        brush.Freeze();
        dc.DrawEllipse(brush, null, pt, rx, ry);
    }

    /// Solid bar along an edge (used for the bright core of a channel that is really loud).
    static void EdgeBar(DrawingContext dc, double w, double h, Point pt, int edge, double halfLen, double thick, Color color)
    {
        Rect r = edge switch
        {
            0 => new Rect(pt.X - halfLen, 0, halfLen * 2, thick),
            2 => new Rect(pt.X - halfLen, h - thick, halfLen * 2, thick),
            1 => new Rect(w - thick, pt.Y - halfLen, thick, halfLen * 2),
            _ => new Rect(0, pt.Y - halfLen, thick, halfLen * 2),
        };
        bool horizontal = edge == 0 || edge == 2;
        var lg = new LinearGradientBrush(
            Color.FromArgb(0, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B),
            horizontal ? 0 : 90);
        lg.GradientStops.Insert(1, new GradientStop(color, 0.5));
        lg.Freeze();
        dc.DrawRectangle(lg, null, r);
    }

    // ---------------------------------------------------------------- render

    readonly System.Diagnostics.Stopwatch renderClock = System.Diagnostics.Stopwatch.StartNew();
    long renderCount, renderTicks; DateTime lastRenderLog = DateTime.UtcNow;

    protected override void OnRender(DrawingContext dc)
    {
        long t0 = renderClock.ElapsedTicks;
        try { RenderAll(dc); }
        finally
        {
            renderTicks += renderClock.ElapsedTicks - t0; renderCount++;
            if ((DateTime.UtcNow - lastRenderLog).TotalSeconds >= 10)
            {
                Log.Write($"render: {renderCount / 10.0:F0} fps, avg draw {renderTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, renderCount):F1} ms");
                renderCount = 0; renderTicks = 0; lastRenderLog = DateTime.UtcNow;
            }
        }
    }

    void RenderAll(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        double s = cfg.UiScale > 0 ? cfg.UiScale : Math.Max(0.6, h / 720.0);

        string status, toast; DateTime toastUntil; List<EventItem> evs;
        lock (state.Lock)
        {
            status = state.Status; toast = state.Toast; toastUntil = state.ToastUntil;
            evs = state.Events.ToList();
        }
        var now = DateTime.UtcNow;

        // --- phrase board (F5): numbered lines on the right, press 1..9
        bool boardOpen; List<string> board; int pageNo, pages; List<string> pageNames;
        lock (state.Lock) { boardOpen = state.BoardOpen; board = state.Board.ToList(); pageNo = state.BoardPageNo; pages = state.BoardPages; pageNames = state.PageNames.ToList(); }
        if (boardOpen && board.Count > 0)
        {
            // the wheel: one segment per category, highlighted one = current page; scroll to rotate
            double wr = Math.Min(w, h) * 0.19, wcx = w / 2 - Math.Min(w * 0.22, 330 * s), wcy = h / 2;
            var accent = Color.FromRgb(127, 179, 255);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(200, 18, 18, 22)), new Pen(new SolidColorBrush(Color.FromArgb(120, 127, 179, 255)), 2), new Point(wcx, wcy), wr + 6 * s, wr + 6 * s);
            int n = Math.Max(1, pages);
            for (int i = 0; i < n; i++)
            {
                double a0 = i * 360.0 / n - 180.0 / n, a1 = a0 + 360.0 / n;
                bool sel = i == pageNo;
                var segCol = sel ? accent : Color.FromArgb(60, 255, 255, 255);
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
                string name = i < pageNames.Count ? pageNames[i] : "Page " + (i + 1);
                Text(dc, name, mid.X, mid.Y - 8 * s, 13 * s, sel ? Color.FromRgb(16, 18, 24) : Colors.White, sel, 1);
            }
            string curName = pageNo < pageNames.Count ? pageNames[pageNo] : "";
            Text(dc, curName, wcx, wcy - 22 * s, 20 * s, accent, true, 1);
            Text(dc, "scroll = turn", wcx, wcy + 4 * s, 12 * s, Color.FromArgb(170, 255, 255, 255), false, 1);
            Text(dc, "click / Esc = close", wcx, wcy + 20 * s, 12 * s, Color.FromArgb(170, 255, 255, 255), false, 1);

            // the lines of the highlighted category, numbered
            double fs = 19 * s, brow = fs * 1.9, pad = 14 * s;
            double bw = Math.Min(w * 0.40, 600 * s), bh = pad * 2 + brow * board.Count + 30 * s;
            double bx = wcx + wr + 40 * s, by = h / 2 - bh / 2;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(215, 20, 20, 24)), new Pen(new SolidColorBrush(accent), 2), new Rect(bx, by, bw, bh), 12 * s, 12 * s);
            string head = "Press a number to say it";
            Text(dc, head, bx + pad, by + pad * 0.6, 13 * s, Color.FromArgb(200, 176, 200, 255), true);
            for (int i = 0; i < board.Count && i < 9; i++)
            {
                double y = by + pad + 22 * s + i * brow;
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(127, 179, 255)), null, new Rect(bx + pad, y + 2 * s, fs * 1.5, fs * 1.5), 6 * s, 6 * s);
                Text(dc, (i + 1).ToString(), bx + pad + fs * 0.75, y + 2 * s, fs, Color.FromRgb(20, 20, 24), true, 1);
                string line = board[i];
                var ft = FT(line, fs, Colors.White, false);
                double maxW = bw - pad * 2 - fs * 2.2;
                if (ft.Width > maxW) { while (line.Length > 8 && FT(line + "…", fs, Colors.White, false).Width > maxW) line = line[..^1]; line += "…"; }
                Text(dc, line, bx + pad + fs * 2.2, y, fs, Colors.White);
            }
        }

        // --- captions (whisper): last few lines, bottom centre, newest at the bottom
        if (cfg.WhisperCaptions)
        {
            List<CaptionLine> caps; lock (state.Lock) caps = state.Captions.ToList();
            var live = caps.Where(c => (now - c.Time).TotalSeconds < cfg.CaptionLifetimeSec).TakeLast(Math.Max(1, cfg.CaptionLines)).ToList();
            if (live.Count > 0)
            {
                double cfs = cfg.CaptionFontSize * s, crow = cfs * 1.35, cpad = 10 * s;
                double cw = Math.Min(w * 0.72, 1150 * s);
                double chh = cpad * 2 + crow * live.Count;
                double cx0 = w / 2 - cw / 2, cy0 = h - chh - 34 * s;
                dc.PushOpacity(Math.Clamp(cfg.CaptionOpacity, 0.05, 1));
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(12, 12, 16)), null, new Rect(cx0, cy0, cw, chh), 10 * s, 10 * s);
                for (int i = 0; i < live.Count; i++)
                {
                    string line = live[i].Partial ? live[i].Text + " …" : live[i].Text;   // still being spoken
                    double maxW = cw - cpad * 2;
                    if (FT(line, cfs, Colors.White, false).Width > maxW) { while (line.Length > 10 && FT("…" + line, cfs, Colors.White, false).Width > maxW) line = line[1..]; line = "…" + line; }
                    var col = i == live.Count - 1 ? Colors.White : Color.FromArgb(200, 220, 224, 232);
                    if (cfg.GroupColors.Count > 0)
                    {
                        // ghost lines in the Ghost color, people talking in the Voice color; older lines a bit dimmer
                        string who = live[i].Text.StartsWith("GHOST") ? "Ghost"
                                   : live[i].Text.StartsWith(cfg.OwnVoiceLabel + ":") && cfg.GroupColors.ContainsKey("You") ? "You" : "Voice";
                        var gc = ColorOf(who);
                        col = Color.FromArgb(i == live.Count - 1 ? (byte)255 : (byte)200, gc.R, gc.G, gc.B);
                    }
                    Text(dc, line, w / 2, cy0 + cpad + i * crow, cfs, col, i == live.Count - 1, 1);
                }
                dc.Pop();
            }
        }

        // --- game helper note: top centre under the header, purple border
        string helperNote; DateTime helperUntil;
        lock (state.Lock) { helperNote = state.HelperNote; helperUntil = state.HelperUntil; }
        if (now < helperUntil && helperNote.Length > 0)
        {
            var hcol = Color.FromRgb(190, 150, 255);
            double hfs = 20 * s;
            string hline = "HELPER:  " + helperNote;
            var hft = FT(hline, hfs, Colors.White, true);
            double hx = w / 2 - hft.Width / 2 - 16 * s, hy = 50 * s;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(12, 12, 16)), new Pen(new SolidColorBrush(hcol), 2.5),
                new Rect(hx, hy, hft.Width + 32 * s, hfs + 20 * s), 10 * s, 10 * s);
            Text(dc, hline, w / 2, hy + 9 * s, hfs, hcol, true, 1);
        }

        // --- conversation summary: under the helper note, teal border, up to 2 lines
        string sumNote; DateTime sumUntil;
        lock (state.Lock) { sumNote = state.SummaryNote; sumUntil = state.SummaryUntil; }
        if (now < sumUntil && sumNote.Length > 0)
        {
            var tcol = Color.FromRgb(90, 230, 200);
            double tfs = 19 * s, maxW = Math.Min(w * 0.6, 1100 * s);
            var lines = new List<string>();
            string tline = "TALK:  ";
            foreach (var word in sumNote.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string next = tline.Length == 0 ? word : tline + (tline.EndsWith("  ") ? "" : " ") + word;
                if (FT(next, tfs, Colors.White, true).Width > maxW && tline.Length > 0 && lines.Count < 1) { lines.Add(tline); tline = word; }
                else tline = next;
            }
            lines.Add(tline);
            double bw = lines.Max(l => FT(l, tfs, Colors.White, true).Width) + 32 * s;
            double ty = 50 * s + (now < helperUntil && helperNote.Length > 0 ? 20 * s + 28 * s : 0);
            double th = lines.Count * tfs * 1.35 + 18 * s;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(12, 12, 16)), new Pen(new SolidColorBrush(tcol), 2.5),
                new Rect(w / 2 - bw / 2, ty, bw, th), 10 * s, 10 * s);
            for (int i = 0; i < lines.Count; i++) Text(dc, lines[i], w / 2, ty + 8 * s + i * tfs * 1.35, tfs, tcol, true, 1);
        }

        // --- speaking sign: Kaalob cannot hear the computer voice, so show when it is talking (2026-10-10)
        string speakText; int speakPhase;
        lock (state.Lock) { speakText = state.SpeakText; speakPhase = state.SpeakPhase; }
        if (cfg.SpeakingSign && speakPhase > 0)
        {
            string line = speakText.Length > 60 ? speakText[..57] + "..." : speakText;
            string sign = speakPhase == 2 ? "SPEAKING:  " + line : "Getting voice ready...";
            var you = Color.FromRgb(90, 210, 255);
            double sfs = 20 * s;
            var sft = FT(sign, sfs, Colors.White, true);
            double sx = w / 2 - sft.Width / 2 - 16 * s, sy = h * 0.66;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(12, 12, 16)), new Pen(new SolidColorBrush(you), speakPhase == 2 ? 3 : 1.5),
                new Rect(sx, sy, sft.Width + 32 * s, sfs + 20 * s), 10 * s, 10 * s);
            Text(dc, sign, w / 2, sy + 9 * s, sfs, speakPhase == 2 ? you : Color.FromRgb(200, 200, 200), true, 1);
        }

        // --- toast (training feedback)
        if (now < toastUntil && !string.IsNullOrEmpty(toast))
        {
            double ts = 24 * s;
            var ft = FT(toast, ts, Colors.White, true);
            double tx = w / 2 - ft.Width / 2 - 16 * s, ty = 60 * s;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 40, 40, 40)), new Pen(new SolidColorBrush(Color.FromRgb(255, 215, 0)), 2),
                new Rect(tx, ty, ft.Width + 32 * s, ts + 20 * s), 10 * s, 10 * s);
            Text(dc, toast, w / 2, ty + 9 * s, ts, Colors.White, true, 1);
        }

        // --- sound ring around the crosshair: a dot per sound at its exact direction, closer = bigger,
        //     a fading trail behind a moving sound (ghost walking), ▲ / ▼ for another floor
        if (cfg.SoundRing)
        {
            double rcx = w / 2, rcy = h / 2, R = Math.Min(w, h) * cfg.SoundRingRadius;
            var dots = new List<(float ang, double near, double up, double k, Color col, string cat, List<(float ang, double k)> trail)>();
            var flashes = new List<(double k, Color col)>();
            lock (state.Lock)
            {
                foreach (var ev in state.Events)
                {
                    double age = (now - ev.Time).TotalSeconds;
                    if (age > cfg.SoundRingSec) continue;
                    double k = 1 - age / cfg.SoundRingSec;
                    var col = ColorOf(ev.Category);
                    if (!ev.HasPos) { if (age < 1.2) flashes.Add((1 - age / 1.2, col)); continue; }
                    var (ang, dist, up) = state.Where(ev.X, ev.Y, ev.Z, cfg.EyeHeightM);
                    double near = Math.Clamp(1 - dist / Math.Max(1, cfg.DefaultHearM), 0.15, 1);
                    var trail = new List<(float, double)>();
                    foreach (var p in ev.Trail)
                    {
                        double ta = (now - p.t).TotalSeconds;
                        if (ta > cfg.SoundTrailSec) continue;
                        trail.Add((state.Where(p.x, p.y, p.z, cfg.EyeHeightM).angle, 1 - ta / cfg.SoundTrailSec));
                    }
                    dots.Add((ang, near, up, k, col, ev.Category, trail));
                }
            }
            if (dots.Count > 0 || flashes.Count > 0)
            {
                // faint circle + a tick at the top (= where you look) while there is something to show
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 2 * s), new Point(rcx, rcy), R, R);
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 2 * s), P(rcx, rcy, R - 5 * s, 0), P(rcx, rcy, R + 5 * s, 0));
                // "in your head" sounds (no direction for anyone): the whole ring pulses in their color
                foreach (var (k, col) in flashes)
                    dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(200 * k), col.R, col.G, col.B)), 6 * s), new Point(rcx, rcy), R, R);
                // oldest first so the newest dot is on top
                for (int i = dots.Count - 1; i >= 0; i--)
                {
                    var d = dots[i];
                    foreach (var (ta, tk) in d.trail)
                        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(150 * tk * d.k), d.col.R, d.col.G, d.col.B)), null, P(rcx, rcy, R, ta), (3 + 3 * d.near) * s, (3 + 3 * d.near) * s);
                    double r = (5 + 10 * d.near) * s;
                    var c = P(rcx, rcy, R, d.ang);
                    byte a = (byte)(255 * Math.Clamp(d.k * 1.6, 0, 1));
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(a, d.col.R, d.col.G, d.col.B)), new Pen(new SolidColorBrush(Color.FromArgb(a, 0, 0, 0)), Math.Max(1.5, 2 * s)), c, r, r);
                    if (Math.Abs(d.up) > cfg.FloorDiffM)
                    {
                        var o = P(rcx, rcy, R + r + 12 * s, d.ang);
                        Text(dc, d.up > 0 ? "▲" : "▼", o.X, o.Y - 10 * s, 16 * s, Color.FromArgb(a, 255, 255, 255), true, 1);
                    }
                }
            }
        }

        // --- exact-direction edge markers: a glow on the screen edge where each new (or moving ghost) sound is
        double thickE = cfg.EdgeThickness * s;
        if (cfg.EdgeMarkers)
        {
            foreach (var ev in evs.Where(e => e.HasPos).Reverse())
            {
                double age = (now - ev.Time).TotalSeconds;
                if (age > cfg.EdgeMarkerSec) continue;
                float k = (float)(1 - age / cfg.EdgeMarkerSec);
                var (ang, dist, up) = Where(ev);
                var col = ColorOf(ev.Category);
                // closer = stronger
                float near = (float)Math.Clamp(1 - dist / Math.Max(1, cfg.DefaultHearM), 0.35, 1);
                var (pt, edge) = EdgeHit(w, h, ang);
                double halfLen = (edge == 0 || edge == 2 ? w : h) * 0.10;
                EdgeGlow(dc, pt, edge, halfLen, thickE * (0.8 + 0.6 * near), Color.FromArgb((byte)(230 * k * near), col.R, col.G, col.B));
                EdgeBar(dc, w, h, pt, edge, halfLen * 0.7, thickE * 0.18, Color.FromArgb((byte)(255 * k), col.R, col.G, col.B));
                // its name + distance just inside the glow
                string tag = $"{ev.Label}  {DistText(dist)}{FloorText(up)}";
                double tfs = 15 * s * cfg.SoundTextScale / 0.75;
                var tft = FT(tag, tfs, Colors.White, true);
                double inset = thickE * 1.1;
                double tx = Math.Clamp(pt.X, inset + tft.Width / 2, w - inset - tft.Width / 2);
                double ty = Math.Clamp(pt.Y, inset, h - inset - tfs * 1.4);
                if (edge == 0) ty = inset; else if (edge == 2) ty = h - inset - tfs * 1.4;
                else tx = edge == 1 ? w - inset - tft.Width / 2 : inset + tft.Width / 2;
                dc.PushOpacity(Math.Clamp(k * 1.5, 0, 1));
                GroupText(dc, ev.Category, tag, tx - tft.Width / 2, ty, tfs, col);
                dc.Pop();
            }
        }

        // --- sound rows (bottom-left): arrow in the exact direction (turns as you turn), name, distance, floor
        double ss = s * cfg.SoundTextScale;
        double rowH = 34 * ss, ex = 28 * s, ey = h - 46 * s;
        int row = 0;
        foreach (var ev in evs)
        {
            double age = (now - ev.Time).TotalSeconds;
            if (age > cfg.EventLifetimeSec || row >= cfg.MaxEvents) continue;
            float alpha = age > cfg.EventLifetimeSec - 2 ? (float)((cfg.EventLifetimeSec - age) / 2.0) : 1f;
            alpha = Math.Clamp(alpha, 0f, 1f) * (row == 0 ? 1f : 0.85f);
            var col = ColorOf(ev.Category);
            double y = ey - row * rowH;
            dc.PushOpacity(OpacityOf(ev.Category));
            var dot = Color.FromArgb((byte)(255 * alpha), col.R, col.G, col.B);
            dc.DrawEllipse(new SolidColorBrush(dot), new Pen(new SolidColorBrush(Color.FromArgb((byte)(200 * alpha), 0, 0, 0)), Outlined(ev.Category) ? 2.5 * s : 1),
                new Point(ex + 7 * ss, y + 14 * ss), 7 * ss, 7 * ss);
            double x = ex + 24 * ss;
            string where = "";
            if (ev.HasPos)
            {
                var (ang, dist, up) = Where(ev);
                DrawArrow(dc, x + 14 * ss, y + 14 * ss, 15 * ss, ang, Color.FromArgb((byte)(255 * alpha), 255, 255, 255));
                where = DistText(dist) + FloorText(up);
            }
            else Text(dc, "•", x + 6 * ss, y - 2 * ss, 24 * ss, Color.FromArgb((byte)(255 * alpha), 255, 255, 255), true);
            x += 34 * ss;
            x += GroupText(dc, ev.Category, ev.Label, x, y + 1 * ss, 21 * ss, Color.FromArgb((byte)(255 * alpha), col.R, col.G, col.B)) + 12 * s;
            if (where.Length > 0) x += Text(dc, where, x, y + 3 * ss, 18 * ss, Color.FromArgb((byte)(255 * alpha), 255, 255, 255), true) + 12 * s;
            if (ev.Count > 1) x += Text(dc, "x" + ev.Count, x, y + 6 * ss, 14 * ss, Color.FromArgb((byte)(200 * alpha), 220, 220, 220)) + 10 * s;
            Text(dc, age < 1 ? "now" : $"{(int)age}s", x, y + 6 * ss, 14 * ss, Color.FromArgb((byte)(200 * alpha), 220, 220, 220));
            dc.Pop();
            row++;
        }

        // --- no feed yet: say so once the game should be running
        DateTime feedSeen; lock (state.Lock) feedSeen = state.FeedSeen;
        if (feedSeen == DateTime.MinValue && cfg.ShowHeader)
            Text(dc, "Waiting for the game (SoundSpy)...", ex, ey - rowH, 14 * ss, Color.FromArgb(170, 255, 255, 255));

        // --- color key for the sound groups, under the event log (ShowColorKey off: Kaalob 2026-10-11 "I already know")
        if (cfg.ShowColorKey && cfg.GroupColors.Count > 0)
        {
            double kx = ex, ky = ey + 32 * s, kfs = 13 * ss;
            foreach (var g in cfg.GroupColors.Keys)
            {
                var gc = ColorOf(g);
                dc.PushOpacity(OpacityOf(g));
                dc.DrawEllipse(new SolidColorBrush(gc), new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), Outlined(g) ? 2 * s : 1), new Point(kx + 5 * s, ky + kfs * 0.7), 5 * s, 5 * s);
                kx += 14 * s;
                kx += GroupText(dc, g, g, kx, ky, kfs, Color.FromArgb(230, gc.R, gc.G, gc.B)) + 16 * s;
                dc.Pop();
            }
        }

        // --- header
        double cx = w / 2;
        string hint = string.IsNullOrEmpty(status) ? (cfg.MouseWheelMenu ? "F5 = phrase wheel  ·  " : "") + (cfg.SayHomeKey ? "Home" : cfg.SayMouseButton > 0 ? "thumb button" : "F6") + " = type to speak  ·  " + (cfg.HelperEnabled ? "Page Down = teach helper  ·  " : "") + "F10 free mouse  ·  F9 window/full  ·  F8 hide  ·  Ctrl+F8 quit" : status;
        if (cfg.ShowHeader)
        {
            Text(dc, "Other Side Sound  ·  sounds read from the game", cx, 10 * s, 13 * s, Color.FromArgb(150, 255, 255, 255), true, 1);
            Text(dc, hint, cx, 28 * s, 11 * s, Color.FromArgb(120, 255, 255, 255), false, 1);
        }
        // DebugClock: draw time of this frame, to measure how late the overlay reaches the screen against a screenshot's time
        if (cfg.DebugClock) Text(dc, DateTime.Now.ToString("HH:mm:ss.f"), cx, 44 * s, 14 * s, Colors.Yellow, true, 1);
    }

    (float ang, double dist, double up) Where(EventItem ev)
    {
        lock (state.Lock) return state.Where(ev.X, ev.Y, ev.Z, cfg.EyeHeightM);
    }

    static string DistText(double m) => m < 0 ? "" : m < 1.5 ? "close" : $"{m:F0} m";

    string FloorText(double up) => up > cfg.FloorDiffM ? "  ▲ above" : up < -cfg.FloorDiffM ? "  ▼ below" : "";

    /// Arrow pointing the exact way to a sound: up = where you look, right = your right, down = behind you.
    static void DrawArrow(DrawingContext dc, double cx, double cy, double r, double deg, Color color)
    {
        var tip = P(cx, cy, r, deg);
        var tail = P(cx, cy, r * 0.75, deg + 180);
        var l = P(cx, cy, r * 0.75, deg - 140);
        var rr = P(cx, cy, r * 0.75, deg + 140);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(tip, true, true);
            c.LineTo(rr, true, false);
            c.LineTo(new Point((tip.X + tail.X * 2) / 3, (tip.Y + tail.Y * 2) / 3), true, false);
            c.LineTo(l, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(new SolidColorBrush(color), new Pen(new SolidColorBrush(Color.FromArgb(color.A, 0, 0, 0)), Math.Max(1.5, r * 0.15)) { LineJoin = PenLineJoin.Round }, g);
    }

}
