using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace PhasmoSound;

/// Watches the game picture during a hunt for the ghost's flicker: a patch of the screen that pops in and
/// out while the rest of the picture stays put. Times the gaps between pops; they alternate visible / hidden,
/// and the shorter set is "visible". Normal ghosts: visible ~0.1-0.3 s, hidden ~0.3-1 s. Phantom hides
/// longer (1-2 s). Results go to state (box at top + marker where it blinked) and to log.txt ("BLINK").
public sealed class GhostWatch
{
    const int W = 320, H = 180, Cell = 10, CW = W / Cell, CH = H / Cell;
    readonly Config cfg;
    readonly OverlayState state;
    readonly Func<IntPtr> windowHandle;
    volatile bool active;
    Thread? thread;

    readonly string framesDir;
    public GhostWatch(Config cfg, OverlayState state, Func<IntPtr> windowHandle, string framesDir) { this.cfg = cfg; this.state = state; this.windowHandle = windowHandle; this.framesDir = framesDir; }

    /// Turn watching on while a hunt is heard, off after.
    public void SetActive(bool on)
    {
        if (on == active) return;
        active = on;
        Log.Write(on ? "ghost watch: hunt, watching the screen for blinks" : "ghost watch: stopped");
        if (on && (thread == null || !thread.IsAlive))
        {
            thread = new Thread(Loop) { IsBackground = true, Name = "ghostwatch", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }
    }

    sealed class Blink { public DateTime T; public double X, Y; public int Cells; }

    void Loop()
    {
        IntPtr scr = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(scr);
        var bi = new BITMAPINFO { biSize = 40, biWidth = W, biHeight = -H, biPlanes = 1, biBitCount = 32 };
        IntPtr dib = CreateDIBSection(mem, ref bi, 0, out var bits, IntPtr.Zero, 0);
        SelectObject(mem, dib); SetStretchBltMode(mem, 3);
        var px = new int[W * H];
        var prev = new float[W * H]; var cur = new float[W * H];
        bool havePrev = false;
        var cellDiff = new float[CW * CH];
        var blinks = new List<Blink>();
        IntPtr ovl = windowHandle();
        // the hunt's picture, 320x180 grey, so blink timing can be checked by eye later (frames.bin + times)
        System.IO.FileStream? fout = null; System.IO.StreamWriter? tout = null; int saved = 0; var tStart = DateTime.UtcNow;
        var gray = new byte[W * H];
        if (cfg.SaveHuntFrames)
            try
            {
                System.IO.Directory.CreateDirectory(framesDir);
                string name = System.IO.Path.Combine(framesDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_frames");
                fout = System.IO.File.Create(name + ".gray320x180"); tout = new System.IO.StreamWriter(name + ".txt");
            }
            catch (Exception ex) { Log.Write("ghost watch frames: " + ex.Message); }
        try
        {
            if (ovl != IntPtr.Zero) SetWindowDisplayAffinity(ovl, 0x11);   // keep our own drawings out of the capture
            while (active)
            {
                var t0 = DateTime.UtcNow;
                if (!GetWindowRect(ovl, out var r) || r.R - r.L < 200) { Thread.Sleep(200); continue; }
                int gw = r.R - r.L, gh = r.B - r.T;
                StretchBlt(mem, 0, 0, W, H, scr, r.L, r.T, gw, gh, 0x00CC0020);
                Marshal.Copy(bits, px, 0, px.Length);
                for (int i = 0; i < px.Length; i++) { int c = px[i]; cur[i] = ((c >> 16) & 255) * 0.3f + ((c >> 8) & 255) * 0.59f + (c & 255) * 0.11f; }
                if (fout != null && saved < 30 * 45)
                {
                    for (int i = 0; i < gray.Length; i++) gray[i] = (byte)cur[i];
                    fout.Write(gray); tout!.WriteLine((t0 - tStart).TotalSeconds.ToString("F3")); saved++;
                }
                if (havePrev)
                {
                    // mean |change| per 10x10 cell; skip the outer band (edge glows, captions, menus)
                    Array.Clear(cellDiff);
                    for (int y = 0; y < H; y++)
                        for (int x = 0; x < W; x++)
                            cellDiff[(y / Cell) * CW + x / Cell] += Math.Abs(cur[y * W + x] - prev[y * W + x]);
                    var inner = new List<(int cx, int cy, float d)>();
                    for (int cy = 2; cy < CH - 2; cy++)
                        for (int cx = 2; cx < CW - 2; cx++)
                            inner.Add((cx, cy, cellDiff[cy * CW + cx] / (Cell * Cell)));
                    var sorted = inner.Select(c => c.d).OrderBy(d => d).ToList();
                    float median = sorted[sorted.Count / 2];
                    float thr = Math.Max(cfg.BlinkMinChange, median * 5);
                    var hot = inner.Where(c => c.d > thr).ToList();
                    // a blink: a few cells change a lot while the rest of the picture stays still (not you turning)
                    if (median < cfg.BlinkMaxBackground && hot.Count >= 2 && hot.Count <= inner.Count * 0.15)
                    {
                        double mx = hot.Average(c => c.cx), my = hot.Average(c => c.cy);
                        double spread = hot.Average(c => Math.Abs(c.cx - mx) + Math.Abs(c.cy - my));
                        if (spread < 6)
                        {
                            var b = new Blink { T = t0, X = (mx + 0.5) / CW, Y = (my + 0.5) / CH, Cells = hot.Count };
                            blinks.Add(b);
                            lock (state.Lock) { state.GhostMarkX = b.X; state.GhostMarkY = b.Y; state.GhostMarkUntil = t0.AddSeconds(0.6); }
                            Analyse(blinks, t0);
                        }
                    }
                }
                (prev, cur) = (cur, prev); havePrev = true;
                blinks.RemoveAll(b => (t0 - b.T).TotalSeconds > 8);
                int wait = 33 - (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                if (wait > 0) Thread.Sleep(wait);
            }
        }
        catch (Exception ex) { Log.Write("ghost watch: " + ex.Message); }
        finally
        {
            if (ovl != IntPtr.Zero) SetWindowDisplayAffinity(ovl, 0);
            fout?.Dispose(); tout?.Dispose();
            if (saved > 0) Log.Write($"ghost watch: saved {saved} frames");
            DeleteObject(dib); DeleteDC(mem); ReleaseDC(IntPtr.Zero, scr);
            active = false;
        }
    }

    // Pops in the same area come in runs: while the ghost is visible (and moving) the patch changes every
    // frame; while it is hidden nothing changes. Run length = seen, gap between runs = hidden.
    void Analyse(List<Blink> blinks, DateTime now)
    {
        var last = blinks[^1];
        var near = blinks.Where(b => Math.Abs(b.X - last.X) < 0.3 && Math.Abs(b.Y - last.Y) < 0.35).ToList();
        var runs = new List<(DateTime a, DateTime b)>();
        foreach (var b in near)
        {
            if (runs.Count > 0 && (b.T - runs[^1].b).TotalSeconds <= 0.09) runs[^1] = (runs[^1].a, b.T);
            else runs.Add((b.T, b.T));
        }
        var seen = new List<double>(); var hidden = new List<double>();
        for (int i = 0; i < runs.Count; i++)
        {
            if (i > 0)
            {
                double gap = (runs[i].a - runs[i - 1].b).TotalSeconds;
                if (gap > 2.5) { seen.Clear(); hidden.Clear(); } else hidden.Add(gap);
            }
            seen.Add((runs[i].b - runs[i].a).TotalSeconds + 0.033);
        }
        // only when a new run has just started: the run before it is complete, the new one is not
        if (runs[^1].a != last.T) return;
        if (seen.Count > 0) seen.RemoveAt(seen.Count - 1);
        if (hidden.Count < 3 || seen.Count < 3) return;
        double vis = seen.OrderBy(x => x).ElementAt(seen.Count / 2), hid = hidden.OrderBy(x => x).ElementAt(hidden.Count / 2);
        if (vis < 0.07)
        {
            // a ghost standing still only changes the picture when it pops in and when it pops out:
            // the gaps then alternate seen / hidden, and the shorter set is "seen"
            var ga = hidden.Where((_, i) => i % 2 == 0).OrderBy(g => g).ToList();
            var gb = hidden.Where((_, i) => i % 2 == 1).OrderBy(g => g).ToList();
            if (ga.Count == 0 || gb.Count == 0) return;
            double ma = ga[ga.Count / 2], mb = gb[gb.Count / 2];
            vis = Math.Min(ma, mb); hid = Math.Max(ma, mb);
        }
        string guess = hid >= cfg.BlinkPhantomHiddenSec ? "long hidden: PHANTOM?"
                     : hid <= cfg.BlinkOniHiddenSec ? "short hidden (shows a lot): ONI?"
                     : "normal blinking";
        lock (state.Lock) { state.BlinkVisible = (float)vis; state.BlinkHidden = (float)hid; state.BlinkGuess = guess; state.BlinkUntil = now.AddSeconds(4); }
        Log.Write($"BLINK seen={vis:F2}s hidden={hid:F2}s runs={runs.Count} -> {guess} at {last.X:F2},{last.Y:F2} cells={last.Cells}");
    }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint a);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int m);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bi, uint u, out IntPtr bits, IntPtr sec, uint off);
    struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFO { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant, pad; }
}
