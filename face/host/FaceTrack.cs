using OpenCvSharp;

// M2A face tracking for the local camera. Same YuNet model, same roam, same
// pan. The page posts frames from the camera TosFace already holds.
static class FaceTrack
{
    const float FeedW = 1280f;
    const float FeedH = 720f;
    const float DiamondRatio = 1.45f;
    const float RoamHalf = 45f;
    const float DeadzonePx = 22f;
    const float SmoothAlpha = 0.11f;
    const float ScoreFloor = 0.7f;
    const int DetectW = 320;
    const int DetectH = 240;
    static readonly TimeSpan VanishGrace = TimeSpan.FromSeconds(10);

    static readonly object Gate = new();
    static byte[]? pending;
    static bool pendingMirror = true;
    static bool workerOn;
    static int dead;
    static int results;
    static FaceDetectorYN? detector;
    static readonly OpenCvSharp.Size DetectSize = new(DetectW, DetectH);

    static float? diamondDx;
    static float? videoPanX;
    static float? trackFaceX;
    static bool hidden;
    static bool everSawFace;
    static DateTime? faceLostSince;

    public readonly struct Reading
    {
        public float? DiamondDx { get; init; }
        public float? VideoPanX { get; init; }
        public bool DiamondHidden { get; init; }
    }

    public static Reading Read()
    {
        lock (Gate)
        {
            return new Reading
            {
                DiamondDx = diamondDx,
                VideoPanX = videoPanX,
                DiamondHidden = hidden,
            };
        }
    }

    public static void Submit(byte[] jpeg, bool mirror)
    {
        if (jpeg.Length < 200 || Volatile.Read(ref dead) != 0) return;
        bool start;
        lock (Gate)
        {
            pending = jpeg;
            pendingMirror = mirror;
            start = !workerOn;
            if (start) workerOn = true;
        }
        if (start) Task.Run(Work);
    }

    static void Work()
    {
        try
        {
            Ensure();
            while (true)
            {
                byte[]? jpeg;
                bool mirror;
                lock (Gate)
                {
                    jpeg = pending;
                    mirror = pendingMirror;
                    pending = null;
                    if (jpeg == null)
                    {
                        workerOn = false;
                        return;
                    }
                }
                try { Detect(jpeg, mirror); }
                catch (Exception ex) { Note("face frame " + ex.Message); }
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref dead, 1);
            Note("face tracker off " + ex.Message);
            lock (Gate) workerOn = false;
        }
    }

    static void Ensure()
    {
        if (detector != null) return;
        var local = Path.Combine(AppContext.BaseDirectory, "models", "face_detection_yunet_2023mar.onnx");
        var model = File.Exists(local)
            ? local
            : @"D:\Projects\M2A\assets\models\face_detection_yunet_2023mar.onnx";
        if (!File.Exists(model)) throw new FileNotFoundException("YuNet model missing", model);
        detector = FaceDetectorYN.Create(model, "", DetectSize, ScoreFloor, 0.3f, 5000);
        Note("face tracker ready");
    }

    static void Detect(byte[] jpeg, bool mirror)
    {
        var det = detector;
        if (det == null) return;
        using var frame = Cv2.ImDecode(jpeg, ImreadModes.Color);
        if (frame.Empty()) return;
        using var small = new Mat();
        Cv2.Resize(frame, small, DetectSize);
        using var faces = new Mat();
        det.Detect(small, faces);
        float? rawX = null;
        var rows = faces.Rows;
        if (rows >= 1 && faces.Cols >= 15)
        {
            var best = 0;
            var bestScore = faces.At<float>(0, 14);
            for (var i = 1; i < rows; i++)
            {
                var score = faces.At<float>(i, 14);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            if (bestScore >= ScoreFloor)
            {
                var scaleX = frame.Width / (float)DetectW;
                var x = faces.At<float>(best, 0) * scaleX;
                var w = faces.At<float>(best, 2) * scaleX;
                rawX = x + w / 2f;
            }
        }
        var n = Interlocked.Increment(ref results);
        Apply(rawX, frame.Width, mirror);
        if (n == 1 || n % 50 == 0)
            Note(rawX.HasValue ? "face" : "no face");
    }

    // rawX is the camera frame. A mirrored self view is measured from the
    // other side of the 1280-wide feed. The other person's picture is not
    // mirrored, so their face stays where the frame put it.
    static void Apply(float? rawX, int frameW, bool mirror)
    {
        lock (Gate)
        {
            if (rawX.HasValue && frameW > 0)
            {
                faceLostSince = null;
                hidden = false;
                everSawFace = true;
                var scaled = rawX.Value * (FeedW / frameW);
                var proposed = mirror ? FeedW - scaled : scaled;
                var faceX = Deadzone(proposed, trackFaceX, DeadzonePx);
                trackFaceX = faceX;
                var sample = Track(faceX);
                diamondDx = Smooth(diamondDx, sample.Dx, SmoothAlpha);
                videoPanX = Smooth(videoPanX, sample.Pan, SmoothAlpha);
                return;
            }
            trackFaceX = null;
            if (hidden || !everSawFace) return;
            faceLostSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - faceLostSince.Value < VanishGrace) return;
            hidden = true;
            diamondDx = null;
            videoPanX = null;
            faceLostSince = null;
        }
    }

    static float Deadzone(float proposed, float? held, float dead)
    {
        if (dead <= 0f || held == null) return proposed;
        if (MathF.Abs(proposed - held.Value) < dead) return held.Value;
        return proposed;
    }

    static float Smooth(float? current, float target, float alpha)
    {
        if (current == null) return target;
        if (alpha < 0f) alpha = 0f;
        if (alpha > 1f) alpha = 1f;
        return current.Value + (target - current.Value) * alpha;
    }

    readonly struct TrackSample
    {
        public float Dx { get; init; }
        public float Pan { get; init; }
    }

    // diamond_tracking.dart. Diamond roams ±45 feed px, then the picture pans.
    static TrackSample Track(float faceCenterX)
    {
        var halfW = (FeedH / 2f) / DiamondRatio;
        var mid = FeedW / 2f;
        var feedMin = halfW - mid;
        var feedMax = FeedW - halfW - mid;
        var roamMin = -RoamHalf;
        var roamMax = RoamHalf;
        if (roamMin < feedMin) roamMin = feedMin;
        if (roamMax > feedMax) roamMax = feedMax;
        if (roamMin > roamMax)
        {
            roamMin = feedMin;
            roamMax = feedMax;
        }
        var ideal = faceCenterX - mid;
        if (ideal < feedMin) ideal = feedMin;
        if (ideal > feedMax) ideal = feedMax;
        var dx = ideal;
        if (dx < roamMin) dx = roamMin;
        if (dx > roamMax) dx = roamMax;
        var pan = mid + dx - faceCenterX;
        var minPan = dx + halfW - mid;
        var maxPan = mid + dx - halfW;
        if (pan < minPan) pan = minPan;
        if (pan > maxPan) pan = maxPan;
        return new TrackSample { Dx = dx, Pan = pan };
    }

    static void Note(string line)
    {
        try
        {
            File.AppendAllText(@"D:\Tools\tos-faceplates\share.log",
                DateTime.Now.ToString("HH:mm:ss") + " " + line + "\r\n");
        }
        catch { }
    }
}
