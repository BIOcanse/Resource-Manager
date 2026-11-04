using Microsoft.Diagnostics.Tracing;
using G = ResourceManager.App.Infrastructure.Telemetry.Etw.GpuAllocationField;

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

// One reader per serialized ETW consumer; reused for every callback. Each consumed
// DxgKrnl and boundary event is read at the fixed offsets of its ID and version
// (EtwFixedLayout.cs); values are boxed with the types TraceEvent's dynamic payload had.
internal sealed class GpuAllocationFixedPayload : IGpuAllocationPayload
{
    internal static readonly EtwFixedLayoutCatalog Catalog = CreateCatalog();
    private EtwFixedPayload current;
    private int id;
    private int version;
    public EtwEventDropCounter? DropCounter { get; private set; }

    internal EtwFixedLayoutStatus SetEvent(TraceEvent data, EtwEventDropCounter? dropCounter = null)
        => SetEvent(data.ProviderGuid, (int)data.ID, data.Version, EtwRawPayload.Of(data), dropCounter);

    internal EtwFixedLayoutStatus SetEvent(Guid provider, int id, int version, EtwRawPayload raw, EtwEventDropCounter? dropCounter = null)
    {
        DropCounter = dropCounter;
        this.id = id;
        this.version = version;
        return Catalog.Find(provider, id, version, raw, out current);
    }

    public object Get(GpuAllocationField field)
    {
        if (!current.Has((int)field)) throw new InvalidDataException($"GPU event {id}/v{version} has no {GpuAllocationNamedPayload.Names[(int)field]} payload.");
        return current.Value((int)field)!;
    }

