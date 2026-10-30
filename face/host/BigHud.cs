using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// Left Arrow: my own hex camera, long and wide in the middle of the game window. It fades in,
// stays 6 seconds, fades out. Hold Left Arrow 0.5 s and it stays up (locked). A tap while
// it is up closes it. Only on my screen: it follows big_self.txt, which my game writes
// as "tap-..." or "lock-..." on the key.
// Click-through, never takes focus, only drawn while the game window is up.
sealed class BigHud : Form
{
    const double FadeIn = 0.25;
    const double Hold = 6.0;
    const double FadeOut = 0.3;
    // The plate's hex stretched sideways to the camera's own 16:9: up to 96% of the width, 94% of the height.
    const double WideFill = 0.96;
    const double WideAspect = 16.0 / 9.0;
    const double Fill = 0.94;

    readonly System.Windows.Forms.Timer timer = new() { Interval = 33 };
    string lastPress = "";
    DateTime lastRead = DateTime.MinValue;
    DateTime lastFrame = DateTime.MinValue;
    DateTime startedAt = DateTime.MinValue;
    bool showing;
    bool locked;
    DateTime nextPidScan = DateTime.MinValue;
    HashSet<uint> gamePids = new();
    IntPtr found;
    int foundArea;
    readonly EnumProc enumProc;
    Bitmap? mine;

    delegate bool EnumProc(IntPtr h, IntPtr l);

