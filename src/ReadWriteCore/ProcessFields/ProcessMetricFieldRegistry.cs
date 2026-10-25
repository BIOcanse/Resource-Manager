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
    [property: JsonIgnore] ProcessMetricFieldCode Code, string Name, string ValueKind, string Unit,
    string SourceId = "windows.etw.network",
    [property: JsonIgnore] string DatasetId = ProcessMetricDatasetIds.ObservedNetworkTraffic,
    [property: JsonIgnore] ProcessMetricReadMask ReadMask = ProcessMetricReadMask.None)
{
    public string Resource => "process";
    public string Vendor => "all";
    public ProcessMetricReading? Reading => Code switch
    {
        ProcessMetricFieldCode.WorkingSet => new("memory", "working_set_including_shared"),
        ProcessMetricFieldCode.PrivateWorkingSet => new("memory", "private_working_set"),
        ProcessMetricFieldCode.PrivateCommit => new("memory", "private_commit"),
        ProcessMetricFieldCode.PeakPrivateCommit => new("memory", "peak_private_commit"),
        _ => null
    };
}

public sealed record ProcessMetricReading(string Resource, string Quantity);

public static class ProcessMetricFieldRegistry
{
    public static IReadOnlyList<ProcessMetricFieldDefinition> Definitions { get; } =
    [
        new(ProcessMetricFieldCode.NetworkReceiveBytes, "process_all_network_receive_bytes_attributed", "unsigned64", "B"),
        new(ProcessMetricFieldCode.NetworkSendBytes, "process_all_network_send_bytes_attributed", "unsigned64", "B"),
        new(ProcessMetricFieldCode.NetworkReceiveRate, "process_all_network_receive_rate_attributed", "float64", "B/s"),
        new(ProcessMetricFieldCode.NetworkSendRate, "process_all_network_send_rate_attributed", "float64", "B/s"),
        new(ProcessMetricFieldCode.PhysicalDiskReadBytes, "process_all_physical_disk_read_bytes_attributed", "unsigned64", "B",
            "windows.etw.physical-disk", ProcessMetricDatasetIds.ObservedPhysicalDiskIo),
        new(ProcessMetricFieldCode.PhysicalDiskWriteBytes, "process_all_physical_disk_write_bytes_attributed", "unsigned64", "B",
            "windows.etw.physical-disk", ProcessMetricDatasetIds.ObservedPhysicalDiskIo),
        new(ProcessMetricFieldCode.GpuResidency, WindowsPdhProcessGpuFields.FieldName, "float64", "%", "windows.pdh.gpu-engine", ProcessMetricDatasetIds.GpuResidency),
        new(ProcessMetricFieldCode.CpuResidency, "process_all_cpu_residency_attributed", "float64", "%",
            "windows.etw.cpu-residency", ProcessMetricDatasetIds.ObservedCpuResidency),
        Basic(ProcessMetricFieldCode.CpuTime, "process_all_cpu_time_actual", "float64", "s", ProcessMetricReadMask.CpuTime),
        Basic(ProcessMetricFieldCode.WorkingSet, "process_all_working_set_actual", "unsigned64", "B", ProcessMetricReadMask.Memory),
        Basic(ProcessMetricFieldCode.PrivateWorkingSet, "process_all_private_working_set_actual", "unsigned64", "B", ProcessMetricReadMask.PrivateWorkingSet),
        Basic(ProcessMetricFieldCode.PrivateCommit, "process_all_private_commit_actual", "unsigned64", "B", ProcessMetricReadMask.Memory),
        Basic(ProcessMetricFieldCode.PeakPrivateCommit, "process_all_peak_private_commit_actual", "unsigned64", "B", ProcessMetricReadMask.Memory),
        Basic(ProcessMetricFieldCode.IoReadBytes, "process_all_io_read_bytes_actual", "unsigned64", "B", ProcessMetricReadMask.Io),
        Basic(ProcessMetricFieldCode.IoWriteBytes, "process_all_io_write_bytes_actual", "unsigned64", "B", ProcessMetricReadMask.Io),
        Basic(ProcessMetricFieldCode.IoOtherBytes, "process_all_io_other_bytes_actual", "unsigned64", "B", ProcessMetricReadMask.Io),
        Basic(ProcessMetricFieldCode.IoOperations, "process_all_io_operations_actual", "unsigned128", "count", ProcessMetricReadMask.Io),
        Basic(ProcessMetricFieldCode.IoReadOperations, "process_all_io_read_operations_actual", "unsigned64", "count", ProcessMetricReadMask.Io),
        Basic(ProcessMetricFieldCode.IoWriteOperations, "process_all_io_write_operations_actual", "unsigned64", "count", ProcessMetricReadMask.Io),
        Basic(ProcessMetricFieldCode.IoOtherOperations, "process_all_io_other_operations_actual", "unsigned64", "count", ProcessMetricReadMask.Io),
        new(ProcessMetricFieldCode.IoReadOperationsRate, "process_all_io_read_operations_rate_actual", "float64", "ops/s",
            "windows.process.io-interval", ProcessMetricDatasetIds.BasicMetrics, ProcessMetricReadMask.Io),
        new(ProcessMetricFieldCode.IoWriteOperationsRate, "process_all_io_write_operations_rate_actual", "float64", "ops/s",
            "windows.process.io-interval", ProcessMetricDatasetIds.BasicMetrics, ProcessMetricReadMask.Io),
        new(ProcessMetricFieldCode.IoOtherOperationsRate, "process_all_io_other_operations_rate_actual", "float64", "ops/s",
            "windows.process.io-interval", ProcessMetricDatasetIds.BasicMetrics, ProcessMetricReadMask.Io),
        new(ProcessMetricFieldCode.IoReadBytesRate, "process_all_io_read_bytes_rate_actual", "float64", "B/s",
            "windows.process.io-interval", ProcessMetricDatasetIds.BasicMetrics, ProcessMetricReadMask.Io),
        new(ProcessMetricFieldCode.IoWriteBytesRate, "process_all_io_write_bytes_rate_actual", "float64", "B/s",
            "windows.process.io-interval", ProcessMetricDatasetIds.BasicMetrics, ProcessMetricReadMask.Io),
        new(ProcessMetricFieldCode.IoOtherBytesRate, "process_all_io_other_bytes_rate_actual", "float64", "B/s",
            "windows.process.io-interval", ProcessMetricDatasetIds.BasicMetrics, ProcessMetricReadMask.Io),
        Basic(ProcessMetricFieldCode.HandleCount, "process_all_handle_count_actual", "unsigned64", "count", ProcessMetricReadMask.HandleCount),
        new(ProcessMetricFieldCode.CpuUsage, "process_all_cpu_usage_actual", "float64", "%",
            "windows.process.cpu-interval", ProcessMetricDatasetIds.BasicMetrics, ProcessMetricReadMask.CpuTime),
        Basic(ProcessMetricFieldCode.StartTime, "process_all_start_time_actual", "string", "UTC", ProcessMetricReadMask.StartTime),
        Basic(ProcessMetricFieldCode.ImagePath, "process_all_image_path_actual", "string", "text", ProcessMetricReadMask.ImagePath),
        Basic(ProcessMetricFieldCode.ParentPid, "process_all_parent_pid_actual", "unsigned64", "pid", ProcessMetricReadMask.ParentPid),
        Basic(ProcessMetricFieldCode.UserSid, "process_all_user_sid_actual", "string", "text", ProcessMetricReadMask.UserSid),
        Basic(ProcessMetricFieldCode.ThreadCount, "process_all_thread_count_actual", "unsigned64", "count", ProcessMetricReadMask.ThreadCount),
        Basic(ProcessMetricFieldCode.CommandLine, "process_all_command_line_actual", "string", "text", ProcessMetricReadMask.CommandLine)
    ];
    private static readonly FrozenDictionary<string, ProcessMetricFieldDefinition> ByName =
        Definitions.ToFrozenDictionary(static field => field.Name, StringComparer.OrdinalIgnoreCase);
    public static ProcessMetricFieldDefinition? Find(string name) => ByName.GetValueOrDefault(name);

    private static ProcessMetricFieldDefinition Basic(ProcessMetricFieldCode code, string name, string kind,
        string unit, ProcessMetricReadMask mask)
        => new(code, name, kind, unit, "windows.process.query", ProcessMetricDatasetIds.BasicMetrics, mask);
}

