using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// A small yellow shape in the middle of the game screen so you know which shape
// you just switched to. It shows the shape you are leaving, morphs into the new
// one, holds a moment, then fades out. Own shape only: it follows shape_self.txt,
// the file the game writes when you press Right Arrow. Click-through, never
// takes focus, never shown while the game is not running.
sealed class ShapeHud : Form
{
    const int Size_ = 260;
    const double HoldFrom = 0.15;
    const double Morph = 0.4;
    const double Hold = 0.8;
    const double Fade = 0.35;

    readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
    string lastShape = "";
    DateTime lastRead = DateTime.MinValue;
    DateTime startedAt = DateTime.MinValue;
    bool toHex;
    bool showing;
    DateTime nextPidScan = DateTime.MinValue;
    HashSet<uint> gamePids = new();
    IntPtr found;
    int foundArea;
    readonly EnumProc enumProc;

    delegate bool EnumProc(IntPtr h, IntPtr l);

    public ShapeHud()
    {
        enumProc = OnEnum;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-32000, -32000, Size_, Size_);
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

    // Same rules as the pose reader: the shape is whatever the game wrote last.
    string ReadShape()
    {
        try
        {
            var f = Path.Combine(ShareHexForm.PlateDir(), "shape_self.txt");
            if (File.Exists(f)) return File.ReadAllText(f).Trim() == "hex" ? "hex" : "diamond";
        }
        catch { }
        return "";
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

    bool GameCenter(out int cx, out int cy)
    {
        cx = cy = 0;
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
        cx = o.X + r.Right / 2;
        cy = o.Y + r.Bottom / 2;
        return true;
    }

    void Tick()
    {
        var now = DateTime.UtcNow;
        if ((now - lastRead).TotalMilliseconds >= 100)
        {
            lastRead = now;
            var shape = ReadShape();
            if (shape.Length > 0)
            {
                // The first read is the starting shape, not a change.
                if (lastShape.Length > 0 && shape != lastShape)
                {
                    toHex = shape == "hex";
                    startedAt = now;
                    showing = true;
                }
                lastShape = shape;
            }
        }
        if (!showing)
        {
            if (Visible) Hide();
            return;
        }
        double t = (now - startedAt).TotalSeconds;
        double total = HoldFrom + Morph + Hold + Fade;
        if (t >= total || !GameCenter(out int cx, out int cy))
        {
            showing = false;
            if (Visible) Hide();
            return;
        }
        double blendT = Math.Clamp((t - HoldFrom) / Morph, 0, 1);
        float eased = Ease((float)blendT);
        float blend = toHex ? eased : 1f - eased;
        double fadeT = t - (HoldFrom + Morph + Hold);
        byte alpha = fadeT <= 0 ? (byte)235 : (byte)Math.Round(235 * (1 - Math.Clamp(fadeT / Fade, 0, 1)));
        if (!Visible) Show();
        Draw(blend, alpha, cx - Size_ / 2, cy - Size_ / 2);
    }

    // cubic-bezier(0, 0, 0.2, 1), the M2A morph curve.
    static float Ease(float x)
    {
        double u = x;
        for (int i = 0; i < 8; i++)
        {
            double bx = Bez(u, 0, 0, 0.2, 1);
            double dx = BezD(u, 0, 0, 0.2, 1);
            if (Math.Abs(dx) < 1e-5) break;
            u -= (bx - x) / dx;
            u = Math.Clamp(u, 0, 1);
        }
        return (float)Bez(u, 0, 0, 1, 1);
    }

    static double Bez(double u, double a, double b, double c, double d)
    {
        double v = 1 - u;
        return v * v * v * a + 3 * v * v * u * b + 3 * v * u * u * c + u * u * u * d;
    }

    static double BezD(double u, double a, double b, double c, double d)
    {
        double v = 1 - u;
        return 3 * v * v * (b - a) + 6 * v * u * (c - b) + 3 * u * u * (d - c);
    }

    // Same vertex order and lerp as the in-game plate and M2A overlay_geometry.
    static PointF[] Points(float blend, float k, float cx, float cy)
    {
        float[] diaX = { 11, 11, 0, -11, -11, 0 };
        float[] hexX = { 11, 11, -8.5f, -28, -28, -8.5f };
        // The hex is the old hex scaled up evenly (39 tall instead of 22), so it keeps its shape.
        float sc = 39f / 22f;
        float[] hexZ = { -8 * sc, 8 * sc, 14 * sc, 8 * sc, -8 * sc, -14 * sc };
        float dz = 11f / 1.45f;
        float[] diaZ = { 0, 0, dz, 0, 0, -dz };
        var pts = new PointF[6];
        // Centre the shape on its own middle: the diamond spans 11..-11, the hex 11..-28.
        float mid = -8.5f * blend;
        for (int i = 0; i < 6; i++)
        {
            float x = diaX[i] + (hexX[i] - diaX[i]) * blend;
            float z = diaZ[i] + (hexZ[i] - diaZ[i]) * blend;
            pts[i] = new PointF(cx + z * k, cy - (x - mid) * k);
        }
        return pts;
    }

    void Draw(float blend, byte alpha, int left, int top)
    {
        using var bmp = new Bitmap(Size_, Size_, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float k = 4.6f;
            var pts = Points(blend, k, Size_ / 2f, Size_ / 2f);
            using var fill = new SolidBrush(Color.FromArgb(255, 255, 214, 10));
            using var edge = new Pen(Color.FromArgb(255, 60, 48, 0), 3f) { LineJoin = LineJoin.Round };
            g.FillPolygon(fill, pts);
            g.DrawPolygon(edge, pts);
        }
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(mem, hbmp);
        var size = new SIZE { cx = Size_, cy = Size_ };
        var src = new POINT();
        var dst = new POINT { X = left, Y = top };
        var blendFn = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
        if (!IsHandleCreated) CreateHandle();
        UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blendFn, 2);
        SetWindowPos(Handle, new IntPtr(-1), left, top, Size_, Size_, 0x10 | 0x40);
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
