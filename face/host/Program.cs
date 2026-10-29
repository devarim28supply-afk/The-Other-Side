using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class Program
{
    static string Page = "http://127.0.0.1:5191/";
    const string Game = "TheOtherSide-Win64-Shipping";

    [STAThread]
    static void Main(string[] args)
    {
        var pass = Path.Combine(AppContext.BaseDirectory, "friend.json");
        if (File.Exists(pass))
        {
            FriendHost.Start(pass);
            Page = "http://127.0.0.1:5192/";
        }
        using var one = new Mutex(true, "TosFace-host", out bool first);
        if (!first) return;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new PanelForm(args.Contains("--preview")));
    }

    sealed class PanelForm : Form
    {
        readonly bool preview;
        readonly OverlayForm overlay;
        readonly ShareHexForm share;
        readonly Label status = new() { Left = 12, Top = 96, Width = 360, Height = 64, Font = new Font("Segoe UI", 11f) };
        readonly Button hexBtn = new() { Left = 12, Top = 12, Width = 170, Height = 44, Text = "Hex", Font = new Font("Segoe UI", 14f), FlatStyle = FlatStyle.Flat };
        readonly Button diamondBtn = new() { Left = 192, Top = 12, Width = 170, Height = 44, Text = "Diamond", Font = new Font("Segoe UI", 14f), FlatStyle = FlatStyle.Flat };
        readonly TrackBar lift = new() { Left = 12, Top = 64, Width = 350, Minimum = 40, Maximum = 180, Value = 80, TickFrequency = 20 };
        string shape = "diamond";

        public PanelForm(bool preview)
        {
            this.preview = preview;
            Text = "Other Side faces";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            TopMost = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            ClientSize = new Size(384, 168);
            Controls.Add(hexBtn);
            Controls.Add(diamondBtn);
            Controls.Add(lift);
            Controls.Add(status);
            overlay = new OverlayForm(preview);
            share = new ShareHexForm();
            share.PlaceVideo = (x, y, w, h, poly, uv, watch) => overlay.PlaceVideo(x, y, w, h, poly, uv, watch);
            share.Show();
            hexBtn.Click += (_, _) => { shape = "hex"; PaintShape(); Push(); };
            diamondBtn.Click += (_, _) => { shape = "diamond"; PaintShape(); Push(); };
            lift.ValueChanged += (_, _) => Push();
            PaintShape();
            Load += async (_, _) =>
            {
                status.Text = "Opening the camera…";
                try
                {
                    await overlay.Start(() => Push(), text => BeginInvoke(() =>
                    {
                        status.Text = text;
                    }));
                    Push();
                }
                catch (Exception ex)
                {
                    status.Text = ex.Message;
                }
            };
            FormClosed += (_, _) => Application.Exit();
        }

        void PaintShape()
        {
            hexBtn.BackColor = shape == "hex" ? Color.FromArgb(35, 255, 154) : Color.White;
            diamondBtn.BackColor = shape == "diamond" ? Color.FromArgb(35, 255, 154) : Color.White;
        }

        void Push() => overlay.Configure(shape, lift.Value);
    }

    sealed class OverlayForm : Form
    {
        readonly bool preview;
        readonly WebView2 web = new() { Dock = DockStyle.Fill };
        Action<string>? onStatus;
        string shape = "hex";
        int lift = 80;
        string regionKey = "";
        string? lastFrame;
        System.Windows.Forms.Timer? follow;

        public OverlayForm(bool preview)
        {
            clipChild = OnClipChild;
            this.preview = preview;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            web.DefaultBackgroundColor = Color.Black;
            Controls.Add(web);
            if (preview)
            {
                Bounds = new Rectangle(80, 80, 480, 320);
                Show();
            }
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void PlaceVideo(int x, int y, int w, int h, PointF[]? poly, float[]? uv, string watch)
        {
            if (preview) return;
            if (poly == null || uv == null || poly.Length < 3 || uv.Length < 12 || w < 1 || h < 1)
            {
                Park();
                PushFrame(0, 0, null, null, "");
                return;
            }
            if (!IsHandleCreated) return;
            if (!Visible) Show();
            SetWindowPos(Handle, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            var pts = new POINT[poly.Length];
            for (int i = 0; i < poly.Length; i++)
            {
                pts[i].X = (int)MathF.Round(poly[i].X);
                pts[i].Y = (int)MathF.Round(poly[i].Y);
            }
            Clip(Handle, pts);
            if (web.IsHandleCreated) Clip(web.Handle, pts);
            PushFrame(w, h, poly, uv, watch);
        }

        void PushFrame(int w, int h, PointF[]? poly, float[]? uv, string watch)
        {
            if (web.CoreWebView2 == null) return;
            string json;
            if (poly == null || uv == null || poly.Length < 3 || uv.Length < poly.Length * 2)
            {
                json = "{\"clear\":true,\"watch\":\"\"}";
            }
            else
            {
                var xy = new float[poly.Length * 2];
                for (int i = 0; i < poly.Length; i++)
                {
                    xy[i * 2] = poly[i].X;
                    xy[i * 2 + 1] = poly[i].Y;
                }
                json = JsonSerializer.Serialize(new { xy, uv, w, h, watch });
            }
            if (lastFrame == json) return;
            lastFrame = json;
            web.CoreWebView2.PostWebMessageAsJson(json);
        }

        void Park()
        {
            if (!IsHandleCreated || preview) return;
            SetWindowRgn(Handle, IntPtr.Zero, false);
            if (Bounds.Left > -1000)
                SetWindowPos(Handle, HWND_TOPMOST, -32000, -32000, 640, 360, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        void Clip(IntPtr hwnd, POINT[] pts)
        {
            var mine = CreatePolygonRgn(pts, pts.Length, 1);
            if (mine != IntPtr.Zero) SetWindowRgn(hwnd, mine, true);
            clipPts = pts;
            EnumChildWindows(hwnd, clipChild, IntPtr.Zero);
        }

        POINT[] clipPts = Array.Empty<POINT>();
        readonly EnumChild clipChild;

        bool OnClipChild(IntPtr child, IntPtr l)
        {
            if (clipPts.Length < 3) return false;
            var rgn = CreatePolygonRgn(clipPts, clipPts.Length, 1);
            if (rgn != IntPtr.Zero) SetWindowRgn(child, rgn, false);
            return true;
        }

        public async Task Start(Action push, Action<string> status)
        {
            onStatus = status;
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TosFace");
            Directory.CreateDirectory(folder);
            var env = await CoreWebView2Environment.CreateAsync(null, folder,
                new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
            await web.EnsureCoreWebView2Async(env);
            web.CoreWebView2.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.Camera)
                    e.State = CoreWebView2PermissionState.Allow;
                else if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
                    e.State = CoreWebView2PermissionState.Deny;
            };
            web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                try { ApplyMessage(e.WebMessageAsJson); }
                catch (Exception ex) { onStatus?.Invoke(ex.Message); }
            };
            web.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) onStatus?.Invoke("Face page did not load. Start isn't up yet — run start-tos-face.cmd.");
                else push();
            };
            web.Source = new Uri(preview ? Page + "#preview" : Page);
            if (!preview)
            {
                ShowInTaskbar = false;
                Bounds = new Rectangle(-32000, -32000, 640, 360);
                Show();
            }
        }

        public void Configure(string nextShape, int nextLift)
        {
            shape = nextShape;
            lift = nextLift;
            if (web.CoreWebView2 == null) return;
            var json = JsonSerializer.Serialize(new { shape, lift, start = true });
            web.CoreWebView2.PostWebMessageAsJson(json);
        }

        void ApplyMessage(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var st))
                onStatus?.Invoke(st.GetString() ?? "");
        }

        void FollowGame()
        {
            // The webcam stays off the game. The in-game plate is the only face.
            if (Visible && Bounds.Left > -1000) Hide();
        }

        const int GWL_EXSTYLE = -20;
        const int WS_EX_LAYERED = 0x80000;
        const int WS_EX_TRANSPARENT = 0x20;
        const int WS_EX_TOOLWINDOW = 0x80;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const uint LWA_ALPHA = 2;
        const uint SWP_NOACTIVATE = 0x10;
        const uint SWP_SHOWWINDOW = 0x40;
        static readonly IntPtr HWND_TOPMOST = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int n);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int n, int value);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
        [DllImport("gdi32.dll")] static extern IntPtr CreatePolygonRgn(POINT[] pts, int n, int mode);
        [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr region, bool redraw);
        delegate bool EnumChild(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumChild cb, IntPtr l);
    }
}
