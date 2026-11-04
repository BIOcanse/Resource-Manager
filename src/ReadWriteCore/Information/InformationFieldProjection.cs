using ResourceManager.App.Domain.Metrics;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace ResourceManager.ReadWrite.Information;

/// <summary>Projects the existing information contract into the shared field API.
/// Definitions select the value type; neither field names nor source names are parsed.</summary>
public static class InformationFieldProjection
{
    public static FieldDescriptor Describe(DeviceInformationDefinition definition)
        => new(definition.Name, definition.Unit, definition.ValueKind switch
        {
            "number" => "float64",
            "uint32" => "unsigned32",
            "signed32" => "signed32",
            "unsigned-integer" => "unsigned128",
            "unsigned64" => "unsigned64",
            "text" => "string",
            "boolean" => "boolean",
            "bytes" => "bytes",
            "allocated-dma-channels" or "allocated-interrupts" or "array" or "camera-modes" or "chromaticity"
                or "cpuid-registers" or "disk-extents" or "hdr-luminance-declarations" or "integer-list" or "object"
                or "partition-alignment" or "partition-layout" or "partition-volume-relations" or "processor-addresses"
                or "rational" or "resolution" or "resource-ranges" or "security-products" or "text-list"
                or "volume-relations" => "structured",
            _ => throw new ArgumentException("Information value kind has no explicit field binding.", nameof(definition))
        });

    public static FieldReading Read(string deviceIdentity, DeviceInformationField field, DateTimeOffset? observedAt)
    {
        observedAt = field.ObservedAt ?? observedAt;
        var reference = new FieldReference(deviceIdentity, field.Definition.Name);
        if (field.Value is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new(reference, FieldObservationStatus.Unavailable, null, null, field.Definition.SourceId);

        var projected = ConvertValue(Describe(field.Definition).ValueKind, value);
        return new(reference, projected is null ? FieldObservationStatus.Invalid : FieldObservationStatus.Current,
            projected, projected is null ? null : observedAt, field.Definition.SourceId);
    }

    private static FieldValue? ConvertValue(string kind, JsonElement value) => kind switch
    {
        "float64" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? new(Number: number) : null,
        "unsigned32" => value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var unsigned32)
            ? new(Integer: new MetricInteger(unsigned32)) : null,
        "signed32" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var signed32)
            ? new(Integer: MetricInteger.FromSigned64(signed32)) : null,
        "unsigned64" => value.ValueKind == JsonValueKind.String
            && ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var unsigned64)
            ? new(Integer: new MetricInteger(unsigned64)) : null,
        "unsigned128" => value.ValueKind == JsonValueKind.String
            && UInt128.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var integer)
            ? new(Integer: MetricInteger.FromUnsigned128(integer)) : null,
        "string" => value.ValueKind == JsonValueKind.String ? new(Text: value.GetString()) : null,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? new(Boolean: value.GetBoolean()) : null,
        "bytes" => value.ValueKind == JsonValueKind.String && value.TryGetBytesFromBase64(out var bytes)
            ? new(Bytes: ImmutableArray.Create(bytes)) : null,
        "structured" => new(Structure: value.Clone()),
        _ => null
    };
}


