// Original implementation now belongs to ResourceManager.ReadWriteCore Native/Snapshots.

namespace ResourceManager.App.Infrastructure.Monitoring;

// Product presentation stays with the host; SDK callers receive canonical/raw names.
internal static class HostMetricSnapshotCopy
{
    internal const string VirtualMemoryDetail = "系统提交量 / 当前提交上限；进程分项为私有提交量，包含物理内存与页面文件后备。";
    internal static string LegacyLabel(string id) => id switch
    {
        "cpu.usage" => "CPU 占用率",
        "cpu.frequency" => "CPU 频率",
        "cpu.frequencyPercent" => "CPU 频率百分比",
        "memory.usage" => "物理内存实际占用",
        "memory.percent" => "物理内存实际占用率",
        "virtualMemory.usage" => "虚拟内存提交量",
        "virtualMemory.percent" => "虚拟内存提交率",
        _ => id
    };
}
