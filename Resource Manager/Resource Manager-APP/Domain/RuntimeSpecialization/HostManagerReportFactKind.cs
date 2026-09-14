namespace ResourceManager.App.Domain.RuntimeSpecialization;

public enum HostManagerReportFactKind : uint
{
    CpuTemperatureCelsius = 1,
    CpuUsagePercent = 2,
    CpuFrequencyPercent = 3,
    InterruptMaximumSingleDurationMilliseconds = 4,
    InterruptEventsAtOrAboveOneMillisecond = 5,
    InterruptCpuCapacityPercent = 6,
    SoftwareMemorySystemPercent = 7
}
