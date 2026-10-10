using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace PhasmoSound;

/// Polls the keyboard and mouse so the overlay can tell which sounds you caused yourself.
public sealed class InputWatch
{
    readonly int[] moveKeys, actionKeys;
    readonly int moveTailMs, actionWindowMs;
    DateTime movingUntil = DateTime.MinValue, actingUntil = DateTime.MinValue;

    public InputWatch(Config cfg)
    {
        moveKeys = Parse(cfg.MoveKeys);
        actionKeys = Parse(cfg.ActionKeys);
        moveTailMs = cfg.OwnMoveTailMs;
        actionWindowMs = cfg.OwnActionWindowMs;
        Log.Write($"input watch: move={string.Join(",", moveKeys)} action={string.Join(",", actionKeys)}");
    }

    static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LButton"] = 0x01, ["RButton"] = 0x02, ["MButton"] = 0x04, ["Shift"] = 0x10, ["Ctrl"] = 0x11, ["Alt"] = 0x12,
        ["Space"] = 0x20, ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28, ["Tab"] = 0x09,
    };

    static int[] Parse(string s) => s.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(k => Named.TryGetValue(k, out var v) ? v : k.Length == 1 ? char.ToUpperInvariant(k[0]) : 0)
        .Where(v => v != 0).ToArray();

    /// Call often (every ~20 ms). Returns nothing; query IsMoving / IsActing afterwards.
    public void Poll()
    {
        var now = DateTime.UtcNow;
        if (moveKeys.Any(Down)) movingUntil = now.AddMilliseconds(moveTailMs);
        if (actionKeys.Any(Down)) actingUntil = now.AddMilliseconds(actionWindowMs);
    }

    public bool IsMoving => DateTime.UtcNow < movingUntil;
    public bool IsActing => DateTime.UtcNow < actingUntil;

    public static bool KeyDown(int vk) => Down(vk);
    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
}
