using System.Collections.Immutable;
using System.Text.Json;

namespace ResourceManager.ReadWrite.Information;

/// <summary>Information content excludes observation timestamps. A repeated observation is not a value change.</summary>
public static class DeviceInformationContent
{
    public static bool Equals(DeviceInformationSnapshot left, DeviceInformationSnapshot right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Devices.Count != right.Devices.Count) return false;
        var devices = right.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        foreach (var device in left.Devices)
        {
            if (!devices.TryGetValue(device.DeviceIdentity, out var other)
                || device.ObjectType != other.ObjectType || device.Fields.Count != other.Fields.Count) return false;
            foreach (var field in device.Fields)
            {
                var next = right.FindField(new(device.DeviceIdentity, field.Definition.Name));
                if (next is null || field.Definition != next.Definition
                    || field.Value.HasValue != next.Value.HasValue
                    || (field.Value is { } value && !JsonElement.DeepEquals(value, next.Value!.Value))) return false;
            }
        }
        return true;
    }

    /// <summary>Preserve information declarations while delivering the exact core readings from one completed batch.</summary>
    public static DeviceInformationSnapshot Project(DeviceInformationSnapshot declarations, ImmutableArray<FieldReading> readings)
        => new(readings.Select(static reading => reading.ObservedAt).DefaultIfEmpty().Max(),
            readings.GroupBy(static reading => reading.Field.DeviceIdentity, StringComparer.Ordinal)
            .Select(group => new DeviceInformationObject(group.Key,
                declarations.Devices.Single(device => device.DeviceIdentity == group.Key).ObjectType,
                group.Select(reading =>
                {
                    var definition = declarations.FindField(reading.Field)?.Definition
                        ?? throw new ArgumentException("Select a declared information field.", nameof(readings));
                    return new DeviceInformationField(definition, Value(reading.Value, definition.ValueKind))
                        { ObservedAt = reading.ObservedAt };
                }).ToArray())).ToArray());

    private static JsonElement? Value(FieldValue? value, string kind)
    {
        if (value is null) return null;
        if (value.Structure is { } structure) return structure.Clone();
        if (value.Integer is { } integer)
        {
            if (kind == "uint32") return Json(checked((uint)integer.LowBits));
            if (kind == "signed32") return Json(checked((int)integer.ToSigned64()));
            Span<char> digits = stackalloc char[40];
            if (!integer.TryFormat(digits, out var written)) throw new InvalidOperationException("The field integer exceeds its declared type.");
            return Json(new string(digits[..written]));
        }
        if (value.Number is { } number) return Json(number);
        if (value.Boolean is { } boolean) return Json(boolean);
        if (value.Bytes is { } bytes) return Json(Convert.ToBase64String(bytes.AsSpan()));
        return value.Text is { } text ? Json(text) : null;
    }

    private static JsonElement Json<T>(T value)
        => JsonSerializer.SerializeToElement(value, WindowsInformationFieldProjection.InformationJsonOptions);
}
