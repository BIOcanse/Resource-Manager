using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.External;

namespace Resource_Manager_APP.Tests;

public sealed class GpuRendererConfirmationTests
{
    [Theory]
    [InlineData(11u, GpuGraphicsApi.D3D11)]
    [InlineData(12u, GpuGraphicsApi.D3D12)]
    [InlineData(4u, GpuGraphicsApi.Vulkan)]
    [InlineData(14u, GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12)]
    [InlineData(21u, GpuGraphicsApi.Vulkan | GpuGraphicsApi.D3D12)]
    [InlineData(0u, null)]
    [InlineData(1u, GpuGraphicsApi.OpenGL)]
    [InlineData(9u, GpuGraphicsApi.D3D9)]
    [InlineData(13u, GpuGraphicsApi.D3D9 | GpuGraphicsApi.D3D12)]
    [InlineData(16u, GpuGraphicsApi.OpenGL | GpuGraphicsApi.D3D12)]
    [InlineData(0xffffffffu, null)]
    public void KernelClientClassificationDoesNotInventApiPriority(uint hint, GpuGraphicsApi? expected)
        => Assert.Equal(expected, WindowsGpuRendererConfirmation.Classify(hint));

    [Fact]
    public void FailedQueryCannotConfirmAnApi()
        => Assert.Null(new WindowsGpuRendererConfirmation.AdapterClient(123, -1, 11).GraphicsApi);

    [Theory]
    [InlineData(0, 1u, false)]
    [InlineData(0, 9u, false)]
    [InlineData(0, 10u, false)]
    [InlineData(0, 12u, false)]
    [InlineData(0, 4u, false)]
    [InlineData(0, 0u, false)]
    [InlineData(0, 11u, true)]
    [InlineData(-1073741811, 0u, true)]
    public void OtherRegisteredClientsAreNotDiscardedBeforeD3D11Admission(int status, uint hint, bool expected)
        => Assert.Equal(expected, WindowsGpuRendererConfirmation.ConfirmsOnlyD3D11(
            [new(123, 0, 11), new(456, status, hint)]));

    [Fact]
    public void QueryMatchesWindowsX64Layout()
    {
        Assert.Equal(0x328, Marshal.SizeOf<WindowsGpuRendererConfirmation.Query>());
        Assert.Equal(288, Marshal.OffsetOf<WindowsGpuRendererConfirmation.Query>("ClientHint").ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<WindowsGpuRendererConfirmation.Query>("Process").ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<WindowsGpuRendererConfirmation.Query>("Adapter").ToInt32());
    }

    private sealed class NativeClientTheoryAttribute : TheoryAttribute
    {
        public NativeClientTheoryAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RM_RENDERER_CLIENT_PROBE")))
                Skip = "Requires the owned renderer client fixture; no user processes are changed.";
        }
    }

    private sealed class LiveClientFactAttribute : FactAttribute
    {
        public LiveClientFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RM_RENDERER_READONLY_PID")))
                Skip = "Requires an explicitly selected existing Chromium GPU process; read-only.";
        }
    }

    [LiveClientFact]
    public void ExistingChromiumGpuProcessCanBePreparedFromKernelClassification()
    {
        using var process = Process.GetProcessById(int.Parse(Environment.GetEnvironmentVariable("RM_RENDERER_READONLY_PID")!));
        var identity = new GpuPlacementProcessInstance(process.Id,
            checked((ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc()), process.ProcessName, process.MainModule!.FileName);
        var keys = Environment.GetEnvironmentVariable("RM_RENDERER_READONLY_ADAPTERS")!.Split(',').Select(ulong.Parse).ToArray();
        var rows = WindowsGpuRendererConfirmation.Read(identity, keys);
        Assert.Contains(rows, row => row.GraphicsApi == GpuGraphicsApi.D3D11);
        var apis = rows.Where(row => row.GraphicsApi is not null)
            .Aggregate((GpuGraphicsApi)0, (all, row) => all | row.GraphicsApi!.Value);
        var route = ChromiumGpuProcessIdentity.Read(identity, apis);
        Assert.NotNull(route);
        Assert.Equal(identity, route.GpuProcess);
        Assert.False(process.HasExited);
    }

    [NativeClientTheory]
    [InlineData("d3d11", GpuGraphicsApi.D3D11)]
    [InlineData("d3d12", GpuGraphicsApi.D3D12)]
    [InlineData("vulkan", GpuGraphicsApi.Vulkan)]
    public async Task ActualGraphicsContextsAreReadWithoutModuleGuessing(string api, GpuGraphicsApi expected)
    {
        var path = Environment.GetEnvironmentVariable("RM_RENDERER_CLIENT_PROBE")!;
        using var process = Process.Start(new ProcessStartInfo(path)
        {
            ArgumentList = { api, "--hold" }, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        })!;
        try
        {
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var line = await process.StandardOutput.ReadLineAsync(bound.Token);
            using var actual = JsonDocument.Parse(line!);
            var keys = actual.RootElement.GetProperty("adapters").EnumerateArray()
                .Select(row => row.GetProperty("luid").GetUInt64()).ToArray();
            var identity = new GpuPlacementProcessInstance(process.Id,
                checked((ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc()), "owned-client", path);
            var rows = WindowsGpuRendererConfirmation.Read(identity, keys);
            Assert.Contains(rows, row => row.GraphicsApi == expected);
            Assert.All(rows.Where(row => row.GraphicsApi is not null), row => Assert.Equal(expected, row.GraphicsApi));
            Assert.Empty(WindowsGpuRendererConfirmation.Read(identity with { ProcessStartKey = identity.ProcessStartKey + 1 }, keys));
            Assert.Empty(WindowsGpuRendererConfirmation.Read(identity with { ExecutablePath = path + ".wrong" }, keys));
            await process.StandardInput.WriteLineAsync();
            await process.WaitForExitAsync(bound.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await process.StandardError.ReadToEndAsync(bound.Token));
            Assert.Empty(WindowsGpuRendererConfirmation.Read(identity, keys));
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }
    }
}