    public BigHud()
    {
        enumProc = OnEnum;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-32000, -32000, 100, 100);
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8; // layered, transparent, toolwindow, noactivate, topmost
            return cp;
        }
    }

    // The first read only learns the starting value, so an old press never shows it.
    // Returns "tap" or "lock" for a new key press, "" for nothing new.
    string Pressed()
    {
        string text;
        try
        {
            var f = Path.Combine(ShareHexForm.PlateDir(), "big_self.txt");
            if (!File.Exists(f)) return "";
            text = File.ReadAllText(f).Trim();
        }
        catch { return ""; }
        if (text.Length == 0) return "";
        if (lastPress.Length == 0)
        {
            lastPress = text;
            return "";
        }
        if (text == lastPress) return "";
        lastPress = text;
        return text.StartsWith("lock") ? "lock" : "tap";
    }

    void LoadFrame()
    {
        try
        {
            var f = Path.Combine(ShareHexForm.PlateDir(), "local.jpg");
            var info = new FileInfo(f);
            if (!info.Exists || (DateTime.UtcNow - info.LastWriteTimeUtc).TotalSeconds > 4) { mine?.Dispose(); mine = null; return; }
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            ms.Position = 0;
            using var raw = new Bitmap(ms);
            var next = new Bitmap(raw);
            mine?.Dispose();
            mine = next;
        }
        catch { }
    }

    bool OnEnum(IntPtr h, IntPtr l)
    {
        GetWindowThreadProcessId(h, out uint pid);
        if (!gamePids.Contains(pid) || !IsWindowVisible(h) || IsIconic(h)) return true;
        if (!GetClientRect(h, out RECT r)) return true;
        int area = r.Right * r.Bottom;
        if (area > foundArea)
        {
            foundArea = area;
            found = h;
        }
        return true;
    }

    bool GameClient(out Rectangle client)
    {
        client = Rectangle.Empty;
        if (DateTime.UtcNow >= nextPidScan)
        {
            nextPidScan = DateTime.UtcNow.AddMilliseconds(500);
            var next = new HashSet<uint>();
            foreach (var p in Process.GetProcessesByName("TheOtherSide-Win64-Shipping"))
            {
                next.Add((uint)p.Id);
                p.Dispose();
            }
            gamePids = next;
        }
        if (gamePids.Count == 0) return false;
        found = IntPtr.Zero;
        foundArea = 0;
        EnumWindows(enumProc, IntPtr.Zero);
        if (found == IntPtr.Zero || !GetClientRect(found, out RECT r)) return false;
        var o = new POINT();
        if (!ClientToScreen(found, ref o)) return false;
        client = new Rectangle(o.X, o.Y, r.Right, r.Bottom);
        return true;
    }

    void Tick()
    {
        var now = DateTime.UtcNow;
        if ((now - lastRead).TotalMilliseconds >= 100)
        {
            lastRead = now;
            var press = Pressed();
            if (press == "lock" && showing && locked)
            {
                // Already locked and the key was pressed again, even held a little long:
                // that press closes it, it does not lock it a second time.
                locked = false;
                startedAt = now.AddSeconds(-(FadeIn + Hold));
            }
            else if (press == "lock")
            {
                // Held: it stays across the screen until the next press.
                if (!showing) startedAt = now;
                showing = true;
                locked = true;
                lastFrame = DateTime.MinValue;
            }
            else if (press == "tap")
            {
                if (showing && (locked || (now - startedAt).TotalSeconds < FadeIn + Hold))
                {
                    // Tapped while it is up: close it.
                    locked = false;
                    startedAt = now.AddSeconds(-(FadeIn + Hold));
                }
                else
                {
                    startedAt = now;
                    showing = true;
                    lastFrame = DateTime.MinValue;
                }
            }
        }
        if (!showing)
        {
            if (Visible) Hide();
            return;
        }
        double t = (now - startedAt).TotalSeconds;
        double total = FadeIn + Hold + FadeOut;
        // Locked: park at the end of the fade-in until a tap ends it.
        if (locked && t >= FadeIn) t = FadeIn;
        if (t >= total || !GameClient(out var client))
        {
            showing = false;
            locked = false;
            if (Visible) Hide();
            return;
        }
        if ((now - lastFrame).TotalMilliseconds >= 60)
        {
            lastFrame = now;
            LoadFrame();
        }
        double a = t < FadeIn ? t / FadeIn : t < FadeIn + Hold ? 1 : 1 - (t - FadeIn - Hold) / FadeOut;
        a = Math.Clamp(a, 0, 1);
        // Long and wide, the same shape as the camera picture (16:9), so none of it is cut
        // off at the top or bottom. Almost the full width, or the full height if that is less.
        int w = (int)(client.Width * WideFill);
        int h = (int)(w / WideAspect);
        if (h > client.Height * Fill)
        {
            h = (int)(client.Height * Fill);
            w = (int)(h * WideAspect);
        }
        int left = client.Left + (client.Width - w) / 2;
        int top = client.Top + (client.Height - h) / 2;
        if (!Visible) Show();
        Draw(w, h, left, top, (byte)Math.Round(255 * a));
    }

    // Same six corners as the hex plate in the game (main.lua).
    static PointF[] HexPoints(int w, int h, float inset)
    {
        float sc = 39f / 22f;
        // Top and bottom corners sit at 10.5 of the 14 half-width (the plate uses 8), so the
        // left and right tips are blunter.
        float[] z = { -10.5f * sc, 10.5f * sc, 14 * sc, 10.5f * sc, -10.5f * sc, -14 * sc };
        float[] x = { 11, 11, -8.5f, -28, -28, -8.5f };
        float kx = (w - 2 * inset) / (28f * sc);
        float ky = (h - 2 * inset) / 39f;
        var pts = new PointF[6];
        for (int i = 0; i < 6; i++)
            pts[i] = new PointF(w / 2f + z[i] * kx, inset + (11 - x[i]) * ky);
        return pts;
    }

    void Draw(int w, int h, int left, int top, byte alpha)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.Clear(Color.Transparent);
            var pts = HexPoints(w, h, 4f);
            using var path = new GraphicsPath();
            path.AddPolygon(pts);
            var state = g.Save();
            g.SetClip(path);
            if (mine != null)
            {
                // Crop the camera picture to the hex's shape, never stretch it.
                double want = (double)w / h;
                int sw = mine.Width, sh = mine.Height;
                if ((double)sw / sh > want) sw = (int)(sh * want); else sh = (int)(sw / want);
                var src = new Rectangle((mine.Width - sw) / 2, (mine.Height - sh) / 2, sw, sh);
                g.DrawImage(mine, new Rectangle(0, 0, w, h), src, GraphicsUnit.Pixel);
            }
            else
            {
                using var dark = new SolidBrush(Color.FromArgb(255, 24, 24, 30));
                g.FillPath(dark, path);
            }
            g.Restore(state);
            using var edge = new Pen(Color.FromArgb(255, 255, 214, 10), 5f) { LineJoin = LineJoin.Round };
            g.DrawPolygon(edge, pts);
        }
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(mem, hbmp);
        var size = new SIZE { cx = w, cy = h };
        var srcPt = new POINT();
        var dst = new POINT { X = left, Y = top };
        var blendFn = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
        if (!IsHandleCreated) CreateHandle();
        UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref srcPt, 0, ref blendFn, 2);
        SetWindowPos(Handle, new IntPtr(-1), left, top, w, h, 0x10 | 0x40);
        SelectObject(mem, old);
        DeleteObject(hbmp);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pptDst, ref SIZE psize,
        IntPtr src, ref POINT pptSrc, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
}
