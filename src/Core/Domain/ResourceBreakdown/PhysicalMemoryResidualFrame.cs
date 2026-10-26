namespace ResourceManager.App.Domain.ResourceBreakdown;

public sealed record PhysicalMemoryResidualPart(string Kind, ulong Bytes);

public sealed record PhysicalMemoryResidualFrame(
    DateTimeOffset CapturedAt, string Status, string? ReasonCode,
    ulong? UsedBytes, ulong? AttributedProcessPrivateBytes, ulong? ResidualBytes,
    ulong? UnclassifiedBytes, IReadOnlyList<PhysicalMemoryResidualPart> Parts)
{
    public const string MetricId = "memory.residual.details";
    public int Version => 1;
    public string Source => "pfn";
    public static PhysicalMemoryResidualFrame Unavailable(DateTimeOffset at, string reason)
        => new(at, "unavailable", reason, null, null, null, null, []);
}
