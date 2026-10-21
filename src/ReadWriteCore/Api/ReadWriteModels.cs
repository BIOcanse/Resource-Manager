using System.Collections.Immutable;
using System.Text.Json;

namespace ResourceManager.ReadWrite;

public readonly record struct FieldReference(string DeviceIdentity, string FieldName);
public sealed record DeviceDescriptor(string Identity, string Resource, string? Vendor);
public sealed record FieldDescriptor(string Name, string Unit, string ValueKind, string? Meaning);
public sealed record OperationSupport(bool Supported, string? Reason, ImmutableArray<string> Dependencies)
{
    public static OperationSupport Unsupported(string reason) => new(false, reason, []);
}
public sealed record FieldCapability(FieldReference Field, FieldDescriptor Definition,
    OperationSupport Read, OperationSupport Write, string ReadSource)
{
    public FieldWriteContract? WriteContract { get; init; }
}

/// <summary>Potential raw success evidence declared by a capability. Only a receipt
/// describes evidence actually obtained for one write; applied alone is not verification.</summary>
public enum FieldWriteEvidence
{
    None, AcceptedOnly, MaskedRegisterReadback, EncodedMailboxReadback,
    SettingReadbackTolerance, ExactIntegerAndPnpReadback, ConditionalReadbackElseAccepted
}

/// <summary>Declared bounds/defaults are not sampled settings or hardware-discovered
/// limits. Null bounds require explicit discovery of the original driver range.</summary>
public sealed record FieldWriteContract(string PhysicalScope, double? Minimum, double? Maximum,
    double? Step, double? DeclaredDefault, FieldWriteEvidence Evidence, string RangeOrigin)
{
    public string? ModeGate { get; init; }
}

/// <summary>Actual evidence returned by the raw write. ObservedValue, tolerance and
/// timestamp are present only when that operation supplies them, never from caller intent.</summary>
public sealed record FieldWriteReceipt(FieldWriteEvidence Kind, FieldValue? ObservedValue = null,
    double? Tolerance = null, DateTimeOffset? ObservedAt = null);

public enum FieldObservationStatus { Current, Invalid, Unavailable, NotRequested, Unsupported, Partial, Warming }

/// <summary>Exact integer text is never converted through double; null payloads
/// remain different from zero, false and a successfully read empty byte sequence.</summary>
public sealed record FieldValue(double? Number = null, string? Integer = null, bool? Boolean = null,
    ImmutableArray<byte>? Bytes = null, string? Text = null, JsonElement? Structure = null)
{
    internal bool Matches(string kind)
    {
        var count = (Number.HasValue ? 1 : 0) + (Integer is not null ? 1 : 0)
            + (Boolean.HasValue ? 1 : 0) + (Bytes.HasValue ? 1 : 0)
            + (Text is not null ? 1 : 0) + (Structure.HasValue ? 1 : 0);
        if (count != 1) return false;
        return kind switch
        {
            "float64" => Number is { } number && double.IsFinite(number),
            "signed64" => long.TryParse(Integer, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out _),
            "signed32" => int.TryParse(Integer, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out _),
            "unsigned32" => uint.TryParse(Integer, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _),
            "unsigned64" => ulong.TryParse(Integer, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _),
            "unsigned128" => UInt128.TryParse(Integer, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _),
            "boolean" => Boolean.HasValue,
            "bytes" => Bytes is { IsDefault: false },
            "string" => Text is not null,
            "structured" => Structure.HasValue,
            _ => false
        };
    }
}

public sealed record FieldReading(FieldReference Field, FieldObservationStatus Status, FieldValue? Value,
    DateTimeOffset? ObservedAt, string SourceId, ulong? SampleDurationMilliseconds = null);
public sealed record FieldWriteResult(FieldReference Field, string Status, string? Reason)
{
    public FieldWriteReceipt? Evidence { get; init; }
}
