using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.ResourceBreakdown;

// The reason table owns the native codes and their wire names. This type admits only
// its contiguous native failure section, so diagnostics cannot carry another reason.
[JsonConverter(typeof(NativePlanFailureStatusConverter))]
public sealed record NativePlanFailureStatus
{
    public SchedulingFactExclusionReason Reason { get; }

    private NativePlanFailureStatus(SchedulingFactExclusionReason reason) => Reason = reason;

    public static NativePlanFailureStatus FromReason(SchedulingFactExclusionReason reason)
        => reason is >= SchedulingFactExclusionReason.InvalidArgument and <= SchedulingFactExclusionReason.RecreateRequired
            ? new(reason)
            : throw new ArgumentOutOfRangeException(nameof(reason), reason, "Expected a native plan failure reason.");
}

public sealed class NativePlanFailureStatusConverter : JsonConverter<NativePlanFailureStatus>
{
    public override NativePlanFailureStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var reason = JsonSerializer.Deserialize<SchedulingFactExclusionReason>(ref reader, options);
        try { return NativePlanFailureStatus.FromReason(reason); }
        catch (ArgumentOutOfRangeException exception) { throw new JsonException("Expected a native plan failure code.", exception); }
    }

    public override void Write(Utf8JsonWriter writer, NativePlanFailureStatus value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value.Reason, options);
}
