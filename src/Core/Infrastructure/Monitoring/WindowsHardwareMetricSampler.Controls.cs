using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class WindowsHardwareMetricSampler
{
    // Pure declaration facade borrows the same existing product writer resources.
    // The original sampler retains its gate, current snapshot and subscription worker.
    private readonly WindowsMetricControlFields controlFields;

    internal OperationSupport QueryControl(MetricDeviceFieldBinding binding) => controlFields.Query(binding);
    internal OperationSupport ReadControlSupport(MetricDeviceFieldBinding binding) => controlFields.ReadSupport(binding);
    internal FieldWriteContract? ControlContract(MetricDeviceFieldBinding binding) => controlFields.GetContract(binding);

    internal async ValueTask<FieldWriteResult> ExecuteBoundControlWriteAsync(FieldReference field,
        MetricDeviceFieldBinding binding, FieldValue value,
        Func<CancellationToken, ValueTask<FieldWriteResult>> write, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await sampleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return await controlFields.ExecuteBoundWriteAsync(field, binding, value, write, cancellationToken);
        }
        finally { sampleGate.Release(); }
    }
}
