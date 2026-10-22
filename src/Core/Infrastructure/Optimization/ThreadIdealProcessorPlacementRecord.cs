using System.Globalization;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed record ThreadIdealProcessorPlacementRecord(int ProcessId, ulong ProcessStartKey,
    int ThreadId, ulong ThreadStartKey, ThreadIdealProcessor Previous, ThreadIdealProcessor Applied)
{
    internal bool Matches(ThreadIdealProcessorPolicySnapshot value)
        => value.ProcessId == ProcessId && value.ProcessStartKey == ProcessStartKey
            && value.ThreadId == ThreadId && value.ThreadStartKey == ThreadStartKey;

    internal static bool TryRead(HostManagerAppliedRecord record, out ThreadIdealProcessorPlacementRecord value)
    {
        value = null!;
        var data = record.Metadata;
        if (record.Kind != HostManagerAppliedRecordKinds.CpuThreadIdealProcessor || data is null
            || !int.TryParse(data.GetValueOrDefault("processId"), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 4
            || !ulong.TryParse(data.GetValueOrDefault("processStartKey"), NumberStyles.None, CultureInfo.InvariantCulture, out var process) || process == 0
            || !int.TryParse(data.GetValueOrDefault("threadId"), NumberStyles.None, CultureInfo.InvariantCulture, out var tid) || tid <= 0
            || !ulong.TryParse(data.GetValueOrDefault("threadStartKey"), NumberStyles.None, CultureInfo.InvariantCulture, out var thread) || thread == 0
            || !TryReadProcessor(data, "previous", out var previous) || !TryReadProcessor(data, "applied", out var applied)
            || previous == applied || previous.Group != applied.Group) return false;
        value = new(pid, process, tid, thread, previous, applied);
        return true;
    }

    private static bool TryReadProcessor(IReadOnlyDictionary<string, string> data, string prefix, out ThreadIdealProcessor value)
    {
        value = default;
        if (!ushort.TryParse(data.GetValueOrDefault(prefix + "ProcessorGroup"), NumberStyles.None, CultureInfo.InvariantCulture, out var group)
            || !byte.TryParse(data.GetValueOrDefault(prefix + "ProcessorNumber"), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number >= 64) return false;
        value = new(group, number);
        return true;
    }
}
