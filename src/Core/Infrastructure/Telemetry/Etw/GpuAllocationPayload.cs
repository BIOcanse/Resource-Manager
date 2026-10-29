using Microsoft.Diagnostics.Tracing;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

// Field identities are shared by the decoder and each recorded event schema.
// No physical offsets or field order are assumed across event IDs or versions.
internal enum GpuAllocationField
{
    pDxgAdapter, AdapterLuid, hVidMmGlobalAlloc, allocSize, PageTableOrDirectory,
    hVidMmAlloc, hProcessId, ulSegmentId, Flags, hAllocationHandle, Size, SegmentOffset,
    hGlobalAllocationHandle, hAllocationGlobalHandle, SegmentId, AllocationOffset, FillSize,
    SourceSegmentId, TransferOffset, TransferSize, SourceSegmentOffset,
    DestinationSegmentId, DestinationSegmentOffset, session, scan, eventsLost
}

internal interface IGpuAllocationPayload
{
    object Get(GpuAllocationField field);
    EtwEventDropCounter? DropCounter => null;
}

internal readonly struct GpuAllocationNamedPayload(Func<string, object> read, EtwEventDropCounter? dropCounter = null) : IGpuAllocationPayload
{
    internal static readonly string[] Names = Enum.GetNames<GpuAllocationField>();
    public object Get(GpuAllocationField field) => read(Names[(int)field]);
    public EtwEventDropCounter? DropCounter => dropCounter;
}

// One reader per serialized ETW consumer; reused for every callback. The cache
// belongs to this session and is populated once per provider/event/version.
internal sealed class GpuAllocationIndexedPayload : IGpuAllocationPayload
{
    private readonly Dictionary<(Guid Provider, int Id, int Version), int[]> layouts = [];
    private TraceEvent current = null!;
    private int[] indices = null!;
    public EtwEventDropCounter? DropCounter { get; private set; }

    internal void SetEvent(TraceEvent data, EtwEventDropCounter? dropCounter = null)
    {
        DropCounter = dropCounter;
        current = data;
        var key = (data.ProviderGuid, (int)data.ID, (int)data.Version);
        if (!layouts.TryGetValue(key, out var layout))
        {
            layout = GpuAllocationNamedPayload.Names.Select(data.PayloadIndex).ToArray();
            layouts.Add(key, layout);
        }
        indices = layout;
    }

    public object Get(GpuAllocationField field)
    {
        var index = indices[(int)field];
        if (index < 0) throw new InvalidDataException($"GPU event {(int)current.ID}/v{current.Version} has no {GpuAllocationNamedPayload.Names[(int)field]} payload.");
        return current.PayloadValue(index);
    }
}
