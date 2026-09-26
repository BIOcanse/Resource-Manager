namespace ResourceManager.App.Application.Metrics;

public static class MetricDisplayDetailFormatter
{
    private static readonly string[] DiagnosticMarkers =
    [
        "Provider",
        "providers",
        "Unavailable",
        "NotRequested",
        "Partial",
        "fallback",
        "WMI",
        "NVML",
        "NVAPI",
        "ADLX",
        "UWACPI",
        "LibreHardwareMonitor",
        "OpenHardwareMonitor",
        "Hardware monitor",
        "DeviceId",
        "当前不推导",
        "未接入",
        "未返回",
        "不使用 Windows API"
    ];

    public static string SensorDetail(string? rawDetail, string fallback = "")
    {
        var text = Normalize(rawDetail);
        if (string.IsNullOrWhiteSpace(text))
        {
            return Normalize(fallback);
        }

        var segments = text
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();
        if (ContainsDiagnosticText(text) && segments.Length > 0)
        {
            text = segments.FirstOrDefault(static segment => !ContainsDiagnosticText(segment))
                ?? Normalize(fallback);
        }

        return NormalizeKnownSensorName(text);
    }

    public static string FanDetail(string? name, int index)
    {
        return SensorDetail(name, $"风扇{index}");
    }

    // 后端不再把容量拼进副标题：容量的换算和单位标签归前端统一负责，
    // 这里只给磁盘名字。完整容量在设备拓扑页按用户选择的进制显示。
    public static string DiskDetail(string name, ulong? sizeBytes)
    {
        _ = sizeBytes;
        return Normalize(name);
    }

    public static bool ContainsDiagnosticText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return DiagnosticMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeKnownSensorName(string text)
    {
        text = Normalize(text);
        return text switch
        {
            "CPU fan" => "CPU 风扇",
            "dGPU fan" => "dGPU 风扇",
            "Memory" => "内存",
            "No NVIDIA GPU telemetry" => "GPU",
            _ => text
        };
    }

    private static string Normalize(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : text.Trim();
    }
}
