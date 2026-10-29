using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

// Outline only. It reads the in-game plate pose and draws the same hex.
// The webcam is added after this outline sits on the plate.
sealed class ShareHexForm : Form
{
    const int Pad = 12;
    readonly List<PointF[]> draw = new();
    bool announced;
    public Action<int, int, int, int, PointF[]?, float[]?, string>? PlaceVideo;
    volatile bool run = true;
    bool shapeIsHex;
    bool animating;
    float animFrom;
    float animTo;
    DateTime animStart;
    int pendingShape = -1;
    bool keyWasDown;
    bool lockFired;
    bool hexLocked;
    DateTime keyDownAt;
    DateTime hexUntil;
    string seenBody = "";
    int wakeIdle;
    HashSet<uint> gamePids = new();
    DateTime nextPidScan = DateTime.MinValue;
    readonly EnumProc enumProc;

    const int WS_EX_LAYERED = 0x80000;
    const int WS_EX_TRANSPARENT = 0x20;
    const int WS_EX_TOOLWINDOW = 0x80;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const uint LWA_COLORKEY = 1;
    const uint SWP_NOACTIVATE = 0x10;
    const uint SWP_SHOWWINDOW = 0x40;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    static readonly Color Key = Color.Magenta;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool UpdateWindow(IntPtr h);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr handle, int flags, System.Text.StringBuilder exe, ref int size);

    static string plateFile = @"D:\Tools\tos-faceplates\plate.txt";
    static DateTime plateFileUntil;

    // The game writes plate.txt beside the FacePlate mod. That path works on
    // any PC. The D:\Tools file is only a fallback.
    public static string PlateDir() => Path.GetDirectoryName(PlateFile()) ?? @"D:Tools	os-faceplates";

    static string PlateFile()
    {
        if (DateTime.UtcNow < plateFileUntil) return plateFile;
        plateFileUntil = DateTime.UtcNow.AddSeconds(2);
        foreach (var proc in Process.GetProcessesByName("TheOtherSide-Win64-Shipping"))
        {
            try
            {
                var handle = OpenProcess(0x1000, false, (uint)proc.Id);
                if (handle == IntPtr.Zero) continue;
                try
                {
                    var sb = new System.Text.StringBuilder(1024);
                    int n = sb.Capacity;
                    if (!QueryFullProcessImageName(handle, 0, sb, ref n)) continue;
                    var dir = Path.GetDirectoryName(sb.ToString());
                    if (string.IsNullOrEmpty(dir)) continue;
                    plateFile = Path.Combine(dir, "ue4ss", "Mods", "FacePlate", "plate.txt");
                    return plateFile;
                }
                finally { CloseHandle(handle); }
            }
            catch { }
            finally { proc.Dispose(); }
        }
        return plateFile;
    }

    IntPtr found;
    int foundArea;

    public ShareHexForm()
    {
        enumProc = OnEnum;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Key;
        TransparencyKey = Key;
        Bounds = new Rectangle(-32000, -32000, 32, 32);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        var pump = new Thread(Pump) { IsBackground = true, Name = "ShareHex" };
        pump.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        run = false;
        base.OnFormClosed(e);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetLayeredWindowAttributes(Handle, 0x00FF00FFu, 255, LWA_COLORKEY);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Key);
    }

    // Right Arrow, same as M2A. Tap: hex for 20 s, then diamond. Hold 1 s: hex stays.
    // Tap while it is locked: diamond. A tap during the 20 s starts them over.
    void PollShapeKey()
    {
        bool down = (GetAsyncKeyState(0x27) & 0x8000) != 0;
        var now = DateTime.UtcNow;
        if (down && !keyWasDown)
        {
            keyDownAt = now;
            lockFired = false;
        }
        if (down && !lockFired && keyDownAt != DateTime.MinValue && (now - keyDownAt).TotalSeconds >= 1)
        {
            lockFired = true;
            hexLocked = true;
            hexUntil = DateTime.MinValue;
            RequestShape(true);
        }
        if (!down && keyWasDown && !lockFired)
        {
            if (hexLocked)
            {
                hexLocked = false;
                hexUntil = DateTime.MinValue;
                RequestShape(false);
            }
            else
            {
                RequestShape(true);
                hexUntil = now.AddSeconds(20);
            }
        }
        if (!down) keyDownAt = DateTime.MinValue;
        keyWasDown = down;
        if (hexUntil != DateTime.MinValue && now >= hexUntil && !hexLocked)
        {
            hexUntil = DateTime.MinValue;
            RequestShape(false);
        }
    }

    void RequestShape(bool hex)
    {
        if (animating)
        {
            pendingShape = hex ? 1 : 0;
            return;
        }
        if (shapeIsHex == hex) return;
        animFrom = shapeIsHex ? 1f : 0f;
        animTo = hex ? 1f : 0f;
        animStart = DateTime.UtcNow;
        animating = true;
    }

    float ShapeBlend()
    {
        if (!animating) return shapeIsHex ? 1f : 0f;
        double t = (DateTime.UtcNow - animStart).TotalSeconds / 0.4;
        if (t >= 1)
        {
            shapeIsHex = animTo >= 0.5f;
            animating = false;
            if (pendingShape >= 0)
            {
                bool next = pendingShape == 1;
                pendingShape = -1;
                RequestShape(next);
            }
            return shapeIsHex ? 1f : 0f;
        }
        return animFrom + (animTo - animFrom) * EaseOut(t);
    }

    static float EaseOut(double x)
    {
        double u = x;
        for (int i = 0; i < 8; i++)
        {
            double bx = Bez(u, 0, 0, 0.2, 1);
            double dx = BezD(u, 0, 0, 0.2, 1);
            if (Math.Abs(dx) < 1e-5) break;
            u -= (bx - x) / dx;
            if (u < 0) u = 0;
            if (u > 1) u = 1;
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

    // M2A overlay_geometry, centered. Diamond height is 1.45 times its width.
    // Hex flats are 0.72 of the frame. Both share the same center.
    // offsetX is the diamond's horizontal roam, already faded for the hex.
    static PointF[] Silhouette(float w, float h, float hex, float offsetX)
    {
        if (hex < 0f) hex = 0f;
        if (hex > 1f) hex = 1f;
        float cx = w * 0.5f;
        float cy = h * 0.5f;
        float halfH = h * 0.5f;
        float halfW = halfH / 1.45f;
        var diamond = new[]
        {
            new PointF(cx, cy - halfH),
            new PointF(cx, cy - halfH),
            new PointF(cx + halfW, cy),
            new PointF(cx, cy + halfH),
            new PointF(cx, cy + halfH),
            new PointF(cx - halfW, cy),
        };
        float flat = w * 0.72f * 0.5f;
        var hexagon = new[]
        {
            new PointF(cx - flat, 0),
            new PointF(cx + flat, 0),
            new PointF(w, cy),
            new PointF(cx + flat, h),
            new PointF(cx - flat, h),
            new PointF(0, cy),
        };
        var o = new PointF[6];
        for (int i = 0; i < 6; i++)
        {
            o[i] = new PointF(
                diamond[i].X + (hexagon[i].X - diamond[i].X) * hex + offsetX,
                diamond[i].Y + (hexagon[i].Y - diamond[i].Y) * hex);
        }
        return o;
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

    void Follow()
    {
        PollShapeKey();
        if (DateTime.UtcNow >= nextPidScan)
        {
            nextPidScan = DateTime.UtcNow.AddMilliseconds(500);
            var next = new HashSet<uint>();
            foreach (var proc in Process.GetProcessesByName("TheOtherSide-Win64-Shipping"))
            {
                next.Add((uint)proc.Id);
                proc.Dispose();
            }
            gamePids = next;
        }
        found = IntPtr.Zero;
        foundArea = 0;
        if (gamePids.Count > 0) EnumWindows(enumProc, IntPtr.Zero);
        var kind = TryRead(out var body);
        if (kind == ReadKind.Torn) return;
        if (found == IntPtr.Zero || kind != ReadKind.Frame)
        {
            HideHex();
            return;
        }
        var origin = new POINT();
        if (!ClientToScreen(found, ref origin) || !GetClientRect(found, out RECT client))
        {
            HideHex();
            return;
        }
        float hex = ShapeBlend();
        var face = FaceTrack.Read();
        float weight = 1f - hex;
        float panFrac = face.VideoPanX.HasValue ? face.VideoPanX.Value / 1280f * weight : 0f;
        float dxFeed = face.DiamondDx.HasValue ? face.DiamondDx.Value * weight : 0f;
        if (!Project(body, client.Right, client.Bottom, hex, panFrac, dxFeed, out var poly, out var uvs, out var watch)
            || poly.Length < 3)
        {
            HideHex();
            return;
        }
        poly = Lead(poly, body, watch, hex);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in poly)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        int left = (int)MathF.Floor(minX);
        int top = (int)MathF.Floor(minY);
        int w = Math.Max(1, (int)MathF.Ceiling(maxX) - left);
        int h = Math.Max(1, (int)MathF.Ceiling(maxY) - top);
        var local = new PointF[poly.Length];
        for (int i = 0; i < poly.Length; i++)
            local[i] = new PointF(poly[i].X - left, poly[i].Y - top);
        int x = origin.X + left;
        int y = origin.Y + top;
        PlaceVideo?.Invoke(x, y, w, h, local, uvs, watch);
        if (Visible && Bounds.Left > -1000) Hide();
        if (!announced)
        {
            announced = true;
            try
            {
                File.AppendAllText(@"D:\Tools\tos-faceplates\share.log",
                    DateTime.Now.ToString("HH:mm:ss") + " outline on\r\n");
            }
            catch { }
        }
    }

    // The overlay is a separate window. Its picture reaches the screen a few
    // milliseconds after the game drew the frame, so a moving head left the plate
    // behind. Measure the plate speed on the game clock (the T line) and draw it
    // where it will be when the overlay shows up.
    PointF[]? leadPrev;
    float leadT = float.NaN;
    float leadVx, leadVy;
    string leadWatch = "";
    float leadMs = 12f;
    DateTime leadFileAt = DateTime.MinValue;

    float LeadMs()
    {
        // lead.txt beside the exe: milliseconds, 0 turns the lead off.
        if (DateTime.UtcNow < leadFileAt) return leadMs;
        leadFileAt = DateTime.UtcNow.AddSeconds(2);
        try
        {
            var f = Path.Combine(AppContext.BaseDirectory, "lead.txt");
            if (File.Exists(f) && Num(File.ReadAllText(f).Trim(), out float v) && v >= 0f && v <= 60f) leadMs = v;
        }
        catch { }
        return leadMs;
    }

    static bool FrameClock(string body, out float t)
    {
        t = 0f;
        int i = body.LastIndexOf("\nT ", StringComparison.Ordinal);
        if (i < 0) return false;
        int e = body.IndexOf('\n', i + 1);
        var text = (e < 0 ? body.Substring(i + 3) : body.Substring(i + 3, e - (i + 3))).Trim();
        return Num(text, out t);
    }

    PointF[] Lead(PointF[] poly, string body, string watch, float hexBlend)
    {
        float lead = LeadMs();
        if (!FrameClock(body, out float t) || lead <= 0f || animating || watch != leadWatch)
        {
            leadPrev = poly;
            leadT = float.NaN;
            leadVx = leadVy = 0f;
            leadWatch = watch;
            if (!FrameClock(body, out t)) return poly;
            leadT = t;
            return poly;
        }
        if (leadPrev != null && leadPrev.Length == poly.Length && !float.IsNaN(leadT) && t != leadT)
        {
            float dt = t - leadT;
            if (dt > 0.5f && dt < 80f)
            {
                float mx = 0f, my = 0f;
                for (int i = 0; i < poly.Length; i++)
                {
                    mx += poly[i].X - leadPrev[i].X;
                    my += poly[i].Y - leadPrev[i].Y;
                }
                mx /= poly.Length;
                my /= poly.Length;
                // A cut or a respawn is not motion. Do not throw the plate across the screen.
                if (MathF.Abs(mx) < 300f && MathF.Abs(my) < 300f)
                {
                    leadVx = leadVx * 0.4f + (mx / dt) * 0.6f;
                    leadVy = leadVy * 0.4f + (my / dt) * 0.6f;
                }
                else
                {
                    leadVx = leadVy = 0f;
                }
            }
            leadPrev = poly;
            leadT = t;
        }
        else if (leadPrev == null || float.IsNaN(leadT))
        {
            leadPrev = poly;
            leadT = t;
        }
        float ox = Math.Clamp(leadVx * lead, -60f, 60f);
        float oy = Math.Clamp(leadVy * lead, -60f, 60f);
        if (ox == 0f && oy == 0f) return poly;
        var o = new PointF[poly.Length];
        for (int i = 0; i < poly.Length; i++) o[i] = new PointF(poly[i].X + ox, poly[i].Y + oy);
        return o;
    }

    void HideHex()
    {
        leadPrev = null;
        leadT = float.NaN;
        leadVx = leadVy = 0f;
        draw.Clear();
        PlaceVideo?.Invoke(0, 0, 0, 0, null, null, "");
        if (Visible && Bounds.Left > -1000) Hide();
    }

    enum ReadKind { Missing, Torn, Empty, Frame }

    void Pump()
    {
        timeBeginPeriod(1);
        try
        {
            while (run)
            {
                var kind = TryRead(out var body);
                bool wake = false;
                if (kind != ReadKind.Torn && body != seenBody)
                {
                    seenBody = body;
                    wake = true;
                    wakeIdle = 0;
                }
                else if (++wakeIdle >= 8)
                {
                    wake = true;
                    wakeIdle = 0;
                }
                if (wake && IsHandleCreated && !IsDisposed)
                {
                    try { Invoke(Follow); }
                    catch (ObjectDisposedException) { break; }
                    catch (InvalidOperationException) { break; }
                }
                Thread.Sleep(1);
            }
        }
        finally
        {
            timeEndPeriod(1);
        }
    }

    static ReadKind TryRead(out string body)
    {
        body = "";
        try
        {
            using var fs = new FileStream(PlateFile(),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[4096];
            int n = fs.Read(buf, 0, buf.Length);
            if (n <= 0) return ReadKind.Missing;
            var text = System.Text.Encoding.ASCII.GetString(buf, 0, n);
            if (text.StartsWith("NONE", StringComparison.Ordinal)) return ReadKind.Empty;
            int s = text.IndexOf("S ", StringComparison.Ordinal);
            int e = text.LastIndexOf("\nE ", StringComparison.Ordinal);
            if (s >= 0 && e > s)
            {
                int sl = text.IndexOf('\n', s);
                int el = text.IndexOf('\n', e + 1);
                if (sl < 0 || el < 0) return ReadKind.Torn;
                var ss = text.Substring(s + 2, sl - (s + 2)).Trim();
                var es = text.Substring(e + 3, el - (e + 3)).Trim();
                if (ss.Length == 0 || ss != es) return ReadKind.Torn;
                body = text.Substring(sl + 1, e - sl);
                return body.Contains("\nP ", StringComparison.Ordinal) || body.StartsWith("P ", StringComparison.Ordinal)
                    ? ReadKind.Frame : ReadKind.Empty;
            }
            if (text.StartsWith("C ", StringComparison.Ordinal))
            {
                body = text;
                return text.Contains("\nP ", StringComparison.Ordinal) ? ReadKind.Frame : ReadKind.Empty;
            }
            return ReadKind.Torn;
        }
        catch
        {
            return ReadKind.Missing;
        }
    }

    static bool Num(string s, out float v) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    static bool Project(string body, int viewW, int viewH, float hexBlend, float panFrac, float dxFeed,
        out PointF[] poly, out float[] uvs, out string watchId)
    {
        poly = Array.Empty<PointF>();
        uvs = Array.Empty<float>();
        watchId = "";
        if (viewW < 32 || viewH < 32) return false;
        float pitch = 0, yaw = 0, roll = 0, cx = 0, cy = 0, cz = 0, fov = 90;
        var haveCam = false;
        var selfId = "";
        PointF[]? best = null;
        float[]? bestUv = null;
        float bestDepth = float.MaxValue;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 8 && parts[0] == "C")
            {
                if (!Num(parts[1], out pitch)) return false;
                if (!Num(parts[2], out yaw)) return false;
                if (!Num(parts[3], out roll)) return false;
                if (!Num(parts[4], out cx)) return false;
                if (!Num(parts[5], out cy)) return false;
                if (!Num(parts[6], out cz)) return false;
                if (!Num(parts[7], out fov)) return false;
                haveCam = true;
            }
            else if (parts.Length >= 2 && parts[0] == "M")
            {
                selfId = parts[1];
            }
            else if (parts.Length >= 29 && parts[0] == "P" && haveCam)
            {
                // Never draw your own camera on your own screen. That plate
                // sits on your eyes and blocks the view. Show the other person.
                if (parts[1] == selfId) continue;
                if (!TryPlate(parts, pitch, yaw, roll, cx, cy, cz, fov, viewW, viewH, hexBlend, panFrac, dxFeed, out var plate, out var plateUv, out var depth))
                    continue;
                // Old scripts do not mark self. A plate closer than 50cm is
                // your own head in front of the camera.
                if (selfId.Length == 0 && depth < 50f) continue;
                if (depth < bestDepth)
                {
                    bestDepth = depth;
                    best = plate;
                    bestUv = plateUv;
                    watchId = parts[1];
                }
            }
        }
        if (best == null || bestUv == null) return false;
        poly = best;
        uvs = bestUv;
        return true;
    }

    static bool TryPlate(string[] parts, float camPitch, float camYaw, float camRoll,
        float cx, float cy, float cz, float fov, int viewW, int viewH,
        float hexBlend, float panFrac, float dxFeed, out PointF[] poly, out float[] uvs, out float depth)
    {
        poly = Array.Empty<PointF>();
        uvs = Array.Empty<float>();
        depth = 0f;
        // P id loc(3) rot(3) scale(3) verts(18) = 1 + 1 + 27 = 29 tokens. id is parts[1].
        if (parts.Length < 29) return false;
        if (!Num(parts[2], out float lx)) return false;
        if (!Num(parts[3], out float ly)) return false;
        if (!Num(parts[4], out float lz)) return false;
        if (!Num(parts[5], out float lp)) return false;
        if (!Num(parts[6], out float lyaw)) return false;
        if (!Num(parts[7], out float lr)) return false;
        if (!Num(parts[8], out float scx)) return false;
        if (!Num(parts[9], out float scy)) return false;
        if (!Num(parts[10], out float scz)) return false;
        Basis(lp, lyaw, lr, out var ax, out var ay, out var az);
        Basis(camPitch, camYaw, camRoll, out var fwd, out var right, out var up);
        float tanHalfH = MathF.Tan(fov * MathF.PI / 180f / 2f);
        if (tanHalfH < 0.05f) return false;
        float tanHalfV = tanHalfH * viewH / viewW;
        var vx = new float[6];
        var vy = new float[6];
        var vz = new float[6];
        for (int i = 0; i < 6; i++)
        {
            int o = 11 + i * 3;
            if (!Num(parts[o], out vx[i])) return false;
            if (!Num(parts[o + 1], out vy[i])) return false;
            if (!Num(parts[o + 2], out vz[i])) return false;
        }
        // The running game still has the eye card (local Y = 40, facing the
        // camera). Flip that onto the face hex: Y = 18, the outward side.
        bool eyeCard = true;
        for (int i = 0; i < 6; i++)
        {
            if (MathF.Abs(vy[i] - 40f) > 2f) eyeCard = false;
        }
        if (eyeCard)
        {
            const float s = 1.35f;
            float[] xs = { 11f, 11f, 0f, -11f, -11f, 0f };
            float[] zs = { -8f, 8f, 14f, 8f, -8f, -14f };
            for (int i = 0; i < 6; i++)
            {
                vx[i] = xs[i] * s;
                vy[i] = 18f;
                vz[i] = zs[i] * s;
            }
        }
        // The plate sits on the face, normal +Y. When that normal points away
        // from the camera, the back of the head covers it.
        float faceX = lx + ay.X * 18f * scy;
        float faceY = ly + ay.Y * 18f * scy;
        float faceZ = lz + ay.Z * 18f * scy;
        float facing = (cx - faceX) * ay.X + (cy - faceY) * ay.Y + (cz - faceZ) * ay.Z;
        if (facing <= 0f) return false;
        // Diamond and hex live on that same face plane, then project with the head.
        const float faceS = 1.35f;
        float halfH = 11f * faceS;
        float halfW = 14f * faceS;
        float planeW = halfW * 2f;
        float planeH = halfH * 2f;
        float dHalfW = halfH / 1.45f;
        float[] diamondX = { halfH, halfH, 0f, -halfH, -halfH, 0f };
        float[] diamondZ = { 0f, 0f, dHalfW, 0f, 0f, -dHalfW };
        if (hexBlend < 0f) hexBlend = 0f;
        if (hexBlend > 1f) hexBlend = 1f;
        float zTrack = -(dxFeed / 1280f) * planeW;
        float zPan = panFrac * planeW;
        uvs = new float[12];
        for (int i = 0; i < 6; i++)
        {
            float sx = diamondX[i] + (vx[i] - diamondX[i]) * hexBlend;
            float sz = diamondZ[i] + (vz[i] - diamondZ[i]) * hexBlend;
            uvs[i * 2] = (halfW - sz - zPan) / planeW;
            uvs[i * 2 + 1] = (halfH - sx) / planeH;
            vx[i] = sx;
            vz[i] = sz + zTrack;
        }
        var pts = new PointF[6];
        float depthSum = 0f;
        for (int i = 0; i < 6; i++)
        {
            float wx = lx + ax.X * vx[i] * scx + ay.X * vy[i] * scy + az.X * vz[i] * scz;
            float wy = ly + ax.Y * vx[i] * scx + ay.Y * vy[i] * scy + az.Y * vz[i] * scz;
            float wz = lz + ax.Z * vx[i] * scx + ay.Z * vy[i] * scy + az.Z * vz[i] * scz;
            float dx = wx - cx, dy = wy - cy, dz = wz - cz;
            float vertDepth = dx * fwd.X + dy * fwd.Y + dz * fwd.Z;
            if (vertDepth < 5f) return false;
            depthSum += vertDepth;
            float rx = dx * right.X + dy * right.Y + dz * right.Z;
            float uz = dx * up.X + dy * up.Y + dz * up.Z;
            float ndcX = rx / (vertDepth * tanHalfH);
            float ndcY = -uz / (vertDepth * tanHalfV);
            float sx = (ndcX * 0.5f + 0.5f) * viewW;
            float sy = (ndcY * 0.5f + 0.5f) * viewH;
            if (float.IsNaN(sx) || float.IsNaN(sy) || MathF.Abs(sx) > 20000 || MathF.Abs(sy) > 20000)
                return false;
            pts[i] = new PointF(sx, sy);
        }
        poly = pts;
        depth = depthSum / 6f;
        return true;
    }

    static void Basis(float pitchDeg, float yawDeg, float rollDeg, out PointF3 fwd, out PointF3 right, out PointF3 up)
    {
        float sp = MathF.Sin(pitchDeg * MathF.PI / 180f), cp = MathF.Cos(pitchDeg * MathF.PI / 180f);
        float sy = MathF.Sin(yawDeg * MathF.PI / 180f), cy = MathF.Cos(yawDeg * MathF.PI / 180f);
        float sr = MathF.Sin(rollDeg * MathF.PI / 180f), cr = MathF.Cos(rollDeg * MathF.PI / 180f);
        fwd = new PointF3(cp * cy, cp * sy, sp);
        right = new PointF3(sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp);
        up = new PointF3(-(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp);
    }

    readonly struct PointF3
    {
        public readonly float X, Y, Z;
        public PointF3(float x, float y, float z) { X = x; Y = y; Z = z; }
    }
}
