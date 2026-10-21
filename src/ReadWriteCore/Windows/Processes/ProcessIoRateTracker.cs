using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.ReadWrite.Windows.Processes;

// Owned only by the original process owner, one per existing Hosted/Direct lane.
// Prepare is side-effect free. Only the original publication callback commits it.
internal sealed class ProcessIoRateTracker
{
    private Dictionary<ProcessInstanceKey, ProcessIoObservation> previous = [];
    internal ProcessIoRatePreparation Prepare(IEnumerable<ProcessFieldProjectionSample> samples)
    {
        var next = new Dictionary<ProcessInstanceKey, ProcessIoObservation>();
        var rates = new Dictionary<ProcessInstanceKey, ProcessIoRateObservation>();
        foreach (var sample in samples)
        {
            if (sample.StartKey is not > 0 || sample.BasicMetrics.Io is not { Timing: { } currentTime } current
                || !Valid(currentTime)) continue;
            var key = new ProcessInstanceKey(sample.ProcessId, sample.StartKey.Value);
            // The complete inventory removes disappeared/failed identities; a duplicate
            // exact identity is a malformed batch, not a second baseline candidate.
            if (!next.TryAdd(key, current)) throw new InvalidOperationException("Duplicate process IO identity.");
            if (!previous.TryGetValue(key, out var prior) || prior.Timing is not { } priorTime
                || priorTime.TicksPerSecond != currentTime.TicksPerSecond
                || currentTime.QueryReturnTicks <= priorTime.QueryReturnTicks
                || current.ReadOperations < prior.ReadOperations || current.WriteOperations < prior.WriteOperations
                || current.OtherOperations < prior.OtherOperations || current.ReadBytes < prior.ReadBytes
                || current.WriteBytes < prior.WriteBytes || current.OtherBytes < prior.OtherBytes) continue;
            var interval = new ProcessIoInterval(priorTime.QueryReturnTicks, currentTime.QueryReturnTicks, currentTime.TicksPerSecond);
            var elapsed = currentTime.QueryReturnTicks - priorTime.QueryReturnTicks;
            double Rate(ulong value, ulong before) => (double)(value - before) * currentTime.TicksPerSecond / elapsed;
            rates.Add(key, new(interval, Rate(current.ReadOperations, prior.ReadOperations), Rate(current.WriteOperations, prior.WriteOperations),
                Rate(current.OtherOperations, prior.OtherOperations), Rate(current.ReadBytes, prior.ReadBytes),
                Rate(current.WriteBytes, prior.WriteBytes), Rate(current.OtherBytes, prior.OtherBytes)));
        }
        return new(this, next, rates);
    }
    private static bool Valid(ProcessIoQueryTiming time)
        => time.TicksPerSecond > 0 && time.QueryStartTicks >= 0 && time.QueryReturnTicks >= time.QueryStartTicks;
    internal void Commit(ProcessIoRatePreparation preparation)
    {
        if (!ReferenceEquals(preparation.Owner, this)) throw new InvalidOperationException("Process IO preparation belongs to another lane.");
        previous = preparation.Next;
    }
    internal void Clear() => previous = [];
}

internal sealed record ProcessIoRatePreparation(ProcessIoRateTracker Owner,
    Dictionary<ProcessInstanceKey, ProcessIoObservation> Next,
    IReadOnlyDictionary<ProcessInstanceKey, ProcessIoRateObservation> Rates)
{
    internal ProcessIoRateObservation? Find(int pid, long? birth)
        => birth is > 0 ? Rates.GetValueOrDefault(new ProcessInstanceKey(pid, birth.Value)) : null;
}

internal sealed record ProcessIoRateObservation(ProcessIoInterval Interval, double ReadOperations, double WriteOperations,
    double OtherOperations, double ReadBytes, double WriteBytes, double OtherBytes)
{
    internal double? Value(ProcessMetricFieldCode code) => code switch
    {
        ProcessMetricFieldCode.IoReadOperationsRate => ReadOperations,
        ProcessMetricFieldCode.IoWriteOperationsRate => WriteOperations,
        ProcessMetricFieldCode.IoOtherOperationsRate => OtherOperations,
        ProcessMetricFieldCode.IoReadBytesRate => ReadBytes,
        ProcessMetricFieldCode.IoWriteBytesRate => WriteBytes,
        ProcessMetricFieldCode.IoOtherBytesRate => OtherBytes,
        _ => null
    };
}
