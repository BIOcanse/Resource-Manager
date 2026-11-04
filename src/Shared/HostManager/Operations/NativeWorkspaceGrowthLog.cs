using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal enum NativeWorkspaceGrowthReason : uint
{
    none, operation_submit, operation_decode, ownership_promote, ownership_decode,
    journal_prepare, journal_batch, journal_decode, smart_process, smart_software,
    smart_gpu, smart_reservation, smart_atomic_group, report_rules, report_import,
    report_facts, report_trust, report_reports, report_persistence
}

internal enum NativeWorkspaceCapacityUnit : uint { Rows, Bytes }

[StructLayout(LayoutKind.Sequential)]
internal struct NativeWorkspaceGrowthEvent
{
    public ulong Sequence;
    public NativeWorkspaceGrowthReason Reason;
    public NativeWorkspaceCapacityUnit Unit;
    public ulong OldCapacity;
    public ulong NewCapacity;

    public readonly string Owner => (uint)Reason switch
    {
        >= 1 and <= 2 => "operation-coordinator",
        >= 3 and <= 4 => "applied-ownership",
        >= 5 and <= 7 => "transaction-journal",
        >= 8 and <= 12 => "smart-coordinator",
        >= 13 and <= 18 => "report-coordinator",
        _ => throw new InvalidOperationException("Unknown native workspace growth reason.")
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeWorkspaceGrowthEvents
{
    public NativeWorkspaceGrowthEvent Event0;
    public NativeWorkspaceGrowthEvent Event1;
    public NativeWorkspaceGrowthEvent Event2;
    public NativeWorkspaceGrowthEvent Event3;
    public NativeWorkspaceGrowthEvent Event4;
    public NativeWorkspaceGrowthEvent Event5;
    public NativeWorkspaceGrowthEvent Event6;
    public NativeWorkspaceGrowthEvent Event7;

    public NativeWorkspaceGrowthEvent this[int index]
    {
        readonly get => index switch
        {
            0 => Event0, 1 => Event1, 2 => Event2, 3 => Event3,
            4 => Event4, 5 => Event5, 6 => Event6, 7 => Event7,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
        set
        {
            switch (index)
            {
                case 0: Event0 = value; break;
                case 1: Event1 = value; break;
                case 2: Event2 = value; break;
                case 3: Event3 = value; break;
                case 4: Event4 = value; break;
                case 5: Event5 = value; break;
                case 6: Event6 = value; break;
                case 7: Event7 = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeWorkspaceGrowthLog
{
    public ulong Count;
    public NativeWorkspaceGrowthEvents Events;
}

// Each caller reads immediately after a growth-capable native operation, under
// its existing session lock. Eight entries cover every published growth in one
// operation (at most five Smart banks, or the Report preparation phases).
internal sealed class NativeWorkspaceGrowthObserver
{
    private ulong observed;
    internal Action<NativeWorkspaceGrowthEvent>? Observer { get; set; }

    internal void Read(in NativeWorkspaceGrowthLog log)
    {
        if (Observer is not { } observer) return;
        if (log.Count < observed || log.Count - observed > 8)
            throw new InvalidOperationException("Native workspace growth diagnostics were not consumed before their bounded window expired.");
        while (observed < log.Count)
        {
            var sequence = checked(observed + 1);
            var entry = log.Events[checked((int)((sequence - 1) % 8))];
            if (entry.Sequence != sequence || entry.Reason == NativeWorkspaceGrowthReason.none ||
                !Enum.IsDefined(entry.Reason) || !Enum.IsDefined(entry.Unit) || entry.NewCapacity <= entry.OldCapacity)
                throw new InvalidOperationException("Native workspace growth diagnostics are invalid.");
            observer(entry);
            observed = sequence;
        }
    }
}
