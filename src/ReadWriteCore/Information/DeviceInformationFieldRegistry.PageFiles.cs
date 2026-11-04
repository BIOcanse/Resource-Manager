namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition MemoryAllPagefilePathReported =
        new("memory_all_pagefile_path_reported", "memory", "all", "text", "text", "memory.pagefile.path_reported", "windows.pagefile", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition MemoryAllPagefileCapacity =
        new("memory_all_pagefile_capacity", "memory", "all", "unsigned-integer", "B", "memory.pagefile.capacity_reported", "windows.pagefile", ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition MemoryAllPagefileUsed =
        new("memory_all_pagefile_used", "memory", "all", "unsigned-integer", "B", "memory.pagefile.used_reported", "windows.pagefile", ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition MemoryAllPagefilePeakUsedReportedBytes =
        new("memory_all_pagefile_peak_used_reported_bytes", "memory", "all", "unsigned-integer", "B", "memory.pagefile.peak_used_reported", "windows.pagefile", ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition MemoryAllPagefileCapacityReportedPages =
        new("memory_all_pagefile_capacity_reported_pages", "memory", "all", "unsigned-integer", "page", "memory.pagefile.capacity_reported", "windows.pagefile", ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition MemoryAllPagefileUsedReportedPages =
        new("memory_all_pagefile_used_reported_pages", "memory", "all", "unsigned-integer", "page", "memory.pagefile.used_reported", "windows.pagefile", ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition MemoryAllPagefilePeakUsedReportedPages =
        new("memory_all_pagefile_peak_used_reported_pages", "memory", "all", "unsigned-integer", "page", "memory.pagefile.peak_used_reported", "windows.pagefile", ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition MemoryAllPagefilePageSizeReportedBytes =
        new("memory_all_pagefile_page_size_reported_bytes", "memory", "all", "unsigned-integer", "B/page", "memory.pagefile.page_size_reported", "windows.pagefile", ReadVolatility.Fixed);
}
