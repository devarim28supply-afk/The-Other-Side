using System.Text;

namespace TosReader;

// UE 5.4 object + name tables, found by shape (no fixed addresses), read from outside the game.
sealed class Unreal
{
    readonly Mem m;
    public ulong ObjectArray;   // FUObjectArray (GUObjectArray)
    public ulong NamePool;      // FNamePool
    int itemSize = 0x18;        // FUObjectItem
    readonly Dictionary<uint, string> names = new();

    // UObject layout
    public const int OClass = 0x10, OName = 0x18, OOuter = 0x20;

    public Unreal(Mem mem) { m = mem; }

    public bool Find(TextWriter log)
    {
        var img = m.Image();
        log.WriteLine($"game exe at 0x{m.Base:X}, {img.Length / 1048576} MB");
        ObjectArray = FindObjectArray(img);
        NamePool = FindNamePool(img);
        log.WriteLine($"object table: {(ObjectArray == 0 ? "NOT FOUND" : $"0x{ObjectArray:X} (exe+0x{ObjectArray - m.Base:X})")}");
        log.WriteLine($"name table:   {(NamePool == 0 ? "NOT FOUND" : $"0x{NamePool:X} (exe+0x{NamePool - m.Base:X})")}");
        if (ObjectArray == 0 || NamePool == 0) return false;
        // FUObjectItem is 0x18 in UE5; fall back to 0x10 if object 1 does not say it is index 1.
        foreach (var size in new[] { 0x18, 0x10 })
        {
            itemSize = size;
            var o = Object(1);
            if (o != 0 && m.I32(o + 0xC) == 1) break;
        }
        return true;
    }

    // FUObjectArray: 4 ints, then FChunkedFixedUObjectArray { Objects**, PreAllocated*, Max, Num, MaxChunks, NumChunks }.
    ulong FindObjectArray(byte[] img)
    {
        for (int o = 0; o + 0x30 <= img.Length; o += 8)
        {
            int max = BitConverter.ToInt32(img, o + 0x20), num = BitConverter.ToInt32(img, o + 0x24);
            int maxChunks = BitConverter.ToInt32(img, o + 0x28), numChunks = BitConverter.ToInt32(img, o + 0x2C);
            if (num < 50000 || num > max || max > 0x4000000) continue;
            if (numChunks != (num + 65535) / 65536 || maxChunks != (max + 65535) / 65536) continue;
            ulong objects = BitConverter.ToUInt64(img, o + 0x10);
            if (!Heap(objects)) continue;
            ulong chunk0 = m.Ptr(objects);
            if (!Heap(chunk0)) continue;
            return m.Base + (ulong)o;
        }
        return 0;
    }

    // FNamePool: lock (8), CurrentBlock, CurrentByteCursor, Blocks[8192]. Entry 0 of block 0 is "None".
    ulong FindNamePool(byte[] img)
    {
        for (int o = 0; o + 0x20 <= img.Length; o += 8)
        {
            uint cur = BitConverter.ToUInt32(img, o + 8), cursor = BitConverter.ToUInt32(img, o + 0xC);
            if (cur == 0 || cur > 8191 || cursor == 0 || cursor > 0x40000) continue;
            ulong b0 = BitConverter.ToUInt64(img, o + 0x10);
            if (!Heap(b0)) continue;
            if (o + 0x10 + 8 * (int)cur + 8 > img.Length) continue;
            if (!Heap(BitConverter.ToUInt64(img, o + 0x10 + 8 * (int)cur))) continue;
            var head = m.Bytes(b0, 2 + 4 + 2 + 12);
            if (head.Length == 0 || Encoding.ASCII.GetString(head, 2, 4) != "None") continue;
            if (!Encoding.ASCII.GetString(head, 8, 12).StartsWith("ByteProperty")) continue;
            return m.Base + (ulong)o;
        }
        return 0;
    }

    static bool Heap(ulong p) => p > 0x10000 && p < 0x7FF000000000 && (p & 7) == 0;

    public int Count => m.I32(ObjectArray + 0x24);

    public ulong Object(int i)
    {
        ulong chunks = m.Ptr(ObjectArray + 0x10);
        ulong chunk = m.Ptr(chunks + (ulong)(i / 65536) * 8);
        if (chunk == 0) return 0;
        return m.Ptr(chunk + (ulong)(i % 65536 * itemSize));
    }

    public string Name(uint index)
    {
        if (names.TryGetValue(index, out var s)) return s;
        ulong block = m.Ptr(NamePool + 0x10 + (index >> 16) * 8);
        ulong e = block + (index & 0xFFFF) * 2;
        ushort h = m.Read<ushort>(e);
        int len = h >> 6;
        bool wide = (h & 1) != 0;
        if (block == 0 || len <= 0 || len > 1024) s = "";
        else if (wide) s = Encoding.Unicode.GetString(m.Bytes(e + 2, len * 2));
        else s = Encoding.Latin1.GetString(m.Bytes(e + 2, len));
        names[index] = s;
        return s;
    }

    // FName = { ComparisonIndex, Number }; Number n > 0 shows as name_(n-1).
    public string FName(ulong at)
    {
        uint idx = m.U32(at), num = m.U32(at + 4);
        var s = Name(idx);
        return num > 0 ? $"{s}_{num - 1}" : s;
    }

    public string ObjName(ulong o) => o == 0 ? "" : FName(o + OName);
    public ulong Class(ulong o) => m.Ptr(o + OClass);
    public string ClassName(ulong o) => ObjName(Class(o));
    public ulong Outer(ulong o) => m.Ptr(o + OOuter);

    public string Path(ulong o)
    {
        var parts = new List<string>();
        for (int d = 0; o != 0 && d < 16; d++, o = Outer(o)) parts.Add(ObjName(o));
        parts.Reverse();
        return string.Join(".", parts);
    }

    // Every live object; each chunk of 65536 items is read in one go.
    public IEnumerable<(int index, ulong obj)> Objects()
    {
        int n = Count;
        ulong chunks = m.Ptr(ObjectArray + 0x10);
        for (int c = 0; c * 65536 < n; c++)
        {
            int inChunk = Math.Min(65536, n - c * 65536);
            var b = m.Bytes(m.Ptr(chunks + (ulong)c * 8), inChunk * itemSize);
            if (b.Length == 0) continue;
            for (int k = 0; k < inChunk; k++)
            {
                ulong o = BitConverter.ToUInt64(b, k * itemSize);
                if (o != 0) yield return (c * 65536 + k, o);
            }
        }
    }
}
