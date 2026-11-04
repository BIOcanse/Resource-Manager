using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class WindowsResourceBreakdownSampler
{
    private static ProcessMetricFieldSnapshot CreateProcessBasicFields(IEnumerable<ProcessMetricFieldDefinition> selected,
        IReadOnlyList<ProcessResourceSample> samples, DateTimeOffset capturedAt, TimeSpan? cpuInterval, ProcessIoRatePreparation? ioRates = null)
        => WindowsProcessFieldProjection.CreateProcessBasicFields(selected, FieldProjectionSamples(samples), capturedAt, cpuInterval, ioRates);
    private static ProcessFieldProjectionSample[] FieldProjectionSamples(IReadOnlyList<ProcessResourceSample> samples)
        => samples.Select(sample => new ProcessFieldProjectionSample(sample.ProcessId, sample.StartKey,
            sample.BasicMetrics, sample.CpuPercent, sample.HasCpuPercent)).ToArray();
}
