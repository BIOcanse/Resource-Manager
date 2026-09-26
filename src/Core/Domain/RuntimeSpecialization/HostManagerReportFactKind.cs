namespace ResourceManager.App.Domain.RuntimeSpecialization;

public enum HostManagerReportFactKind : uint
{
    CpuTemperatureCelsius = 1,
    CpuUsagePercent = 2,
    CpuFrequencyPercent = 3,
    InterruptMaximumSingleDurationMilliseconds = 4,
    InterruptEventsAtOrAboveOneMillisecond = 5,
    InterruptCpuCapacityPercent = 6,
    SoftwareMemorySystemPercent = 7,

    // 内存报告是双重判断：小机器先过百分比，大机器靠绝对值。
    // 两者是各自独立的谓词组、共用同一个 family，所以任一成立都会出报告（或关系）。
    SoftwareMemoryBytes = 8
}
