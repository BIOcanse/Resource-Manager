using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledBackendControlSettings(
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.WorkerCount)]
    int WorkerCount,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.HistoryCapacity)]
    int HistoryCapacity,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.CompletionCapacity)]
    int CompletionCapacity,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.ReaderCapacity)]
    int ReaderCapacity,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.ReaderBytes)]
    int ReaderBytes,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.WorkerArenaBytes)]
    int WorkerArenaBytes,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.ReasonTextCapacity)]
    int ReasonTextCapacity)
{
    public bool IsPublished => WorkerCount > 0
        && HistoryCapacity >= 0
        && CompletionCapacity > 0
        && ReaderCapacity > 0
        && ReaderBytes > 0
        && WorkerArenaBytes > 0
        && ReasonTextCapacity > 0;

    public static CompiledBackendControlSettings Unpublished { get; } = new(0, 0, 0, 0, 0, 0, 0);
}
