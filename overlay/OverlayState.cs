using System;
using System.Collections.Generic;

namespace PhasmoSound;

public sealed class EventItem
{
    public DateTime Time;          // last time it was heard / moved
    public string Label = "";
    public string Category = "";   // sound group (Ghost, Physical, Device, Object, Environment)
    public float Score = 1f;
    // exact position in the map (UE units, cm) from the game feed; none for "in your head" sounds
    public bool HasPos;
    public double X, Y, Z;
    public string Id = "0";        // audio component of a playing sound (0 = one-shot)
    public bool Playing;           // still playing (moves with its source)
    public int Count = 1;          // repeats merged into this row (footsteps)
    public readonly List<(DateTime t, double x, double y, double z)> Trail = new();   // earlier positions (ghost walking)

    /// Move to a new spot, keeping the old one in the trail.
    public void MoveTo(double x, double y, double z, DateTime now)
    {
        if (HasPos) { Trail.Add((Time, X, Y, Z)); if (Trail.Count > 16) Trail.RemoveAt(0); }
        X = x; Y = y; Z = z;
    }
}

/// Everything the overlay draws. Written by the game feed / caption threads, read by the UI thread.
public sealed class OverlayState
{
    public readonly object Lock = new();
    public bool Surround = true;                        // the game feed gives full 360° directions
    public string Status = "";
    public string Toast = "";
    public string HelperNote = "";                     // game helper's latest note
    public DateTime HelperUntil = DateTime.MinValue;
    public string SummaryNote = "";                    // helper's summary of the players' talk
    public DateTime SummaryUntil = DateTime.MinValue;
    public string SpeakText = "";                      // line the computer voice is saying
    public int SpeakPhase;                              // 0 = quiet, 1 = getting the voice ready, 2 = speaking now
    public bool BoardOpen;                              // phrase board (F5) is showing
    public List<string> Board = new();                  // its lines, 1..9
    public int BoardPageNo, BoardPages;
    public List<string> PageNames = new();
    public DateTime ToastUntil = DateTime.MinValue;

    public readonly List<CaptionLine> Captions = new(); // whisper captions, oldest first
    public readonly List<EventItem> Events = new();    // newest first

    // listener = your camera, from the game feed
    public bool HasListener;
    public double LX, LY, LZ, LYaw;
    public DateTime FeedSeen = DateTime.MinValue;      // last line from the game feed

    public bool HuntActive;                             // ghost footsteps / heartbeat heard lately

    /// Direction (degrees, 0 = where you look, clockwise), distance (m) and height (m, + = above you) of a point.
    /// Caller holds Lock.
    public (float angle, double dist, double up) Where(double x, double y, double z, double eyeHeightM)
    {
        if (!HasListener) return (0, -1, 0);
        double dx = x - LX, dy = y - LY;
        double up = (z - LZ) / 100 + eyeHeightM;
        double dist = Math.Sqrt(dx * dx + dy * dy) / 100;
        double a = Math.Atan2(dy, dx) * 180 / Math.PI - LYaw;   // Unreal: X forward, Y right, yaw turns right
        a = ((a + 540) % 360) - 180;
        return ((float)a, dist, up);
    }
}
