using ResourceManager.App.Application.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class GpuSpecializedTelemetryTests
{
    [Theory]
    [InlineData("NVIDIA GeForce GTX 1080", GpuArchitectureFamily.NvidiaCudaOnly)]
    [InlineData("NVIDIA GeForce GTX 1060", GpuArchitectureFamily.NvidiaCudaOnly)]
    [InlineData("NVIDIA GeForce RTX 5060 Laptop GPU", GpuArchitectureFamily.NvidiaRtCudaTensor)]
    [InlineData("NVIDIA A100-SXM4-40GB", GpuArchitectureFamily.NvidiaCudaTensor)]
    public void ClassifyArchitecture_HandlesNvidiaGenerations(string name, GpuArchitectureFamily expected)
    {
        Assert.Equal(expected, GpuSpecializedTelemetry.ClassifyArchitecture(GpuSpecializedTelemetry.NvidiaVendorId, name));
    }

    [Theory]
    [InlineData("AMD Radeon R9 390", GpuArchitectureFamily.AmdGcn)]
    [InlineData("AMD Radeon RX 580", GpuArchitectureFamily.AmdGcn)]
    [InlineData("AMD Radeon RX 7900 XTX", GpuArchitectureFamily.AmdRdna)]
    [InlineData("AMD Radeon 890M", GpuArchitectureFamily.AmdRdna)]
    [InlineData("AMD Instinct MI300X", GpuArchitectureFamily.AmdCdna)]
    public void ClassifyArchitecture_HandlesAmdGenerations(string name, GpuArchitectureFamily expected)
    {
        Assert.Equal(expected, GpuSpecializedTelemetry.ClassifyArchitecture(GpuSpecializedTelemetry.AmdVendorId, name));
    }

    [Theory]
    [InlineData("AMD Radeon(TM) Graphics", true, GpuArchitectureFamily.AmdGcn)]
    [InlineData("AMD Radeon Vega 8 Graphics", true, GpuArchitectureFamily.AmdGcn)]
    [InlineData("AMD Radeon 680M", true, GpuArchitectureFamily.AmdRdna)]
    [InlineData("AMD Radeon 780M", true, GpuArchitectureFamily.AmdRdna)]
    [InlineData("AMD Radeon 890M", true, GpuArchitectureFamily.AmdRdna)]
    [InlineData("AMD Radeon RX 580", false, GpuArchitectureFamily.AmdGcn)]
    [InlineData("AMD Radeon RX 7900 XTX", false, GpuArchitectureFamily.AmdRdna)]
    [InlineData("AMD Instinct MI300X", false, GpuArchitectureFamily.AmdCdna)]
    public void ClassifyArchitecture_UsesAmdIntegratedAndDiscreteRules(
        string name,
        bool isIntegrated,
        GpuArchitectureFamily expected)
    {
        Assert.Equal(
            expected,
            GpuSpecializedTelemetry.ClassifyArchitecture(
                GpuSpecializedTelemetry.AmdVendorId,
                name,
                isIntegrated));
    }

    [Fact]
    public void GetSupportedUsageKinds_UsesNvidiaThreeSlotsAndPascalCudaOnly()
    {
        Assert.Equal(
            [GpuSpecializedUsageKind.NvidiaRt, GpuSpecializedUsageKind.NvidiaCuda, GpuSpecializedUsageKind.NvidiaTensor],
            GpuSpecializedTelemetry.GetSupportedUsageKinds(GpuArchitectureFamily.NvidiaRtCudaTensor));

        Assert.Equal(
            [GpuSpecializedUsageKind.NvidiaCuda],
            GpuSpecializedTelemetry.GetSupportedUsageKinds(GpuArchitectureFamily.NvidiaCudaOnly));
    }

    [Theory]
    [InlineData("engtype_RayTracing", true, false)]
    [InlineData("engtype_Compute_0", false, true)]
    [InlineData("engtype_CUDA", false, true)]
    [InlineData("engtype_3D", false, false)]
    public void EngineTypeClassifier_MapsRtAndCuda(string engineType, bool isRt, bool isCuda)
    {
        Assert.Equal(isRt, GpuSpecializedTelemetry.IsRtEngineType(engineType));
        Assert.Equal(isCuda, GpuSpecializedTelemetry.IsCudaEngineType(engineType));
    }
}
