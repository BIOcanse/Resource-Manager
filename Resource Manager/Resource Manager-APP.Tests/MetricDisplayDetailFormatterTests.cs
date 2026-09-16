using ResourceManager.App.Application.Metrics;

namespace Resource_Manager_APP.Tests;

public sealed class MetricDisplayDetailFormatterTests
{
    [Theory]
    [InlineData(
        "CPU fan / Notebook fan providers / Active / Hardware monitor WMI: Unavailable; Notebook OEM Fan Provider: Active",
        "CPU 风扇")]
    [InlineData(
        "NVIDIA GeForce RTX 5060 Laptop GPU / NVIDIA NVML + Mechrevo UWACPI / Partial / NVIDIA NVML: 电压/电流/风扇不可用 或可靠 field provider，当前不推导。 Platform/OEM fallback: Mechrevo UWACPI / dGPU fan / official ACPI EC read-only provider",
        "NVIDIA GeForce RTX 5060 Laptop GPU")]
    public void SensorDetail_RemovesProviderDiagnosticsFromUserFacingText(string rawDetail, string expected)
    {
        var detail = MetricDisplayDetailFormatter.SensorDetail(rawDetail);

        Assert.Equal(expected, detail);
        Assert.False(MetricDisplayDetailFormatter.ContainsDiagnosticText(detail));
    }

    [Fact]
    public void DiskDetail_DoesNotExposeDeviceIdsOrProviderNames()
    {
        var detail = MetricDisplayDetailFormatter.DiskDetail("Samsung SSD", 1024UL * 1024 * 1024 * 512);

        // 容量不再拼进副标题：换算与单位标签归前端按用户选择的进制统一给出。
        Assert.Equal("Samsung SSD", detail);
        Assert.False(MetricDisplayDetailFormatter.ContainsDiagnosticText(detail));
    }
}
