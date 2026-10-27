using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Frozen;

namespace ResourceManager.ReadWrite.Information;

/// <summary>Declarations and current values share the topology owner's atomic publication.</summary>
public sealed record DeviceInformationSnapshot
{
    public DateTimeOffset? ObservedAt { get; init; }
    public IReadOnlyList<DeviceInformationObject> Devices { get; }
    private readonly FrozenDictionary<FieldReference, DeviceInformationField> fields;

    public DeviceInformationSnapshot(DateTimeOffset? observedAt, IReadOnlyList<DeviceInformationObject> devices)
    {
        ObservedAt = observedAt;
        Devices = devices;
        fields = devices.SelectMany(device => device.Fields.Select(field =>
            KeyValuePair.Create(new FieldReference(device.DeviceIdentity, field.Definition.Name), field))).ToFrozenDictionary();
    }

    public DeviceInformationField? FindField(FieldReference field) => fields.GetValueOrDefault(field);

    public static DeviceInformationSnapshot Empty { get; } = new(null, []);
}

public sealed record DeviceInformationObject(
    string DeviceIdentity,
    string ObjectType,
    IReadOnlyList<DeviceInformationField> Fields);

public sealed record DeviceInformationDefinition(
    string Name,
    string Resource,
    string Vendor,
    string ValueKind,
    string Unit,
    string Quantity,
    string SourceId,
    ReadVolatility Volatility);

/// <summary>Null means no current value. Unsigned integers are lossless decimal strings.</summary>
public sealed record DeviceInformationField(
    DeviceInformationDefinition Definition,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] JsonElement? Value)
{
    /// <summary>Actual field observation time for a partial update. Null inherits
    /// the enclosing snapshot time; a null Value always remains unavailable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ObservedAt { get; init; }
}

public sealed record DeviceInformationResolution(uint Width, uint Height);
public sealed record DeviceInformationRate(uint Numerator, uint Denominator, double NumericValue);
