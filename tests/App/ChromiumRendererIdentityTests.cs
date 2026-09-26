using ResourceManager.App.Infrastructure.GpuPlacement.External;
using ResourceManager.App.Domain.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class ChromiumRendererIdentityTests
{
    private const GpuGraphicsApi Confirmed = GpuGraphicsApi.D3D11;

    [Theory]
    [InlineData("application.exe")]
    [InlineData("msedge.exe")]
    [InlineData("electron.exe")]
    [InlineData("chrome.exe")]
    public void ConfirmedRendererDoesNotDependOnApplicationNameOrSeparateAngleDlls(string executable)
        => Assert.True(ChromiumGpuProcessIdentity.HasCompatibleRenderer(
            [executable, "--type=gpu-process", "--use-angle=d3d11"], Confirmed));

    [Theory]
    [InlineData("--use-angle=default")]
    [InlineData("--use-gl=angle")]
    [InlineData("--use-gl=default")]
    [InlineData("--enable-features=OtherFeature,VulkanFromANGLE")]
    public void CompatibleBackendArgumentsAreAccepted(string argument)
        => Assert.True(ChromiumGpuProcessIdentity.HasCompatibleRenderer(
            ["application.exe", "--type=gpu-process", argument], Confirmed));

    [Theory]
    [InlineData("--type=renderer")]
    [InlineData("--type=gpu-process")]
    [InlineData("--use-angle=swiftshader")]
    [InlineData("--use-angle=vulkan")]
    [InlineData("--use-angle=gl")]
    [InlineData("--use-gl=desktop")]
    [InlineData("--enable-features=Vulkan")]
    [InlineData("--enable-features=Other,Vulkan:enabled/true")]
    [InlineData("--enable-features=Other,Vulkan<Study")]
    [InlineData("--enable-features= Vulkan")]
    [InlineData("--enable-features=Other,Vulkan.Group")]
    public void ConflictingRoleOrDifferentRendererIsNotAdmitted(string argument)
        => Assert.False(ChromiumGpuProcessIdentity.HasCompatibleRenderer(
            ["application.exe", "--type=gpu-process", argument], Confirmed));

    [Fact]
    public void ApplicationNameAloneIsNotRendererEvidence()
        => Assert.False(ChromiumGpuProcessIdentity.HasCompatibleRenderer(["chrome.exe"], Confirmed));

    [Theory]
    [InlineData(null)]
    [InlineData(GpuGraphicsApi.D3D12)]
    [InlineData(GpuGraphicsApi.Vulkan)]
    [InlineData(GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12)]
    public void UnconfirmedOrDifferentApisCannotEnterChromiumD3D11Action(GpuGraphicsApi? api)
        => Assert.False(ChromiumGpuProcessIdentity.HasCompatibleRenderer(
            ["application.exe", "--type=gpu-process"], api));
}
