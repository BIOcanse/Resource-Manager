using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

// Known user-mode ETW events are read at fixed offsets per (provider, event ID, version)
// straight from TraceEvent's raw payload. The GPU allocation, Present and VidMm sessions
// subscribe to Source.UnhandledEvents only and never touch Source.Dynamic or
// Source.Registered: constructing either parser makes TraceEvent resolve every first-seen
// event through TDH, which loads and keeps the provider's whole manifest in the process.
// Layouts are taken from the provider manifests of Windows 10.0.26300 (26H2).
internal enum EtwFixedLayoutStatus { NotConsumed, Decoded, UnknownVersion, ShortPayload }

internal enum EtwFieldType : byte { Absent, Boolean, UInt32, UInt64, Int64, Pointer, FileTime, UnicodeString }

internal enum EtwVariablePart : byte { None, CountedBytes, UnicodeString }

// Byte offset = Bytes + Pointers × the event's pointer size. A field after one variable part
// also adds that part's length: a byte array whose UInt32 count sits at VariableBytes +
// VariablePointers × pointer size, or a NUL-terminated UTF-16 string starting there.
internal readonly record struct EtwFixedOffset(int Bytes, int Pointers = 0,
    EtwVariablePart Variable = EtwVariablePart.None, int VariableBytes = 0, int VariablePointers = 0);

internal readonly record struct EtwFixedField(EtwFieldType Type, EtwFixedOffset At)
{
    internal EtwFixedField(EtwFieldType type, int bytes, int pointers = 0) : this(type, new EtwFixedOffset(bytes, pointers)) { }
    internal bool IsPresent => Type != EtwFieldType.Absent;
}

// One template version. Fields are indexed by the consumer's field enum; End is the
// template's length (its fixed prefix for templates that end in variable strings).
internal sealed class EtwFixedLayout(int version, EtwFixedOffset end, EtwFixedField[] fields)
{
    internal const int AnyVersion = -1;
    internal int Version { get; } = version;
    internal EtwFixedOffset End { get; } = end;
    internal EtwFixedField[] Fields { get; } = fields;
}

internal sealed class EtwFixedLayoutCatalog(int fieldCount)
{
    private readonly Dictionary<(Guid Provider, int Id), EtwFixedLayout[]> events = [];

    internal EtwFixedLayoutCatalog Add(Guid provider, int[] ids, int version, EtwFixedOffset end, params (int Field, EtwFixedField At)[] fields)
    {
        var slots = new EtwFixedField[fieldCount];
        foreach (var (field, at) in fields) slots[field] = at;
        var layout = new EtwFixedLayout(version, end, slots);
        foreach (var id in ids)
            events[(provider, id)] = events.TryGetValue((provider, id), out var known) ? [.. known, layout] : [layout];
        return this;
    }

    // The consumer reads only header fields of this event, so no payload layout is assumed.
    internal EtwFixedLayoutCatalog HeaderOnly(Guid provider, int id)
        => Add(provider, [id], EtwFixedLayout.AnyVersion, new EtwFixedOffset(0));

    internal IEnumerable<(Guid Provider, int Id, EtwFixedLayout Layout)> Layouts
        => events.SelectMany(item => item.Value.Select(layout => (item.Key.Provider, item.Key.Id, layout)));

    internal EtwFixedLayoutStatus Find(TraceEvent data, out EtwFixedPayload payload)
        => Find(data.ProviderGuid, (int)data.ID, data.Version, EtwRawPayload.Of(data), out payload);

    internal EtwFixedLayoutStatus Find(Guid provider, int id, int version, EtwRawPayload raw, out EtwFixedPayload payload)
    {
        payload = default;
        if (!events.TryGetValue((provider, id), out var layouts)) return EtwFixedLayoutStatus.NotConsumed;
        foreach (var layout in layouts)
        {
            if (layout.Version != version && layout.Version != EtwFixedLayout.AnyVersion) continue;
            var end = raw.Offset(layout.End);
            if (end < 0 || end > raw.Length) return EtwFixedLayoutStatus.ShortPayload;
            payload = new(raw, layout);
            return EtwFixedLayoutStatus.Decoded;
        }
        return EtwFixedLayoutStatus.UnknownVersion;
    }
}

// The raw bytes of one event. Valid only during the TraceEvent callback that produced it.
internal readonly struct EtwRawPayload(IntPtr start, int length, int pointerSize)
{
    internal static EtwRawPayload Of(TraceEvent data) => new(data.DataStart, data.EventDataLength, data.PointerSize);
    internal int Length => length;
    internal int PointerSize => pointerSize;

    // -1 when a variable part does not fit in the payload.
    internal int Offset(EtwFixedOffset at)
    {
        var offset = at.Bytes + at.Pointers * pointerSize;
        if (at.Variable == EtwVariablePart.None) return offset;
        var variable = at.VariableBytes + at.VariablePointers * pointerSize;
        if (at.Variable == EtwVariablePart.CountedBytes)
        {
            if (variable < 0 || variable > length - sizeof(uint)) return -1;
            var count = unchecked((uint)Marshal.ReadInt32(start, variable));
            return count > (uint)Math.Max(0, length - offset) ? -1 : offset + (int)count;
        }
        // The string's length includes its terminator; no terminator means it does not fit.
        for (var position = variable; position >= 0 && position <= length - sizeof(char); position += sizeof(char))
            if (Marshal.ReadInt16(start, position) == 0) return offset + position + sizeof(char) - variable;
        return -1;
    }

    internal bool Boolean(EtwFixedOffset at) => Marshal.ReadByte(start, Offset(at)) != 0;
    internal int Int32(EtwFixedOffset at) => Marshal.ReadInt32(start, Offset(at));
    internal long Int64(EtwFixedOffset at) => Marshal.ReadInt64(start, Offset(at));

    // A 4-byte pointer widens with sign extension, as TraceEvent's dynamic payload did.
    internal ulong Pointer(EtwFixedOffset at) => pointerSize == 4
        ? unchecked((ulong)Marshal.ReadInt32(start, Offset(at)))
        : unchecked((ulong)Marshal.ReadInt64(start, Offset(at)));

    internal string UnicodeString(EtwFixedOffset at)
    {
        var offset = Offset(at);
        var end = Offset(new EtwFixedOffset(offset, 0, EtwVariablePart.UnicodeString, offset));
        return Marshal.PtrToStringUni(start + offset, (end - offset) / sizeof(char) - 1);
    }
}

