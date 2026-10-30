using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// Up Arrow: both players' hex cameras slide in from the right edge of the game
// window (upper right corner, one above the other), stay 5 seconds, and slide back out. Either player's
// press counts: it follows slide_self.txt (my game) and slide_remote.txt (the other
// player's game, sent through LiveKit). Click-through, never takes focus, only
// drawn while the game window is up.
sealed class SlideHud : Form
{
    const double SlideIn = 0.45;
    const double Hold = 5.0;
    const double SlideOut = 0.45;
    // The same wide hex as everywhere: the camera's own 16:9, blunt left and right tips.
    const int HexW = 320;
    const int HexH = 180;
    const int Gap = 18;
    const int Margin = 22;
    const int TopMargin = 30;
    const int WinW = HexW + Margin;
    const int WinH = HexH * 2 + Gap + 8;

    readonly System.Windows.Forms.Timer timer = new() { Interval = 33 };
    readonly Dictionary<string, string> last = new();
    DateTime startedAt = DateTime.MinValue;
    bool showing;
    DateTime nextPidScan = DateTime.MinValue;
    HashSet<uint> gamePids = new();
    IntPtr found;
    int foundArea;
    readonly EnumProc enumProc;
    DateTime lastRead = DateTime.MinValue;
    DateTime lastFrames = DateTime.MinValue;
    Bitmap? mine;
    Bitmap? theirs;

    delegate bool EnumProc(IntPtr h, IntPtr l);

    public SlideHud()
    {
        enumProc = OnEnum;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-32000, -32000, WinW, WinH);
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

    // A file changing after the first look is a key press. The first look only
    // learns the starting value, so an old press never slides anything in.
    bool Pressed(string name)
    {
        string text;
        try
        {
            var f = Path.Combine(ShareHexForm.PlateDir(), name);
            if (!File.Exists(f)) return false;
            text = File.ReadAllText(f).Trim();
        }
        catch { return false; }
        if (text.Length == 0) return false;
        if (!last.TryGetValue(name, out var before))
        {
            last[name] = text;
            return false;
        }
        if (before == text) return false;
        last[name] = text;
        return true;
    }

    Bitmap? Load(string name, Bitmap? old)
    {
        try
        {
            var f = Path.Combine(ShareHexForm.PlateDir(), name);
            var info = new FileInfo(f);
            if (!info.Exists || (DateTime.UtcNow - info.LastWriteTimeUtc).TotalSeconds > 4) return null;
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            ms.Position = 0;
            using var raw = new Bitmap(ms);
            var next = new Bitmap(raw);
            old?.Dispose();
            return next;
        }
        catch { return old; }
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
            // Both files are read every time so each one learns its own value.
            bool a = Pressed("slide_self.txt");
            bool b = Pressed("slide_remote.txt");
            if (a || b) Trigger(now);
        }
        if (!showing)
        {
            if (Visible) Hide();
            return;
        }
        double t = (now - startedAt).TotalSeconds;
        double total = SlideIn + Hold + SlideOut;
        if (t >= total || !GameClient(out var client))
        {
            showing = false;
            if (Visible) Hide();
            return;
        }
        if ((now - lastFrames).TotalMilliseconds >= 90)
        {
            lastFrames = now;
            mine = Load("local.jpg", mine);
            theirs = Load("remote.jpg", theirs);
        }
        // 0 = fully in, 1 = fully out.
        double outAmount;
        if (t < SlideIn) outAmount = 1 - EaseOut(t / SlideIn);
        else if (t < SlideIn + Hold) outAmount = 0;
        else outAmount = EaseIn((t - SlideIn - Hold) / SlideOut);
        int left = client.Right - WinW;
        int top = client.Top + TopMargin;
        if (!Visible) Show();
        Draw(outAmount, left, top);
    }

    void Trigger(DateTime now)
    {
        if (!showing)
        {
            startedAt = now;
            showing = true;
            return;
        }
        double t = (now - startedAt).TotalSeconds;
        // Pressed again while it is out: keep it there for another full hold.
        if (t >= SlideIn) startedAt = now.AddSeconds(-SlideIn);
    }

    static double EaseOut(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return 1 - Math.Pow(1 - x, 3);
    }

    static double EaseIn(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * x;
    }

    // Same six corners as the hex plate in the game (main.lua), BigHud and ShapeHud.
    static PointF[] HexPoints(float left, float top)
    {
        float[] fx = { 0.125f, 0.875f, 1f, 0.875f, 0.125f, 0f };
        float[] fy = { 0f, 0f, 0.5f, 1f, 1f, 0.5f };
        var pts = new PointF[6];
        for (int i = 0; i < 6; i++)
            pts[i] = new PointF(left + fx[i] * HexW, top + fy[i] * HexH);
        return pts;
    }

    static void DrawHex(Graphics g, Bitmap? picture, float left, float top)
    {
        var pts = HexPoints(left, top);
        using var path = new GraphicsPath();
        path.AddPolygon(pts);
        var state = g.Save();
        g.SetClip(path);
        if (picture != null)
        {
            // Crop the camera picture to the hex's shape, never stretch it.
            double want = (double)HexW / HexH;
            int sw = picture.Width, sh = picture.Height;
            if ((double)sw / sh > want) sw = (int)(sh * want); else sh = (int)(sw / want);
            var src = new Rectangle((picture.Width - sw) / 2, (picture.Height - sh) / 2, sw, sh);
            g.DrawImage(picture, new Rectangle((int)left, (int)top, HexW, HexH), src, GraphicsUnit.Pixel);
        }
        else
        {
            using var dark = new SolidBrush(Color.FromArgb(255, 24, 24, 30));
            g.FillPath(dark, path);
        }
        g.Restore(state);
        using var edge = new Pen(Color.FromArgb(255, 255, 214, 10), 3f) { LineJoin = LineJoin.Round };
        g.DrawPolygon(edge, pts);
    }

    void Draw(double outAmount, int left, int top)
    {
        using var bmp = new Bitmap(WinW, WinH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.Clear(Color.Transparent);
            // Everything is drawn shifted right; the window ends at the screen edge,
            // so a hex that is "out" is clipped away.
            float shift = (float)(outAmount * WinW);
            DrawHex(g, mine, 2 + shift, 4);
            DrawHex(g, theirs, 2 + shift, 4 + HexH + Gap);
        }
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(mem, hbmp);
        var size = new SIZE { cx = WinW, cy = WinH };
        var src = new POINT();
        var dst = new POINT { X = left, Y = top };
        var blendFn = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        if (!IsHandleCreated) CreateHandle();
        UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blendFn, 2);
        SetWindowPos(Handle, new IntPtr(-1), left, top, WinW, WinH, 0x10 | 0x40);
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
