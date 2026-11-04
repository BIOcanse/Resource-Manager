using ResourceManager.ReadWrite.Native.MetricOwner;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig;

public enum MetricOwnerOperation { Catalog, Round, History, CheckpointExport, CheckpointRestore, ObjectReaders, ObjectSources }
public readonly record struct MetricOwnerFailureTransition(MetricOwnerOperation Operation,
    MetricOwnerFailure Failure, bool Recovered);

/// <summary>The subscription shell logs these transitions once; reads never log or retry.</summary>
internal sealed class MetricOwnerFailureTransitions(Action<MetricOwnerFailureTransition>? changed)
{
    private readonly MetricOwnerFailure[] states = new MetricOwnerFailure[7]; // one per operation, structural count
    internal void Set(MetricOwnerOperation operation, MetricOwnerFailure failure)
    {
        ref var previous = ref states[(int)operation];
        if (previous.Code == failure.Code && previous.Family == failure.Family) return;
        var recovered = failure.Code == MetricOwnerCode.Ok;
        var report = recovered ? previous : failure;
        previous = failure;
        changed?.Invoke(new(operation, report, recovered));
    }
}
