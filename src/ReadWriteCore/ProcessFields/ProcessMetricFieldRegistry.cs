using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace ResourceManager.ReadWrite.ProcessFields;

public enum ProcessMetricFieldCode
{
    NetworkReceiveBytes, NetworkSendBytes, NetworkReceiveRate, NetworkSendRate,
    CpuTime, WorkingSet, PrivateWorkingSet, PrivateCommit, PeakPrivateCommit,
    IoReadBytes, IoWriteBytes, IoOtherBytes, IoOperations, HandleCount, CpuUsage,
    StartTime, ImagePath, ParentPid, UserSid, ThreadCount, CommandLine, PhysicalDiskReadBytes, PhysicalDiskWriteBytes, CpuResidency, GpuResidency,
    IoReadOperations, IoWriteOperations, IoOtherOperations,
    IoReadOperationsRate, IoWriteOperationsRate, IoOtherOperationsRate, IoReadBytesRate, IoWriteBytesRate, IoOtherBytesRate
}

[Flags]
public enum ProcessMetricReadMask { None = 0, Memory = 1, PrivateWorkingSet = 2, CpuTime = 4, Io = 8, HandleCount = 16,
    StartTime = 32, ImagePath = 64, ParentPid = 128, UserSid = 256, ThreadCount = 512, CommandLine = 1024 }

public sealed record ProcessMetricFieldDefinition(
    [property: JsonIgnore] ProcessMetricFieldCode Code, string Name, string ValueKind, string Unit, string Meaning,
    string SourceId = "windows.etw.network",
    [property: JsonIgnore] string DatasetId = ProcessMetricDatasetIds.ObservedNetworkTraffic,
    [property: JsonIgnore] ProcessMetricReadMask ReadMask = ProcessMetricReadMask.None)
{
    public string Resource => "process";
    public string Vendor => "all";
}

