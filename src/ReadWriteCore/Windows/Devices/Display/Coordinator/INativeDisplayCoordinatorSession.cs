using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal interface INativeDisplayCoordinatorSession : IDisposable
{
    NativeDisplayCoordinatorCapacity Capacity { get; }
    NativeDisplayCoordinatorStatus BeginRefresh(in NativeDisplayRefreshInput input);
    NativeDisplayCoordinatorStatus SubmitSource(in NativeDisplaySourceBatchHeader header,
        ReadOnlySpan<NativeDisplayFact> facts, ReadOnlySpan<NativeDisplayTextInput> texts, ReadOnlySpan<byte> bytes);
    NativeDisplayCoordinatorStatus FinalizeRefresh(in NativeDisplayFinalizeInput input);
    NativeDisplayCoordinatorStatus AbortRefresh(in NativeDisplayAbortInput input);
    NativeDisplayCoordinatorStatus Snapshot(out NativeDisplaySnapshotOutput output);
    NativeDisplayCoordinatorStatus ReadNodes(in NativeDisplayReadInput input, Span<NativeDisplayNodeOutput> output);
    NativeDisplayCoordinatorStatus ExportPersistence(in NativeDisplayPersistenceInput input,
        out NativeDisplayPersistenceHeader header, Span<NativeDisplayFact> facts, Span<NativeDisplayTextInput> texts, Span<byte> bytes);
    NativeDisplayCoordinatorStatus ImportPersistence(in NativeDisplayPersistenceInput input,
        in NativeDisplayPersistenceHeader header, ReadOnlySpan<NativeDisplayFact> facts, ReadOnlySpan<NativeDisplayTextInput> texts, ReadOnlySpan<byte> bytes);
}

/// <summary>The host's original optional file-store operations. No path, machine identity or file policy is selected here.</summary>
public sealed record NativeDisplayCoordinatorPersistenceCallbacks(
    Func<ulong, NativeDisplayCoordinatorPersistenceImage?> Load,
    Action<ulong, NativeDisplayCoordinatorPersistenceImage> Save);

public sealed record NativeDisplayCoordinatorReadView(
    HostManagerDisplayCoordinatorReadModel Current, NativeDisplaySnapshotOutput Metadata);
