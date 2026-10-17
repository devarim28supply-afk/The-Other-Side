namespace TosReader;

// Reads Unreal's own reflection data (class -> property names + offsets) from memory,
// so field offsets come from the game instead of being guessed.
sealed class Reflect
{
    readonly Mem m;
    readonly Unreal ue;
    readonly Dictionary<string, ulong> classes = new();          // short name -> UClass/UScriptStruct
    readonly Dictionary<(ulong, string), int> offsets = new();

    // UE 5.4 UStruct / FField layout
    const int SuperStruct = 0x40, ChildProperties = 0x50;
    const int FFieldNext = 0x18, FFieldName = 0x20, PropOffset = 0x44;
    // FBoolProperty: FieldSize, ByteOffset, ByteMask, FieldMask right after FProperty (0x70)
    const int BoolByteOffset = 0x71, BoolFieldMask = 0x73;
    // FObjectProperty / FStructProperty: PropertyClass / Struct
    const int PropInner = 0x70;

    public Reflect(Mem mem, Unreal u) { m = mem; ue = u; }

    // Indexes every Class and ScriptStruct by its short name (one walk of the object table).
    public void Load()
    {
        foreach (var (_, o) in ue.Objects())
        {
            var cls = ue.ClassName(o);
            if (cls is "Class" or "ScriptStruct" or "BlueprintGeneratedClass")
                classes.TryAdd(ue.ObjName(o), o);
        }
    }

    public ulong Class(string name) => classes.GetValueOrDefault(name);
    public ulong Super(ulong st) => m.Ptr(st + SuperStruct);

    public bool IsA(ulong cls, ulong baseCls)
    {
        for (int d = 0; cls != 0 && d < 32; d++, cls = Super(cls))
            if (cls == baseCls) return true;
        return false;
    }

    // Property address (FField*) by name, searching the struct and its supers.
    public ulong Prop(ulong st, string name)
    {
        for (int d = 0; st != 0 && d < 32; d++, st = Super(st))
            for (ulong f = m.Ptr(st + ChildProperties); f != 0; f = m.Ptr(f + FFieldNext))
                if (ue.FName(f + FFieldName) == name) return f;
        return 0;
    }

    public int Offset(string structName, string prop)
    {
        var st = Class(structName);
        if (offsets.TryGetValue((st, prop), out var off)) return off;
        var f = Prop(st, prop);
        off = f == 0 ? -1 : m.I32(f + PropOffset);
        offsets[(st, prop)] = off;
        return off;
    }

    public (int off, byte mask) Bool(string structName, string prop)
    {
        var f = Prop(Class(structName), prop);
        if (f == 0) return (-1, 0);
        return (m.I32(f + PropOffset) + m.Read<byte>(f + BoolByteOffset), m.Read<byte>(f + BoolFieldMask));
    }

    // Lists a struct's own properties (for exploring).
    public IEnumerable<(string name, int offset, string type)> Props(ulong st)
    {
        for (ulong f = m.Ptr(st + ChildProperties); f != 0; f = m.Ptr(f + FFieldNext))
            yield return (ue.FName(f + FFieldName), m.I32(f + PropOffset), ue.FName(m.Ptr(f + 8)));
    }
}
