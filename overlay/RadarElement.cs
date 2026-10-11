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

        float[] chDb; float loud, dirA, dirC; bool surround; int ch; string dev, status, toast; DateTime toastUntil;
        List<OnsetMark> onsets; List<Detection> cur; List<EventItem> evs; List<StepMark> steps;
        lock (state.Lock)
        {
            chDb = (float[])state.ChannelDb.Clone(); loud = state.LoudDb; dirA = state.DirAngle; dirC = state.DirConf;
            surround = state.Surround; ch = state.Channels; dev = state.DeviceName; status = state.Status;
            toast = state.Toast; toastUntil = state.ToastUntil;
            onsets = state.Onsets.ToList(); cur = state.Current.ToList(); evs = state.Events.ToList(); steps = state.Steps.ToList();
        }
        var now = DateTime.UtcNow;

        // --- footprints for footsteps that are not yours, on the side they come from
        if (cfg.StepIcon)
        {
            double size = cfg.StepIconSize * s;
            double thick = cfg.EdgeThickness * s;
            foreach (var side in new[] { -1, 1 })
            {
                var m = steps.Where(st => Math.Sign(st.Angle) == side && (now - st.Time).TotalSeconds < 1.5).OrderByDescending(st => st.Time).FirstOrDefault();
                if (m == null) continue;
                double age = (now - m.Time).TotalSeconds;
                float alpha = (float)(1 - age / 1.5) * (0.55f + 0.45f * m.Level);
                double x = side < 0 ? thick * 0.5 + size * 0.9 : w - thick * 0.5 - size * 0.9;
                double y = h / 2;
                if (surround) { var (pt, edge) = EdgeHit(w, h, m.Angle); if (edge == 0) y = thick * 0.5 + size; else if (edge == 2) y = h - thick * 0.5 - size; else y = pt.Y; x = edge == 3 ? thick * 0.5 + size * 0.9 : edge == 1 ? w - thick * 0.5 - size * 0.9 : pt.X; }
                DrawFootprints(dc, x, y, size, Color.FromArgb((byte)(255 * Math.Clamp(alpha, 0f, 1f)), 255, 150, 40));
            }
        }

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

        // --- step meter: how fast the footsteps come, and what that speed means during a hunt
        float stepRate, stepTrend; bool stepHunt; DateTime stepUntil;
        lock (state.Lock) { stepRate = state.StepRate; stepTrend = state.StepTrend; stepHunt = state.StepHunting; stepUntil = state.StepUntil; }
        if (cfg.StepMeter && stepRate > 0 && now < stepUntil)
        {
            float rel = stepRate / Math.Max(0.1f, cfg.GhostNormalStepsPerSec);
            var (word, col) = rel < 0.75f ? ("SLOW", Color.FromRgb(120, 200, 255))
                            : rel < 1.3f ? ("NORMAL", Color.FromRgb(230, 230, 230))
                            : rel < 1.7f ? ("FAST", Color.FromRgb(255, 170, 40))
                            : ("VERY FAST", Color.FromRgb(255, 70, 60));
            string trend = stepTrend > 1.2f ? "  ▲ speeding up" : stepTrend < 0.83f ? "  ▼ slowing" : "";
            string line = $"{(stepHunt ? "GHOST STEPS" : "STEPS")}  {stepRate:F1}/s  {word}{trend}";
            string stepHint = !stepHunt ? "" : rel < 0.75f ? "slow: Revenant (not chasing), Deogen (close), old Thaye, warm-room Hantu"
                        : rel >= 1.7f && stepTrend <= 1.2f ? "fast from the start: Revenant chasing, young Thaye, Deogen far, Moroi, Jinn, Raiju, cold Hantu"
                        : stepTrend > 1.2f ? "speeding up while it sees you = normal for most ghosts (not Hantu)" : "";
            double fs = 22 * s, ty = 60 * s + 50 * s;
            var ft = FT(line, fs, col, true);
            double bw = Math.Max(ft.Width, stepHint.Length > 0 ? FT(stepHint, 14 * s, Colors.White, false).Width : 0) + 32 * s;
            double bh = fs + 20 * s + (stepHint.Length > 0 ? 22 * s : 0);
            double a = Math.Clamp((stepUntil - now).TotalSeconds / 0.5, 0, 1);
            dc.PushOpacity(a);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 20, 20, 24)), new Pen(new SolidColorBrush(col), 2), new Rect(w / 2 - bw / 2, ty, bw, bh), 10 * s, 10 * s);
            Text(dc, line, w / 2, ty + 9 * s, fs, col, true, 1);
            if (stepHint.Length > 0) Text(dc, stepHint, w / 2, ty + fs + 14 * s, 14 * s, Color.FromArgb(220, 255, 255, 255), false, 1);
            dc.Pop();
        }
        // --- ghost watch: red corners where the screen just blinked, and the blink timing box
        double gmx, gmy; DateTime gmUntil, blUntil; float blVis, blHid; string blGuess;
        lock (state.Lock) { gmx = state.GhostMarkX; gmy = state.GhostMarkY; gmUntil = state.GhostMarkUntil; blVis = state.BlinkVisible; blHid = state.BlinkHidden; blGuess = state.BlinkGuess; blUntil = state.BlinkUntil; }
        if (now < gmUntil)
        {
            double gx = gmx * w, gy = gmy * h, half = 60 * s, len = 22 * s;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp((gmUntil - now).TotalSeconds / 0.6, 0, 1)), 255, 60, 60)), 4 * s);
            foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
            {
                var c = new Point(gx + sx * half, gy + sy * half);
                dc.DrawLine(pen, c, new Point(c.X - sx * len, c.Y));
                dc.DrawLine(pen, c, new Point(c.X, c.Y - sy * len));
            }
        }
        if (now < blUntil && blGuess.Length > 0)
        {
            string bl = $"BLINK  seen {blVis:F2}s / hidden {blHid:F2}s  ·  {blGuess}";
            double fs2 = 20 * s, by2 = 60 * s + 50 * s + 80 * s;
            var bft = FT(bl, fs2, Colors.White, true);
            var bcol = Color.FromRgb(255, 90, 90);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 20, 20, 24)), new Pen(new SolidColorBrush(bcol), 2), new Rect(w / 2 - bft.Width / 2 - 16 * s, by2, bft.Width + 32 * s, fs2 + 20 * s), 10 * s, 10 * s);
            Text(dc, bl, w / 2, by2 + 9 * s, fs2, Colors.White, true, 1);
        }
        // --- hunt indicator (top-right): the overlay thinks a hunt is on; Ctrl+3 tells it the hunt is over
        bool huntOn; lock (state.Lock) huntOn = state.HuntActive;
        if (huntOn && cfg.HuntKey)
        {
            string ht = "HUNT  ·  Ctrl+3 = over";
            double hfs = 18 * s; var hft = FT(ht, hfs, Colors.White, true);
            double hx = w - cfg.EdgeThickness * s - 20 * s - hft.Width - 24 * s, hy = 60 * s;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(210, 150, 20, 20)), new Pen(new SolidColorBrush(Color.FromRgb(255, 80, 80)), 2), new Rect(hx, hy, hft.Width + 24 * s, hfs + 16 * s), 8 * s, 8 * s);
            Text(dc, ht, hx + 12 * s, hy + 7 * s, hfs, Colors.White, true, 0);
        }

        // --- stopwatch (Ctrl+1 start, Ctrl+2 reset): big time top-left, with the smudge marks (Demon 60 s, normal 90 s, Spirit 180 s)
        DateTime? swStart; TimeSpan swEl;
        lock (state.Lock) { swStart = state.StopwatchStart; swEl = state.StopwatchElapsed; }
        if (cfg.Stopwatch && (swStart != null || swEl > TimeSpan.Zero))
        {
            var el = swStart is DateTime st0 ? swEl + (now - st0) : swEl;
            string tt = $"{(int)el.TotalMinutes}:{el.Seconds:00}";
            double fs3 = 40 * s, x0 = cfg.EdgeThickness * s + 20 * s, y0 = 60 * s;
            var tcol = swStart != null ? Color.FromRgb(120, 255, 160) : Color.FromRgb(255, 220, 90);
            var tft = FT(tt, fs3, tcol, true);
            double bw3 = Math.Max(tft.Width, 230 * s) + 28 * s, bh3 = fs3 + 46 * s;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 20, 20, 24)), new Pen(new SolidColorBrush(tcol), 2), new Rect(x0, y0, bw3, bh3), 10 * s, 10 * s);
            Text(dc, tt, x0 + 14 * s, y0 + 6 * s, fs3, tcol, true, 0);
            double mx = x0 + 14 * s;
            foreach (var (sec, name) in new[] { (60, "Demon 60"), (90, "90"), (180, "Spirit 180") })
            {
                bool passed = el.TotalSeconds >= sec;
                mx += Text(dc, (passed ? "✓ " : "") + name, mx, y0 + fs3 + 16 * s, 14 * s, passed ? Color.FromRgb(255, 120, 120) : Color.FromArgb(200, 255, 255, 255), passed, 0) + 14 * s;
            }
            Text(dc, "Ctrl+2 reset", x0 + bw3 - 10 * s, y0 + 8 * s, 11 * s, Color.FromArgb(170, 255, 255, 255), false, 2);
        }
        float lt = Level(loud);
        bool ring = string.Equals(cfg.Style, "ring", StringComparison.OrdinalIgnoreCase);

        double chipY;
        if (ring) chipY = DrawRing(dc, w, h, s, chDb, ch, surround, lt, dirA, dirC, onsets, now);
        else chipY = DrawEdges(dc, w, h, s, chDb, ch, surround, lt, dirA, dirC, onsets, now);

        // --- current sounds (chips)
        double cx = w / 2;
        double ss = s * cfg.SoundTextScale;   // sound text only; captions keep CaptionFontSize
        double chipSize = 17 * ss;
        var chips = cur.Take(4).ToList();
        if (chips.Count > 0)
        {
            var widths = chips.Select(d => FT($"{d.Label}  {(int)(d.Score * 100)}%", chipSize, Colors.White, true).Width + 22 * s).ToList();
            double total = widths.Sum() + (chips.Count - 1) * 8 * s;
            double x = cx - total / 2;
            for (int i = 0; i < chips.Count; i++)
            {
                var d = chips[i];
                var col = ColorOf(d.Category);
                dc.PushOpacity(OpacityOf(d.Category));
                var bg = Color.FromRgb(col.R, col.G, col.B);   // solid (Kaalob: no see-through)
                dc.DrawRoundedRectangle(new SolidColorBrush(bg), new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1),
                    new Rect(x, chipY, widths[i], chipSize + 12 * s), 8 * s, 8 * s);
                // white / yellow chips get black writing, darker chips white
                bool light = cfg.GroupColors.Count > 0 && 0.299 * col.R + 0.587 * col.G + 0.114 * col.B > 170;
                if (light) dc.DrawText(FT($"{d.Label}  {(int)(d.Score * 100)}%", chipSize, Color.FromRgb(10, 10, 12), true), new Point(x + 11 * s, chipY + 5 * s));
                else Text(dc, $"{d.Label}  {(int)(d.Score * 100)}%", x + 11 * s, chipY + 5 * s, chipSize, Colors.White, true);
                dc.Pop();
                x += widths[i] + 8 * s;
            }
        }

        // --- loudness bar
        double barW = 220 * s, barH = 7 * s, barY = chipY + chipSize + 22 * s;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), null, new Rect(cx - barW / 2, barY, barW, barH), 3, 3);
        if (lt > 0)
            dc.DrawRoundedRectangle(new SolidColorBrush(LevelColor(lt)), null, new Rect(cx - barW / 2, barY, barW * lt, barH), 3, 3);

        // --- event log (bottom-left)
        double rowH = 34 * ss, ex = 28 * s, ey = h - 46 * s;
        if (!ring) { ex += cfg.EdgeThickness * 0.35 * s; ey -= cfg.EdgeThickness * 0.35 * s; }
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
            x += Text(dc, ArrowFor(ev.Angle, surround), x, y - 2 * ss, 24 * ss, Color.FromArgb((byte)(255 * alpha), 255, 255, 255), true) + 10 * s;
            var labelCol = ev.Pending && ev.Label.EndsWith("!") ? Colors.White : col;
            x += GroupText(dc, ev.Category, ev.Label, x, y + 1 * ss, 21 * ss, Color.FromArgb((byte)(255 * alpha), labelCol.R, labelCol.G, labelCol.B)) + 12 * s;
            Text(dc, age < 1 ? "now" : $"{(int)age}s", x, y + 6 * ss, 14 * ss, Color.FromArgb((byte)(200 * alpha), 220, 220, 220));
            dc.Pop();
            row++;
        }

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
        string mode = surround ? $"{(ch == 8 ? "7.1" : ch == 6 ? "5.1" : ch + "ch")} SURROUND" : "STEREO (left / right only)";
        string hint = string.IsNullOrEmpty(status) ? (cfg.MouseWheelMenu ? "F5 = phrase wheel  ·  " : "") + (cfg.SayHomeKey ? "Home" : cfg.SayMouseButton > 0 ? "thumb button" : "F6") + " = type to speak  ·  " + (cfg.HelperEnabled ? "Page Down = teach helper  ·  " : "") + "F10 free mouse  ·  F9 window/full  ·  F8 hide  ·  Ctrl+F8 quit" : status;
        if (!ring && surround) hint = "top = front   bottom = back   ·   " + hint;
        // ShowHeader off: Kaalob 2026-10-11 "hide the top thing text, I don't need to see it"
        if (cfg.ShowHeader)
        {
            Text(dc, $"Other Side Sound  ·  {mode}  ·  {dev}", cx, 10 * s, 13 * s, Color.FromArgb(150, 255, 255, 255), true, 1);
            Text(dc, hint, cx, 28 * s, 11 * s, Color.FromArgb(120, 255, 255, 255), false, 1);
        }
        // DebugClock: draw time of this frame, to measure how late the overlay reaches the screen against a screenshot's time
        if (cfg.DebugClock) Text(dc, DateTime.Now.ToString("HH:mm:ss.f"), cx, 44 * s, 14 * s, Colors.Yellow, true, 1);
    }

    /// A pair of footprints centred on (x, y); size = height of one foot.
    static void DrawFootprints(DrawingContext dc, double x, double y, double size, Color color)
    {
        var fill = new SolidColorBrush(color);
        var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(color.A * 0.85), 0, 0, 0)), Math.Max(1, size * 0.03));
        void Foot(double fx, double fy, int mirror)
        {
            double L = size, W = size * 0.45;
            // sole (slightly egg-shaped: bigger at the ball of the foot)
            dc.DrawEllipse(fill, pen, new Point(fx, fy + L * 0.12), W * 0.42, L * 0.36);
            dc.DrawEllipse(fill, null, new Point(fx, fy - L * 0.08), W * 0.46, L * 0.22);
            // toes: big toe on the inner side
            double[] tr = { 0.17, 0.12, 0.11, 0.10, 0.09 };
            for (int i = 0; i < 5; i++)
            {
                double tx = fx + mirror * (-W * 0.34 + i * W * 0.19);
                double ty = fy - L * 0.42 + (i == 0 ? L * 0.02 : i * L * 0.02);
                dc.DrawEllipse(fill, pen, new Point(tx, ty), W * tr[i], W * tr[i]);
            }
        }
        Foot(x - size * 0.28, y + size * 0.12, -1);
        Foot(x + size * 0.28, y - size * 0.12, 1);
    }

    /// Edge style. Returns the y where the chips should start.
    double DrawEdges(DrawingContext dc, double w, double h, double s, float[] chDb, int ch, bool surround,
                     float lt, float dirA, float dirC, List<OnsetMark> onsets, DateTime now)
    {
        double thick = cfg.EdgeThickness * s;
        var angles = Direction.Angles(ch);

        // --- per-channel glows
        if (ch == 2)
        {
            DrawChannelEdge(dc, w, h, new Point(0, h / 2), 3, h * 0.62, thick, chDb.Length > 0 ? chDb[0] : -100);
            DrawChannelEdge(dc, w, h, new Point(w, h / 2), 1, h * 0.62, thick, chDb.Length > 1 ? chDb[1] : -100);
            Text(dc, "L", 14 * s, h / 2 - 12 * s, 20 * s, Color.FromArgb(130, 255, 255, 255), true, 0);
            Text(dc, "R", w - 14 * s, h / 2 - 12 * s, 20 * s, Color.FromArgb(130, 255, 255, 255), true, 2);
        }
        else if (angles != null)
        {
            double frac = ch >= 8 ? 0.30 : ch == 6 ? 0.36 : 0.45;
            for (int c = 0; c < angles.Length && c < chDb.Length; c++)
            {
                if (float.IsNaN(angles[c])) continue;
                var (pt, edge) = EdgeHit(w, h, angles[c]);
                double halfLen = (edge == 0 || edge == 2 ? w : h) * (angles[c] == 0 ? 0.18 : frac);
                DrawChannelEdge(dc, w, h, pt, edge, halfLen, thick, chDb[c]);
            }
            Text(dc, "FRONT", w / 2, 44 * s, 12 * s, Color.FromArgb(120, 255, 255, 255), true, 1);
            Text(dc, "BACK", w / 2, h - 24 * s, 12 * s, Color.FromArgb(120, 255, 255, 255), true, 1);
        }

        // --- dominant direction pointer: small triangle just inside the edge
        if (lt > 0.08f && dirC > 0.12f)
        {
            var (pt, edge) = EdgeHit(w, h, dirA);
            double size = (12 + 20 * lt) * s;
            double inset = thick * 0.45;
            Point tip, b1, b2;
            switch (edge)
            {
                case 0: tip = new Point(pt.X, inset + size); b1 = new Point(pt.X - size * 0.6, inset); b2 = new Point(pt.X + size * 0.6, inset); break;
                case 2: tip = new Point(pt.X, h - inset - size); b1 = new Point(pt.X - size * 0.6, h - inset); b2 = new Point(pt.X + size * 0.6, h - inset); break;
                case 1: tip = new Point(w - inset - size, pt.Y); b1 = new Point(w - inset, pt.Y - size * 0.6); b2 = new Point(w - inset, pt.Y + size * 0.6); break;
                default: tip = new Point(inset + size, pt.Y); b1 = new Point(inset, pt.Y - size * 0.6); b2 = new Point(inset, pt.Y + size * 0.6); break;
            }
            var g = new StreamGeometry();
            using (var c = g.Open()) { c.BeginFigure(tip, true, true); c.LineTo(b1, false, false); c.LineTo(b2, false, false); }
            g.Freeze();
            var col = LevelColor(lt); col.A = (byte)(120 + 135 * Math.Min(1f, dirC * 1.5f));
            dc.DrawGeometry(new SolidColorBrush(col), new Pen(Brushes.Black, 1), g);
        }

        // --- sudden-sound flashes: bright band on the edge the sound came from, growing and fading
        foreach (var o in onsets)
        {
            double age = (now - o.Time).TotalSeconds;
            if (age < 0 || age > 1.0) continue;
            float k = (float)(1 - age / 1.0);
            float str = Math.Clamp((o.Strength - cfg.OnsetDb) / 12f, 0.3f, 1f);
            var col = LevelColor(0.45f + 0.55f * str); col.A = (byte)(255 * k * str);
            double grow = 1 + age * 1.6;
            if (ch == 2 && o.Conf < 0.12f)
            {
                // centered sound in stereo: no side information, pulse the whole border
                var pen = new Pen(new SolidColorBrush(col), thick * 0.5 * grow);
                dc.DrawRectangle(null, pen, new Rect(0, 0, w, h));
            }
            else
            {
                var (pt, edge) = EdgeHit(w, h, ch == 2 ? (o.Angle < 0 ? -90 : 90) : o.Angle);
                double halfLen = (edge == 0 || edge == 2 ? w : h) * 0.22 * grow;
                EdgeBar(dc, w, h, pt, edge, halfLen, thick * 0.55 * grow, col);
                EdgeGlow(dc, pt, edge, halfLen * 1.3, thick * 1.4 * grow, Color.FromArgb((byte)(col.A * 0.7), col.R, col.G, col.B));
            }
        }

        return h - thick - 20 * s - 17 * s - 12 * s - 22 * s - 7 * s;
    }

    void DrawChannelEdge(DrawingContext dc, double w, double h, Point pt, int edge, double halfLen, double thick, float db)
    {
        float t = Level(db);
        if (t <= 0.01f) return;
        var col = LevelColor(t);
        col.A = (byte)(35 + 220 * t);
        EdgeGlow(dc, pt, edge, halfLen, thick * (0.9 + 0.9 * t), col);
        if (t > 0.35f)
        {
            var core = col; core.A = (byte)(200 * (t - 0.35f) / 0.65f);
            EdgeBar(dc, w, h, pt, edge, halfLen * 0.9, thick * 0.22, core);
        }
    }

    /// Ring style (original). Returns the y where the chips should start.
    double DrawRing(DrawingContext dc, double w, double h, double s, float[] chDb, int ch, bool surround,
                    float lt, float dirA, float dirC, List<OnsetMark> onsets, DateTime now)
    {
        double cx = w / 2, cy = h / 2;
        double R = Math.Min(w, h) * cfg.RingRadius;
        double thick = cfg.RingThickness * s;

        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1.5), new Point(cx, cy), R, R);
        var angles = Direction.Angles(ch);
        if (ch == 2)
        {
            DrawLevelArc(dc, cx, cy, R, -160, -20, chDb.Length > 0 ? chDb[0] : -100, thick);
            DrawLevelArc(dc, cx, cy, R, 20, 160, chDb.Length > 1 ? chDb[1] : -100, thick);
            Text(dc, "L", cx - R - 26 * s, cy - 11 * s, 18 * s, Color.FromArgb(150, 255, 255, 255), true, 1);
            Text(dc, "R", cx + R + 26 * s, cy - 11 * s, 18 * s, Color.FromArgb(150, 255, 255, 255), true, 1);
        }
        else if (angles != null)
        {
            double half = ch >= 8 ? 26 : ch == 6 ? 32 : 42;
            for (int c = 0; c < angles.Length && c < chDb.Length; c++)
            {
                if (float.IsNaN(angles[c])) continue;
                double hh = angles[c] == 0 ? 15 : half;
                DrawLevelArc(dc, cx, cy, R, angles[c] - hh, angles[c] + hh, chDb[c], thick);
            }
            Text(dc, "FRONT", cx, cy - R - thick - 22 * s, 12 * s, Color.FromArgb(130, 255, 255, 255), true, 1);
            Text(dc, "BACK", cx, cy + R + thick + 6 * s, 12 * s, Color.FromArgb(130, 255, 255, 255), true, 1);
        }

        if (lt > 0.08f && dirC > 0.12f)
        {
            double size = (10 + 18 * lt) * s;
            double rr = R + thick / 2 + 6 * s;
            var tip = P(cx, cy, rr, dirA);
            var b1 = P(cx, cy, rr + size, dirA - 7);
            var b2 = P(cx, cy, rr + size, dirA + 7);
            var g = new StreamGeometry();
            using (var c = g.Open()) { c.BeginFigure(tip, true, true); c.LineTo(b1, false, false); c.LineTo(b2, false, false); }
            g.Freeze();
            var col = LevelColor(lt); col.A = (byte)(120 + 135 * Math.Min(1f, dirC * 1.5f));
            dc.DrawGeometry(new SolidColorBrush(col), new Pen(Brushes.Black, 1), g);
        }

        foreach (var o in onsets)
        {
            double age = (now - o.Time).TotalSeconds;
            if (age < 0 || age > 1.2) continue;
            float k = (float)(1 - age / 1.2);
            float str = Math.Clamp((o.Strength - cfg.OnsetDb) / 12f, 0.25f, 1f);
            var col = LevelColor(0.4f + 0.6f * str); col.A = (byte)(230 * k * str);
            double spread = (surround || ch == 2 ? 28 : 40) + age * 25;
            double rr = R + thick / 2 + 8 * s + age * 60 * s;
            if (ch == 2 && o.Conf < 0.12f) Arc(dc, cx, cy, rr, 0, 360, col, 4 * s);
            else Arc(dc, cx, cy, rr, o.Angle - spread, o.Angle + spread, col, (5 + 5 * str) * s);
        }

        return cy + R + thick + (surround ? 26 : 12) * s;
    }

    void DrawLevelArc(DrawingContext dc, double cx, double cy, double R, double a0, double a1, float db, double thick)
    {
        float t = Level(db);
        var col = LevelColor(t);
        col.A = (byte)(40 + 215 * t);
        Arc(dc, cx, cy, R, a0, a1, col, thick * (0.6 + 0.6 * t));
        if (t > 0.6f)
        {
            var glow = col; glow.A = (byte)(90 * (t - 0.6f) / 0.4f);
            Arc(dc, cx, cy, R, a0 - 4, a1 + 4, glow, thick * 2.2);
        }
    }
}