public static class ProcessMetricFieldRegistry
{
    public static IReadOnlyList<ProcessMetricFieldDefinition> Definitions { get; } =
    [
        new(ProcessMetricFieldCode.NetworkReceiveBytes, "process_all_network_receive_bytes_attributed", "unsigned64", "B", "TCP/UDP received event size in the complete two-second delivery window, including loopback; not lifetime, application payload or wire measurement"),
        new(ProcessMetricFieldCode.NetworkSendBytes, "process_all_network_send_bytes_attributed", "unsigned64", "B", "TCP/UDP sent event size in the complete two-second delivery window, including loopback; no proxy redistribution"),
        new(ProcessMetricFieldCode.NetworkReceiveRate, "process_all_network_receive_rate_attributed", "float64", "B/s", "Received event size divided by the two-second monotonic delivery window"),
        new(ProcessMetricFieldCode.NetworkSendRate, "process_all_network_send_rate_attributed", "float64", "B/s", "Sent event size divided by the two-second monotonic delivery window"),
        new(ProcessMetricFieldCode.PhysicalDiskReadBytes, "process_all_physical_disk_read_bytes_attributed", "unsigned64", "B",
            "DiskIO completed read transfer sizes delivered in a complete two-second window, per ETW OS DiskNumber/session and verified PID creation; not lifetime IO, MSFT PhysicalDisk identity or media persistence",
            "windows.etw.physical-disk", ProcessMetricDatasetIds.ObservedPhysicalDiskIo),
        new(ProcessMetricFieldCode.PhysicalDiskWriteBytes, "process_all_physical_disk_write_bytes_attributed", "unsigned64", "B",
            "DiskIO completed write transfer sizes delivered in a complete two-second window, per ETW OS DiskNumber/session and verified PID creation; completion does not prove durable media writes",
            "windows.etw.physical-disk", ProcessMetricDatasetIds.ObservedPhysicalDiskIo),
        new(ProcessMetricFieldCode.GpuResidency, WindowsPdhProcessGpuFields.FieldName, "float64", "%", "Original PDH GPU Engine busy percent over the proven native counter interval, per LUID/physical/engine and PID+creation verified after read; not VidMm bytes, requested target, cross-engine sum or fresh data when LastGood", "windows.pdh.gpu-engine", ProcessMetricDatasetIds.GpuResidency),
        new(ProcessMetricFieldCode.CpuResidency, "process_all_cpu_residency_attributed", "float64", "%",
            "Attributed closed CSwitch execution on this logical CPU divided by this PID/FILETIME instance's positive attributed execution across all logical CPUs in the exact continuous window; not wall-time CPU busy percentage, configured affinity or complete thread attribution",
            "windows.etw.cpu-residency", ProcessMetricDatasetIds.ObservedCpuResidency),
        Basic(ProcessMetricFieldCode.CpuTime, "process_all_cpu_time_actual", "float64", "s", "GetProcessTimes cumulative kernel plus user time; not CPU percent", ProcessMetricReadMask.CpuTime),
        Basic(ProcessMetricFieldCode.WorkingSet, "process_all_working_set_actual", "unsigned64", "B", "Current resident working set including shared pages; not private commit or a cross-process unique sum", ProcessMetricReadMask.Memory),
        Basic(ProcessMetricFieldCode.PrivateWorkingSet, "process_all_private_working_set_actual", "unsigned64", "B", "EX2 current private resident working set; unsupported systems remain null", ProcessMetricReadMask.PrivateWorkingSet),
        Basic(ProcessMetricFieldCode.PrivateCommit, "process_all_private_commit_actual", "unsigned64", "B", "PrivateUsage current private commit charge; not resident RAM or bytes written to the page file", ProcessMetricReadMask.Memory),
        Basic(ProcessMetricFieldCode.PeakPrivateCommit, "process_all_peak_private_commit_actual", "unsigned64", "B", "PeakPagefileUsage lifetime peak private commit charge; not peak resident memory", ProcessMetricReadMask.Memory),
        Basic(ProcessMetricFieldCode.IoReadBytes, "process_all_io_read_bytes_actual", "unsigned64", "B", "GetProcessIoCounters lifetime read transfer count; includes cached/device IO, not physical disk throughput", ProcessMetricReadMask.Io),
        Basic(ProcessMetricFieldCode.IoWriteBytes, "process_all_io_write_bytes_actual", "unsigned64", "B", "GetProcessIoCounters lifetime write transfer count; not disk or network attribution", ProcessMetricReadMask.Io),
        Basic(ProcessMetricFieldCode.IoOtherBytes, "process_all_io_other_bytes_actual", "unsigned64", "B", "GetProcessIoCounters lifetime transfer count other than reads/writes", ProcessMetricReadMask.Io),
        Basic(ProcessMetricFieldCode.IoOperations, "process_all_io_operations_actual", "unsigned128", "count", "Exact sum of lifetime read/write/other operation counts; not bytes", ProcessMetricReadMask.Io),
        Basic(ProcessMetricFieldCode.IoReadOperations, "process_all_io_read_operations_actual", "unsigned64", "count", "GetProcessIoCounters lifetime read operation count; not bytes or physical disk attribution", ProcessMetricReadMask.Io),
        Basic(ProcessMetricFieldCode.IoWriteOperations, "process_all_io_write_operations_actual", "unsigned64", "count", "GetProcessIoCounters lifetime write operation count; not bytes or physical disk attribution", ProcessMetricReadMask.Io),
        Basic(ProcessMetricFieldCode.IoOtherOperations, "process_all_io_other_operations_actual", "unsigned64", "count", "GetProcessIoCounters lifetime other operation count; not bytes or physical disk attribution", ProcessMetricReadMask.Io),
        new(ProcessMetricFieldCode.IoReadOperationsRate, "process_all_io_read_operations_rate_actual", "float64", "ops/s",
            "Identity-verified GetProcessIoCounters read operations integer delta over this process instance's two accepted query-return monotonic ticks; first, failure recovery, rollback, changed frequency or nonpositive interval is null; not disk/network attribution",
            "windows.process.io-interval", ProcessMetricDatasetIds.BasicMetrics, ProcessMetricReadMask.Io),
        new(ProcessMetricFieldCode.IoWriteOperationsRate, "process_all_io_write_operations_rate_actual", "float64", "ops/s",
            "Identity-verified GetProcessIoCounters write operations integer delta over this process instance's two accepted query-return monotonic ticks; first, failure recovery, rollback, changed frequency or nonpositive interval is null; not disk/network attribution",
            "windows.process.io-interval", ProcessMetricDatasetIds.BasicMetrics, ProcessMetricReadMask.Io),
        new(ProcessMetricFieldCode.IoOtherOperationsRate, "process_all_io_other_operations_rate_actual", "float64", "ops/s",
            "Identity-verified GetProcessIoCounters other operations integer delta over this process instance's two accepted query-return monotonic ticks; first, failure recovery, rollback, changed frequency or nonpositive interval is null; not disk/network attribution",
            "windows.process.io-interval", ProcessMetricDatasetIds.BasicMetrics, ProcessMetricReadMask.Io),
        new(ProcessMetricFieldCode.IoReadBytesRate, "process_all_io_read_bytes_rate_actual", "float64", "B/s",
            "Identity-verified GetProcessIoCounters read bytes integer delta over this process instance's two accepted query-return monotonic ticks; first, failure recovery, rollback, changed frequency or nonpositive interval is null; not disk/network attribution",
            "windows.process.io-interval", ProcessMetricDatasetIds.BasicMetrics, ProcessMetricReadMask.Io),
        new(ProcessMetricFieldCode.IoWriteBytesRate, "process_all_io_write_bytes_rate_actual", "float64", "B/s",
            "Identity-verified GetProcessIoCounters write bytes integer delta over this process instance's two accepted query-return monotonic ticks; first, failure recovery, rollback, changed frequency or nonpositive interval is null; not disk/network attribution",
            "windows.process.io-interval", ProcessMetricDatasetIds.BasicMetrics, ProcessMetricReadMask.Io),
        new(ProcessMetricFieldCode.IoOtherBytesRate, "process_all_io_other_bytes_rate_actual", "float64", "B/s",
            "Identity-verified GetProcessIoCounters other bytes integer delta over this process instance's two accepted query-return monotonic ticks; first, failure recovery, rollback, changed frequency or nonpositive interval is null; not disk/network attribution",
            "windows.process.io-interval", ProcessMetricDatasetIds.BasicMetrics, ProcessMetricReadMask.Io),
        Basic(ProcessMetricFieldCode.HandleCount, "process_all_handle_count_actual", "unsigned64", "count", "GetProcessHandleCount current open handle count", ProcessMetricReadMask.HandleCount),
        new(ProcessMetricFieldCode.CpuUsage, "process_all_cpu_usage_actual", "float64", "%",
            "Identity-verified GetProcessTimes kernel plus user delta divided by the actual capture-start interval and Environment.ProcessorCount, times 100; normalized to the processor count available to this process, not per-core usage; first sample, counter decrease or nonpositive interval is null",
            "windows.process.cpu-interval", ProcessMetricDatasetIds.BasicMetrics, ProcessMetricReadMask.CpuTime),
        Basic(ProcessMetricFieldCode.StartTime, "process_all_start_time_actual", "string", "UTC", "GetProcessTimes creation FILETIME rendered as UTC with seven fractional digits; the same 100ns identity used by the process directory", ProcessMetricReadMask.StartTime),
        Basic(ProcessMetricFieldCode.ImagePath, "process_all_image_path_actual", "string", "text", "QueryFullProcessImageName on the same creation-verified read-only process handle; not an inferred software root", ProcessMetricReadMask.ImagePath),
        Basic(ProcessMetricFieldCode.ParentPid, "process_all_parent_pid_actual", "unsigned64", "pid", "NtQueryInformationProcess basic information inherited parent PID for this creation; not a verified current parent instance", ProcessMetricReadMask.ParentPid),
        Basic(ProcessMetricFieldCode.UserSid, "process_all_user_sid_actual", "string", "text", "TokenUser SID from the same creation-verified read-only process handle; not a translated account display name", ProcessMetricReadMask.UserSid),
        Basic(ProcessMetricFieldCode.ThreadCount, "process_all_thread_count_actual", "unsigned64", "count", "PROCESSENTRY32.cntThreads in a read-only Toolhelp process snapshot while the same FILETIME-verified process handle remains nonsignaled before/after; instantaneous snapshot, not all-system thread count", ProcessMetricReadMask.ThreadCount),
        Basic(ProcessMetricFieldCode.CommandLine, "process_all_command_line_actual", "string", "text", "NtQueryInformationProcess ProcessCommandLineInformation raw UTF-16 text on the same FILETIME-verified process handle; quotes, whitespace and valid empty text retained, not parsed argv or inferred executable", ProcessMetricReadMask.CommandLine)
    ];
    private static readonly FrozenDictionary<string, ProcessMetricFieldDefinition> ByName =
        Definitions.ToFrozenDictionary(static field => field.Name, StringComparer.OrdinalIgnoreCase);
    public static ProcessMetricFieldDefinition? Find(string name) => ByName.GetValueOrDefault(name);

    private static ProcessMetricFieldDefinition Basic(ProcessMetricFieldCode code, string name, string kind,
        string unit, string meaning, ProcessMetricReadMask mask)
        => new(code, name, kind, unit, meaning, "windows.process.query", ProcessMetricDatasetIds.BasicMetrics, mask);
}