    // Microsoft-Windows-DxgKrnl and GpuAllocationBoundary layouts of Windows 10.0.26300.
    // Offsets are (bytes, pointers); only the fields GpuAllocationEventDecoder and the
    // collector's boundary handling read are listed.
    private static EtwFixedLayoutCatalog CreateCatalog()
    {
        const EtwFieldType P = EtwFieldType.Pointer, U32 = EtwFieldType.UInt32, U64 = EtwFieldType.UInt64, B = EtwFieldType.Boolean;
        var dxgKrnl = GpuAllocationCollector.Provider;
        var boundary = GpuAllocationCollector.BoundaryProvider;
        return new EtwFixedLayoutCatalog(GpuAllocationNamedPayload.Names.Length)
            .Add(dxgKrnl, [33, 34, 35], 3, new(90, 7),
                F(G.pDxgAdapter, P, 0, 2), F(G.hVidMmGlobalAlloc, P, 40, 3), F(G.allocSize, U64, 4, 3), F(G.PageTableOrDirectory, B, 86, 7))
            .Add(dxgKrnl, [36, 37, 38], 2, new(4, 12),
                F(G.pDxgAdapter, P, 0, 2), F(G.hVidMmGlobalAlloc, P, 0, 4), F(G.hVidMmAlloc, P, 0, 3), F(G.hProcessId, P, 0))
            .Add(dxgKrnl, [39, 40], 0, new(0, 1),
                F(G.hVidMmAlloc, P, 0))
            .Add(dxgKrnl, [53], 0, new(44, 3),
                F(G.pDxgAdapter, P, 0), F(G.hAllocationGlobalHandle, P, 4, 2), F(G.TransferOffset, U32, 4, 3), F(G.TransferSize, U64, 8, 3),
                F(G.SourceSegmentId, U32, 16, 3), F(G.SourceSegmentOffset, U64, 20, 3), F(G.DestinationSegmentId, U32, 28, 3), F(G.DestinationSegmentOffset, U64, 32, 3))
            .Add(dxgKrnl, [54], 0, new(28, 3),
                F(G.pDxgAdapter, P, 0), F(G.hAllocationGlobalHandle, P, 4, 2), F(G.FillSize, U64, 4, 3), F(G.SegmentId, U32, 16, 3), F(G.SegmentOffset, U64, 20, 3))
            .Add(dxgKrnl, [55], 0, new(20, 3),
                F(G.pDxgAdapter, P, 0), F(G.hAllocationGlobalHandle, P, 4, 2), F(G.SegmentId, U32, 8, 3), F(G.SegmentOffset, U64, 12, 3))
            .Add(dxgKrnl, [74], 0, new(0, 1),
                F(G.hGlobalAllocationHandle, P, 0))
            .Add(dxgKrnl, [78], 1, new(45, 2),
                F(G.ulSegmentId, U32, 0), F(G.pDxgAdapter, P, 4), F(G.Flags, U32, 32, 1))
            .Add(dxgKrnl, [80], 0, new(28, 3),
                F(G.hProcessId, P, 0), F(G.hAllocationHandle, P, 0, 2), F(G.Size, U64, 8, 3), F(G.ulSegmentId, U32, 16, 3), F(G.SegmentOffset, U64, 20, 3))
            .Add(dxgKrnl, [110], 1, AfterConfigSpace(48),
                F(G.pDxgAdapter, P, 0), (Field: (int)G.AdapterLuid, At: new EtwFixedField(U64, AfterConfigSpace(40))))
            .Add(dxgKrnl, [227, 391], 0, new(12, 1),
                F(G.hGlobalAllocationHandle, P, 0), F(G.ulSegmentId, U32, 0, 1), F(G.SegmentOffset, U64, 4, 1))
            .Add(dxgKrnl, [306], 1, new(84, 3),
                F(G.pDxgAdapter, P, 0), F(G.hAllocationGlobalHandle, P, 4, 2), F(G.AllocationOffset, U64, 4, 3), F(G.TransferSize, U64, 12, 3),
                F(G.SourceSegmentId, U32, 20, 3), F(G.DestinationSegmentId, U32, 24, 3), F(G.SourceSegmentOffset, U64, 68, 3), F(G.DestinationSegmentOffset, U64, 76, 3))
            .Add(dxgKrnl, [307], 1, new(44, 3),
                F(G.pDxgAdapter, P, 0), F(G.hAllocationGlobalHandle, P, 4, 2), F(G.AllocationOffset, U64, 4, 3), F(G.FillSize, U64, 12, 3),
                F(G.SegmentId, U32, 24, 3), F(G.SegmentOffset, U64, 36, 3))
            .Add(dxgKrnl, [313, 314], 0, new(4, 2),
                F(G.pDxgAdapter, P, 0), F(G.hAllocationGlobalHandle, P, 0, 1), F(G.SegmentId, U32, 0, 2))
            .Add(boundary, [2], 0, AfterSession(16),
                (Field: (int)G.session, At: new EtwFixedField(EtwFieldType.UnicodeString, 0)),
                (Field: (int)G.scan, At: new EtwFixedField(EtwFieldType.Int64, AfterSession(0))),
                (Field: (int)G.eventsLost, At: new EtwFixedField(EtwFieldType.Int64, AfterSession(8))))
            .Add(boundary, [3], 0, AfterSession(8),
                (Field: (int)G.session, At: new EtwFixedField(EtwFieldType.UnicodeString, 0)),
                (Field: (int)G.scan, At: new EtwFixedField(EtwFieldType.Int64, AfterSession(0))));

        static (int Field, EtwFixedField At) F(G field, EtwFieldType type, int bytes, int pointers = 0)
            => ((int)field, new EtwFixedField(type, bytes, pointers));
        // DpiReportAdapter_V1: ConfigSpace is ConfigSpaceSize bytes; that count sits after pDxgAdapter.
        static EtwFixedOffset AfterConfigSpace(int bytes) => new(bytes, 1, EtwVariablePart.CountedBytes, 0, 1);
        // BeginScan/EndScan: a NUL-terminated UTF-16 session name, then Int64 values.
        static EtwFixedOffset AfterSession(int bytes) => new(bytes, 0, EtwVariablePart.UnicodeString, 0);
    }
}
