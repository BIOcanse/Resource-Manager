using V = ResourceManager.App.Infrastructure.Telemetry.Etw.VidMmField;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

// Payload fields DxgkrnlVidMmEtwTelemetryZone reads, named as in the DxgKrnl manifest.
internal enum VidMmField { ProcessId, NewUsage, OldUsage, Commitment, OldCommitment, Size }

// One process-memory event: its manifest event name (kept as the evidence's EventName)
// and its byte-value fields in the order the zone tries them.
internal sealed record VidMmProcessMemoryEvent(string EventName, VidMmField[] ByteValues);

// Fixed layouts of the DxgKrnl process-memory events on Windows 10.0.26300
// (EtwFixedLayout.cs). Offsets are (bytes, pointers).
internal static class DxgkrnlVidMmEtwLayouts
{
    internal static readonly Guid Provider = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    internal static readonly EtwFixedLayoutCatalog Catalog = CreateCatalog();
    internal static readonly Dictionary<int, VidMmProcessMemoryEvent> Events = new()
    {
        [80] = new("ReportCommittedAllocation", [V.Size]),
        [367] = new("VidMmProcessUsageChange", [V.NewUsage, V.OldUsage]),
        [370] = new("VidMmProcessDemotedCommitmentChange", [V.Commitment, V.OldCommitment]),
        [371] = new("VidMmProcessCommitmentChange", [V.Commitment, V.OldCommitment])
    };

    private static EtwFixedLayoutCatalog CreateCatalog()
    {
        const EtwFieldType U32 = EtwFieldType.UInt32, U64 = EtwFieldType.UInt64;
        return new EtwFixedLayoutCatalog(Enum.GetValues<VidMmField>().Length)
            // ReportCommittedAllocation has no process ID field; the header's process is used.
            .Add(Provider, [80], 0, new(28, 3), F(V.Size, U64, 8, 3))
            .Add(Provider, [367], 0, new(23, 1), F(V.ProcessId, U32, 16, 1), F(V.NewUsage, U64, 0), F(V.OldUsage, U64, 8))
            .Add(Provider, [370, 371], 0, new(23, 1), F(V.ProcessId, U32, 16, 1), F(V.Commitment, U64, 0), F(V.OldCommitment, U64, 8));

        static (int Field, EtwFixedField At) F(V field, EtwFieldType type, int bytes, int pointers = 0)
            => ((int)field, new EtwFixedField(type, bytes, pointers));
    }
}