// One decoded event: the raw payload and the layout that matched its ID and version.
internal readonly struct EtwFixedPayload(EtwRawPayload raw, EtwFixedLayout layout)
{
    // Largest FILETIME that DateTime.FromFileTime accepts.
    private static readonly long MaxFileTime = DateTime.MaxValue.ToFileTimeUtc();
    internal EtwFixedLayout? Layout => layout;
    internal bool Has(int field) => layout is not null && layout.Fields[field].IsPresent;

    internal bool Boolean(int field) => raw.Boolean(At(field, EtwFieldType.Boolean));
    internal int Int32(int field) => raw.Int32(At(field, EtwFieldType.UInt32));
    internal uint UInt32(int field) => unchecked((uint)Int32(field));
    internal long Int64(int field) => raw.Int64(At(field, EtwFieldType.UInt64, EtwFieldType.Int64));
    internal ulong UInt64(int field) => unchecked((ulong)Int64(field));
    internal ulong Pointer(int field) => raw.Pointer(At(field, EtwFieldType.Pointer));
    internal string UnicodeString(int field) => raw.UnicodeString(At(field, EtwFieldType.UnicodeString));

    // FILETIME converts to local time like TraceEvent's dynamic payload; a value outside
    // DateTime's range was an exception string there and is false here.
    internal bool TryFileTime(int field, out DateTime value)
    {
        var fileTime = raw.Int64(At(field, EtwFieldType.FileTime));
        var valid = fileTime >= 0 && fileTime <= MaxFileTime;
        value = valid ? DateTime.FromFileTime(fileTime) : default;
        return valid;
    }

    // Boxed with TraceEvent's dynamic payload types: UInt32 as int, UInt64 as long, a
    // pointer as ulong, a 4-byte BOOL from its first byte, FILETIME as local DateTime.
    internal object? Value(int field) => layout?.Fields[field].Type switch
    {
        EtwFieldType.Boolean => Boolean(field),
        EtwFieldType.UInt32 => Int32(field),
        EtwFieldType.UInt64 or EtwFieldType.Int64 => Int64(field),
        EtwFieldType.Pointer => Pointer(field),
        EtwFieldType.FileTime => TryFileTime(field, out var time) ? (object)time : null,
        EtwFieldType.UnicodeString => UnicodeString(field),
        _ => throw Missing(field)
    };

    private EtwFixedOffset At(int field, EtwFieldType type, EtwFieldType alternative = EtwFieldType.Absent)
    {
        var at = layout?.Fields[field] ?? default;
        return at.Type != EtwFieldType.Absent && (at.Type == type || at.Type == alternative) ? at.At : throw Missing(field);
    }

    private static InvalidDataException Missing(int field)
        => new($"The fixed ETW layout has no field {field} of the requested type.");
}

// A consumed event whose version has no known layout, or whose payload is shorter than its
// layout, is not decoded and never reaches the consumer. It counts as received and dropped
// on its own provider/ID/opcode counter and on a named
// "fixed-layout/<provider>/<id>/v<version>/<reason>" counter of the same session; the first
// one per (owner, provider, ID, version, reason) in this process is logged.
internal sealed class EtwFixedLayoutGate(ILogger logger, string owner, EtwEventDropSession run)
{
    private static readonly ConcurrentDictionary<(string Owner, Guid Provider, int Id, int Version, EtwFixedLayoutStatus Reason), byte> Logged = new();
    private readonly Dictionary<(Guid Provider, int Id, int Version, EtwFixedLayoutStatus Reason), EtwEventDropCounter> rejected = [];

    // Called on the session's single consumer thread.
    internal bool Admit(TraceEvent data, EtwEventDropCounter counter, EtwFixedLayoutStatus status)
    {
        if (status is EtwFixedLayoutStatus.NotConsumed or EtwFixedLayoutStatus.Decoded) return true;
        counter.Drop();
        (Guid Provider, int Id, int Version, EtwFixedLayoutStatus Reason) key = (data.ProviderGuid, (int)data.ID, data.Version, status);
        if (!rejected.TryGetValue(key, out var named))
        {
            var reason = status == EtwFixedLayoutStatus.ShortPayload ? "short-payload" : "unknown-version";
            var kind = $"fixed-layout/{key.Provider:D}/{key.Id}/v{key.Version}/{reason}";
            named = run.NamedKind(kind);
            rejected.Add(key, named);
            if (Logged.TryAdd((owner, key.Provider, key.Id, key.Version, status), 0))
                logger.LogWarning("{Owner} ETW event {Provider}/{Event} version {Version} was not decoded ({Reason}; payload {Length} bytes, pointer size {PointerSize}). Only known fixed layouts are read; later ones count under {Kind}.",
                    owner, key.Provider, key.Id, key.Version, reason, data.EventDataLength, data.PointerSize, kind);
        }
        named.Receive();
        named.Drop();
        return false;
    }
}
