using System.Diagnostics;
using System.Text.Json;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class GpuGraphicsApiNativeTests(ITestOutputHelper output)
{
    [GpuApiNativeTheory]
    [InlineData("d3d9", GpuGraphicsApi.D3D9)]
    [InlineData("d3d11", GpuGraphicsApi.D3D11)]
    [InlineData("d3d12", GpuGraphicsApi.D3D12)]
    [InlineData("vulkan", GpuGraphicsApi.Vulkan)]
    public async Task ActualDeviceReportCanSeedHistoryButModuleCandidatesDoNotIdentifyIt(string api, GpuGraphicsApi expected)
    {
        var executable = Path.GetFullPath(Environment.GetEnvironmentVariable("RM_GPU_API_NATIVE_PROBE")!);
        using var root = new GpuGraphicsApiIdentificationTests.Fixture();
        using var child = new Process
        {
            StartInfo = new(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        child.StartInfo.ArgumentList.Add(api);
        Assert.True(child.Start());
        var creation = child.StartTime.ToUniversalTime().ToFileTimeUtc();
        var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(ready);
            using var json = JsonDocument.Parse(ready);
            Assert.True(json.RootElement.GetProperty("ready").GetBoolean());
            Assert.Equal(api, json.RootElement.GetProperty("api").GetString());
            Assert.Equal(child.Id, json.RootElement.GetProperty("pid").GetInt32());
            var input = new GpuPlacementProcessObservationRequest("software", "Software",
                [new("native-api-probe", executable, "x64", child.Id, ["real-device-process"])]);
            Assert.Equal(expected, new WindowsGpuGraphicsApiDetector().Detect(child.Id, executable));
            var store = new JsonGpuPlacementProcessHistoryStore(root);
            Assert.Null(Assert.Single((await store.ObserveAsync(input, default)).Processes).GraphicsApi);
            var process = Assert.Single((await store.SaveFirstGraphicsApiAsync("software", "Software",
                new(child.Id, checked((ulong)creation), "native-api-probe", executable), expected, default)).Processes);
            output.WriteLine($"pid={child.Id}; filetime={creation}; requested={api}; explicitProbeReportSeed={process.GraphicsApi}; passiveDetectionTest=false");
            Assert.Equal(expected, process.GraphicsApi);
            await child.StandardInput.WriteLineAsync("x");
            await child.StandardInput.FlushAsync();
            var remaining = await child.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, child.ExitCode);
            Assert.Empty(await stderr);
            Assert.Contains("\"cleanup\":true", remaining, StringComparison.Ordinal);
            var saved = new JsonGpuPlacementProcessHistoryStore(root);
            Assert.Equal(expected, Assert.Single((await saved.ObserveAsync(input, default)).Processes).GraphicsApi);
            output.WriteLine($"{ready}\n{remaining}exit=0; cachedAfterExit=true");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}

internal sealed class GpuApiNativeTheoryAttribute : TheoryAttribute
{
    public GpuApiNativeTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RM_GPU_API_NATIVE_PROBE")))
            Skip = "Requires the explicitly supplied owned native graphics API probe.";
    }
}
