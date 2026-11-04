using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.ReadWrite.Windows.Processes;

public sealed partial class WindowsProcessFieldOwner
{
    private const ProcessMetricReadMask FixedMask = ProcessMetricReadMask.StartTime | ProcessMetricReadMask.ImagePath
        | ProcessMetricReadMask.ParentPid | ProcessMetricReadMask.UserSid | ProcessMetricReadMask.CommandLine;
    // This is part of the original field owner, serialized by CaptureGate.
    // The mask records attempted fields too: an unavailable fixed fact is not retried each frame.
    private readonly Dictionary<ProcessInstanceKey, FixedProcessFields> fixedFields = new();
    private sealed record FixedProcessFields(ProcessMetricReadMask Attempted, ProcessBasicMetricObservation Value);

    private ProcessMetricReadMask SelectUncachedFields(int pid, long birth, ProcessMetricReadMask requested)
    {
        if (pid <= 0 || birth <= 0) return ProcessMetricReadMask.None;
        var attempted = fixedFields.GetValueOrDefault(new(pid, birth))?.Attempted ?? ProcessMetricReadMask.None;
        var missing = requested & ~attempted;
        // A newly read fixed fact is admitted only after the same handle verified its birth.
        return (missing & FixedMask) != 0 ? missing | ProcessMetricReadMask.StartTime : missing;
    }

    /// <summary>Reads one inventory entry. A kernel-table entry answers its dynamic categories from its own record
    /// in this capture's table; its handle is opened only for fixed facts this instance has not attempted.
    /// An entry without table counters reads every requested category through its handle.</summary>
    [RuntimeReadCache]
    internal ProcessBasicMetricObservation ReadBasic(WindowsProcessInventoryEntry process, ProcessMetricReadMask requested)
    {
        var birth = process.Creation.Creation ?? 0;
        if (process.Counters is not { } counters) return ReadBasic(process.Id, birth, requested);
        var handleFields = requested & ~WindowsProcessTableCounters.Fields;
        return counters.Fill(requested, handleFields == ProcessMetricReadMask.None
            ? ProcessBasicMetricObservation.Unavailable : ReadBasic(process.Id, birth, handleFields));
    }

    /// <summary>Original read-once boundary for PID/birth facts; dynamic categories still query every frame.</summary>
    [RuntimeReadCache]
    internal ProcessBasicMetricObservation ReadBasic(int pid, long birth, ProcessMetricReadMask requested)
    {
        if ((requested & FixedMask) == 0) return BasicReader.Read(pid, birth, requested);
        var uncached = SelectUncachedFields(pid, birth, requested);
        var reading = BasicReader.Read(pid, birth, uncached);
        return ReuseFixedFields(pid, birth, requested, uncached, reading);
    }

    private ProcessBasicMetricObservation ReuseFixedFields(int pid, long birth, ProcessMetricReadMask requested,
        ProcessMetricReadMask queried, ProcessBasicMetricObservation reading)
    {
        if ((requested & FixedMask) == 0 || pid <= 0 || birth <= 0) return reading;
        var key = new ProcessInstanceKey(pid, birth);
        fixedFields.TryGetValue(key, out var previous);
        var value = previous?.Value ?? ProcessBasicMetricObservation.Unavailable;
        if (reading.CreationFileTime100Ns == birth)
        {
            value = value with
            {
                CreationFileTime100Ns = birth,
                ImagePath = queried.HasFlag(ProcessMetricReadMask.ImagePath) ? reading.ImagePath : value.ImagePath,
                ParentPid = queried.HasFlag(ProcessMetricReadMask.ParentPid) ? reading.ParentPid : value.ParentPid,
                UserSid = queried.HasFlag(ProcessMetricReadMask.UserSid) ? reading.UserSid : value.UserSid,
                CommandLine = queried.HasFlag(ProcessMetricReadMask.CommandLine) ? reading.CommandLine : value.CommandLine
            };
            fixedFields[key] = new((previous?.Attempted ?? ProcessMetricReadMask.None) | (queried & FixedMask), value);
        }
        return reading with
        {
            CreationFileTime100Ns = requested.HasFlag(ProcessMetricReadMask.StartTime) ? value.CreationFileTime100Ns : null,
            ImagePath = requested.HasFlag(ProcessMetricReadMask.ImagePath) ? value.ImagePath : null,
            ParentPid = requested.HasFlag(ProcessMetricReadMask.ParentPid) ? value.ParentPid : null,
            UserSid = requested.HasFlag(ProcessMetricReadMask.UserSid) ? value.UserSid : null,
            CommandLine = requested.HasFlag(ProcessMetricReadMask.CommandLine) ? value.CommandLine : null
        };
    }

    private void PruneFixedFields(IReadOnlySet<ProcessInstanceKey> live)
    {
        foreach (var key in fixedFields.Keys.Where(key => !live.Contains(key)).ToArray()) fixedFields.Remove(key);
    }
}

