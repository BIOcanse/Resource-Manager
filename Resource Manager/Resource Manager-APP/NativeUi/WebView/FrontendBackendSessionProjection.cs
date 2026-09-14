using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceManager.NativeUi;

internal sealed class FrontendBackendSessionProjection
{
    private const int CurrentSchemaVersion = 1;
    private const string MessageType = "host.backend-session";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private FrontendBackendSessionProjection(
        long publicationRevision,
        string state,
        string? epoch,
        string? reasonCode)
    {
        if (publicationRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(publicationRevision));
        }

        SchemaVersion = CurrentSchemaVersion;
        Type = MessageType;
        PublicationRevision = publicationRevision.ToString(CultureInfo.InvariantCulture);
        State = state;
        Epoch = epoch;
        ReasonCode = reasonCode;
    }

    public string Type { get; }

    public int SchemaVersion { get; }

    public string PublicationRevision { get; }

    public string State { get; }

    public string? Epoch { get; }

    public string? ReasonCode { get; }

    [JsonIgnore]
    public bool IsReady => string.Equals(State, "ready", StringComparison.Ordinal);

    public static FrontendBackendSessionProjection CreateReady(
        long publicationRevision)
    {
        return new FrontendBackendSessionProjection(
            publicationRevision,
            state: "ready",
            epoch: $"session:{publicationRevision.ToString(CultureInfo.InvariantCulture)}",
            reasonCode: null);
    }

    public static FrontendBackendSessionProjection CreateUnavailable(
        long publicationRevision,
        string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 128)
        {
            throw new ArgumentException(
                "A bounded backend session reason code is required.",
                nameof(reasonCode));
        }

        return new FrontendBackendSessionProjection(
            publicationRevision,
            state: "unavailable",
            epoch: null,
            reasonCode);
    }

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}
