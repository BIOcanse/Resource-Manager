using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed partial class GpuPlacementInjectorNativeTests(ITestOutputHelper output)
{
    [GpuApiNativeTheory]
    [InlineData("d3d9", GpuGraphicsApi.D3D9)]
    [InlineData("d3d11", GpuGraphicsApi.D3D11)]
    [InlineData("d3d12", GpuGraphicsApi.D3D12)]
    public async Task SavedDirect3DRouteConfiguresTheCurrentProcessThroughTheActionEntry(
        string api, GpuGraphicsApi expected)
    {
        using var files = new D3d11ProxyShimRuntimeTests.PolicyFiles();
        await using var child = await OwnedProbe.StartAsync(api, output);
        var software = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("actual-software", "Software") with
        {
            EnabledMode = GpuPlacementPolicyModes.Auto, SchedulingMode = GpuPlacementSchedulingModes.Precise,
            RuntimeSchedulingMode = GpuPlacementRuntimeSchedulingModes.Precise, RuntimeHotSwitchEnabled = true,
            AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim]
        };
        var initial = new JsonGpuPlacementProcessHistoryStore(files);
        // Explicit known-fixture input: this tests saved-route execution, not first-use API detection.
        var detected = await initial.SaveFirstGraphicsApiAsync(software.SoftwareId, "Software", child.Identity, expected, default);
        Assert.Equal(expected, Assert.Single(detected.Processes).GraphicsApi);
        var readOnly = new RunningGpuPlacementIdentityTests.HistoryReader(new JsonGpuPlacementProcessHistoryStore(files));
        var injector = new WindowsGpuPlacementInjector(NullLogger<WindowsGpuPlacementInjector>.Instance);
        var service = new WindowsRunningGpuPlacementActionService(NullLogger<WindowsRunningGpuPlacementActionService>.Instance,
            files.Runtime, injector, readOnly, new GpuGraphicsApiIdentificationTests.Plans(software), new());
        var preparation = await service.PrepareAsync(new("instance-target", software.SoftwareId, "Software",
            [child.Identity, child.Identity], "background", 0x8123456789abcdefUL,
            GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover), default);
        Assert.NotNull(preparation.Plan);
        Assert.Equal(expected, preparation.Plan.GraphicsApis[child.Identity.ProcessId]);
        Assert.Single(preparation.Plan.GraphicsApis);
        Assert.Null(files.Runtime.ReadPolicy("instance-target"));
        Assert.False(child.HasProvider());
        var windows = new RunningGpuPlacementExecution(16, _ => throw new InvalidOperationException("No eligible fixture window is expected."),
            GpuRemoteCallTestOwner.ExecuteCompletedAsync, GpuRemoteCallTestOwner.RejectPreparation);
        var unpublished = await service.TryApplyAsync(preparation.Plan, windows, default);
        Assert.Equal("skipped", unpublished.Status);
        Assert.False(child.HasProvider());
        var policy = files.Runtime.CapturePolicyRecord("instance-target", preparation.Plan.PolicyValue)!;
        Assert.True(files.Runtime.TryApplyPolicyRecord(policy));
        var result = await service.TryApplyAsync(preparation.Plan, windows, default);
        Assert.Equal(software.SoftwareId, readOnly.ReadSoftwareId);
        Assert.True(child.HasProvider());
        Assert.Equal("prepared", result.Status);
        Assert.Empty(result.Records);
        var facts = Assert.Single(result.Processes);
        Assert.Equal(child.Identity, facts.Identity);
        Assert.True(facts.Configured);
        Assert.True(facts.Before!.Success);
        Assert.Equal(facts.Before.Snapshot, facts.After!.Snapshot);
        Assert.False(result.Applied);
        Assert.False(result.Triggered);
        Assert.Contains("targetLuid=0x81234567_0x89abcdef", File.ReadAllText(files.Runtime.GetPolicyPath("instance-target")));
        await child.StopAsync();
        Assert.True(files.Runtime.RestorePolicyRecord(policy).CanRemoveReceipt);
        Assert.Null(files.Runtime.ReadPolicy("instance-target"));
        output.WriteLine("savedApiActionConfigured=true; purePreparation=true; unpublishedRejected=true; duplicateInput=true; configuredWithoutWindowRetained=true; beforeAfterRead=true; policyRestored=true; devicePlacementVerified=false");
    }

    [GpuApiNativeTheory]
    [InlineData("d3d9")]
    [InlineData("d3d11")]
    [InlineData("d3d12")]
    public async Task ExactNativeInstanceRejectsWrongBirthAndImageBeforeConfiguring(string api)
    {
        using var files = new D3d11ProxyShimRuntimeTests.PolicyFiles();
        await using var child = await OwnedProbe.StartAsync(api, output);
        var injector = new WindowsGpuPlacementInjector(NullLogger<WindowsGpuPlacementInjector>.Instance);
        var policy = files.Runtime.PrepareExact("owned-target", 0x8123456789abcdefUL);
        Assert.True(policy.PolicyPrepared, policy.ErrorMessage);
        var bytes = File.ReadAllBytes(policy.PolicyPath!);
        Assert.False(child.HasProvider());
        foreach (var wrong in new[]
        {
            child.Identity with { ProcessStartKey = child.Identity.ProcessStartKey - 1 },
            child.Identity with { ProcessStartKey = child.Identity.ProcessStartKey + 1 }
        })
            Assert.Equal("process-identity-mismatch", Configure(injector, wrong, policy.PolicyPath!).Status);
        Assert.Equal("process-image-mismatch", Configure(injector,
            child.Identity with { ExecutablePath = Path.Combine(files.Root, "other.exe") }, policy.PolicyPath!).Status);
        Assert.Equal("process-identity-invalid", Configure(injector,
            child.Identity with { ProcessStartKey = 0 }, policy.PolicyPath!).Status);
        Assert.Equal("process-identity-invalid", Configure(injector,
            child.Identity with { ExecutablePath = "relative.exe" }, policy.PolicyPath!).Status);
        Assert.False(child.HasProvider());
        Assert.Equal(bytes, File.ReadAllBytes(policy.PolicyPath!));

        using var owner = await injector.OpenAndConfigureAsync(child.Identity, Enum.Parse<GpuGraphicsApi>(api, true), policy.PolicyPath!, GpuRemoteCallTestOwner.ExecuteCompletedAsync, default);
        var configured = owner.Result;
        output.WriteLine(JsonSerializer.Serialize(configured));
        Assert.True(configured.Success, configured.Message);
        Assert.False(configured.AlreadyLoaded);
        Assert.Equal("injected-and-configured", configured.Status);
        var required = WindowsGpuPlacementInjector.RequiredProviderStatus(Enum.Parse<GpuGraphicsApi>(api, true));
        Assert.Equal(required, configured.ProviderStatus & required);
        Assert.True(child.HasProvider());
        Assert.True(owner.OwnsWindow(child.Window));
        Assert.False(owner.OwnsWindow(IntPtr.Zero));
        Assert.Equal(bytes, File.ReadAllBytes(policy.PolicyPath!));
        using var nextOwner = await injector.OpenAndConfigureAsync(child.Identity, Enum.Parse<GpuGraphicsApi>(api, true), policy.PolicyPath!, GpuRemoteCallTestOwner.ExecuteCompletedAsync, default);
        var repeated = nextOwner.Result;
        Assert.True(repeated.Success, repeated.Message);
        Assert.True(repeated.AlreadyLoaded);
        Assert.Equal("configured", repeated.Status);
        nextOwner.Dispose();
        Assert.False(nextOwner.OwnsWindow(child.Window));
        Assert.True(owner.OwnsWindow(child.Window));
        await using (var other = await OwnedProbe.StartAsync(api, output))
        {
            Assert.False(owner.OwnsWindow(other.Window));
            await other.StopAsync();
        }
        Assert.Equal("process-identity-mismatch", Configure(injector,
            child.Identity with { ProcessStartKey = child.Identity.ProcessStartKey + 1 }, policy.PolicyPath!).Status);
        await child.StopAsync();
        Assert.False(owner.OwnsWindow(child.Window));
        Assert.False(Configure(injector, child.Identity, policy.PolicyPath!).Success);
        output.WriteLine("wrongIdentityProviderAbsent=true; exactConfiguration=true; devicePlacementVerified=false; ownedWindowLifetime=true");
    }

    [GpuApiNativeTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureSuppressionDoesNotUseAStaleRequestOrAnotherProviderPath(bool foreignProvider)
    {
        using var files = new D3d11ProxyShimRuntimeTests.PolicyFiles();
        var injector = new WindowsGpuPlacementInjector(NullLogger<WindowsGpuPlacementInjector>.Instance);
        string? foreignPath = null;
        if (foreignProvider)
        {
            foreignPath = Path.Combine(files.Root, WindowsGpuPlacementInjector.RuntimeProviderFileName);
            File.Copy(injector.RuntimeProviderPath, foreignPath);
        }
        await using var child = await OwnedProbe.StartAsync("d3d11", output, foreignPath);
        var policyPath = foreignProvider
            ? files.Runtime.PrepareExact("owned-target", 1).PolicyPath!
            : Path.Combine(files.Root, "absent-policy.txt");
        var status = foreignProvider ? "provider-path-mismatch" : "policy-missing";
        Assert.Equal(status, Configure(injector, child.Identity, policyPath).Status);
        Assert.Equal("process-identity-mismatch", Configure(injector,
            child.Identity with { ProcessStartKey = child.Identity.ProcessStartKey + 1 }, policyPath).Status);
        var suppressed = Configure(injector, child.Identity, policyPath);
        Assert.Equal("retry-suppressed-until-process-restart", suppressed.Status);
        Assert.Contains(status, suppressed.Message);
        Assert.Equal(foreignProvider, child.HasProvider());
        await child.StopAsync();
        output.WriteLine($"rejected={status}; staleRequestDidNotClearFailure=true");
    }

    private static RuntimeGpuProviderInjectionResult Configure(
        WindowsGpuPlacementInjector injector, GpuPlacementProcessInstance target, string policyPath)
    {
        using var owner = injector.OpenAndConfigureAsync(target, GpuGraphicsApi.D3D11, policyPath, GpuRemoteCallTestOwner.ExecuteCompletedAsync, default).GetAwaiter().GetResult();
        return owner.Result;
    }

    [GpuApiNativeTheory]
    [InlineData("d3d9")]
    [InlineData("d3d11")]
    [InlineData("d3d12")]
    public async Task RetainedOwnerReadsTheActualReturnedDeviceWithoutCreatingOne(string api)
    {
        using var files = new D3d11ProxyShimRuntimeTests.PolicyFiles();
        await using var child = await OwnedProbe.StartAsync(api, output);
        var injector = new WindowsGpuPlacementInjector(NullLogger<WindowsGpuPlacementInjector>.Instance);
        var policy = files.Runtime.PrepareExact("observed-target", child.AdapterLuid);
        Assert.True(policy.PolicyPrepared, policy.ErrorMessage);
        using var owner = await injector.OpenAndConfigureAsync(child.Identity, Enum.Parse<GpuGraphicsApi>(api, true), policy.PolicyPath!, GpuRemoteCallTestOwner.ExecuteCompletedAsync, default);
        Assert.True(owner.Result.Success, owner.Result.Message);

        var first = await owner.ReadDeviceObservationsAsync(default);
        Assert.True(first.Success, first.Status);
        var empty = new GpuDeviceObservation(0, 0, GpuDeviceAdapterIdentityKind.NoDevice);
        Assert.Equal(new GpuDeviceObservationSnapshot(empty, empty, empty, empty, empty), first.Snapshot);
        Assert.Equal(first.Snapshot, (await owner.ReadDeviceObservationsAsync(default)).Snapshot);

        var actualLuid = await child.CreateDeviceAsync();
        var read = await owner.ReadDeviceObservationsAsync(default);
        Assert.True(read.Success, read.Status);
        var snapshot = Assert.IsType<GpuDeviceObservationSnapshot>(read.Snapshot);
        var returned = api switch { "d3d9" => snapshot.D3D9, "d3d11" => snapshot.D3D11, _ => snapshot.D3D12 };
        Assert.True(returned.ReturnedDeviceCount > 0);
        Assert.Equal(GpuDeviceAdapterIdentityKind.Adapter, returned.Identity);
        Assert.Equal(actualLuid, returned.AdapterLuid);
        Assert.Equal(child.AdapterLuid, actualLuid);
        if (api != "d3d11") Assert.Equal(empty, snapshot.D3D11);
        if (api != "d3d12") Assert.Equal(empty, snapshot.D3D12);
        Assert.Equal(empty, snapshot.Vulkan);
        Assert.Equal(empty, snapshot.OpenGL);
        if (api != "d3d9") Assert.Equal(empty, snapshot.D3D9);
        Assert.Equal(snapshot, (await owner.ReadDeviceObservationsAsync(default)).Snapshot);
        Assert.True(owner.Result.Success);
        await child.StopAsync();
        Assert.Equal("process-unavailable", (await owner.ReadDeviceObservationsAsync(default)).Status);
        owner.Dispose();
        Assert.Null((await owner.ReadDeviceObservationsAsync(default)).Snapshot);
        output.WriteLine($"remoteObservationRead=true; api={api}; actualLuid={actualLuid:x16}; returnedCount={returned.ReturnedDeviceCount}; readOnly=true; exitedOwnerRejected=true");
    }

    internal sealed class OwnedProbe(Process process, Task<string> stderr, GpuPlacementProcessInstance identity,
        ITestOutputHelper output) : IAsyncDisposable
    {
        public GpuPlacementProcessInstance Identity => identity;
        public IntPtr Window { get; private set; }
        public ulong AdapterLuid { get; private set; }
        public ulong AlternateAdapterLuid { get; private set; }
        public string Api { get; private set; } = string.Empty;
        private bool idleD3d11Candidate;
        private int nativeChecks;

        public static async Task<OwnedProbe> StartAsync(string api, ITestOutputHelper output, string? preload = null, string? executablePath = null,
            bool idleD3d11Candidate = false)
        {
            var executable = Path.GetFullPath(executablePath ?? Environment.GetEnvironmentVariable("RM_GPU_API_NATIVE_PROBE")!);
            var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            process.StartInfo.ArgumentList.Add(api);
            if (preload is not null) process.StartInfo.ArgumentList.Add(preload);
            if (idleD3d11Candidate)
            {
                Assert.Equal("vulkan", api);
                Assert.NotNull(preload);
                process.StartInfo.ArgumentList.Add("idle-d3d11");
            }
            OwnedProbe? owned = null;
            var started = false;
            try
            {
                started = process.Start();
                Assert.True(started);
                var instance = new GpuPlacementProcessInstance(process.Id, checked((ulong)process.StartTime.ToFileTimeUtc()),
                    Path.GetFileNameWithoutExtension(executable), executable);
                owned = new OwnedProbe(process, process.StandardError.ReadToEndAsync(), instance, output)
                    { Api = api, idleD3d11Candidate = idleD3d11Candidate };
                output.WriteLine($"ownedPid={instance.ProcessId}; filetime={instance.ProcessStartKey}; api={api}");
                using var ready = await owned.ReadMessageAsync("ready");
                Assert.True(ready.RootElement.GetProperty("ready").GetBoolean());
                Assert.Equal(process.Id, ready.RootElement.GetProperty("pid").GetInt32());
                owned.Window = new IntPtr(ready.RootElement.GetProperty("hwnd").GetInt64());
                owned.AdapterLuid = ready.RootElement.GetProperty("adapterLuid").GetUInt64();
                if (api == "vulkan")
                {
                    owned.AlternateAdapterLuid = ready.RootElement.GetProperty("alternateLuid").GetUInt64();
                    Assert.Equal(1024, ready.RootElement.GetProperty("verifiedWords").GetInt32());
                }
                Assert.NotEqual(IntPtr.Zero, owned.Window);
                return owned;
            }
            catch
            {
                if (owned is not null) await owned.DisposeAsync();
                else
                {
                    try
                    {
                        if (started && !process.HasExited)
                        {
                            output.WriteLine("forcedFailureCleanup=true; phase=identity-capture");
                            process.Kill();
                            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        }
                    }
                    finally { process.Dispose(); }
                }
                throw;
            }
        }

        public bool HasProvider()
        {
            process.Refresh();
            return process.Modules.Cast<ProcessModule>().Any(module => module.ModuleName.Equals(
                WindowsGpuPlacementInjector.RuntimeProviderFileName, StringComparison.OrdinalIgnoreCase));
        }

        public async Task StopAsync()
        {
            var handle = process.SafeHandle;
            await process.StandardInput.WriteLineAsync("x");
            await process.StandardInput.FlushAsync();
            var rest = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(15));
            output.WriteLine(rest);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var initialWait = WaitForSingleObject(handle, 0);
            var finalWait = WaitForSingleObject(handle, 15_000);
            output.WriteLine($"ownedNativeWaitBefore={initialWait}; ownedNativeWaitFinal={finalWait}");
            Assert.Equal(0U, finalWait);
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("\"cleanup\":true", rest);
            if (Api == "vulkan")
            {
                var lines = rest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                string[] labels = ["parent-requested-normal-exit", "owned-window-destroyed", "caller-loader-reference-released"];
                Assert.Equal(idleD3d11Candidate ? 5 : 4, lines.Length);
                for (var i = 0; i < labels.Length; ++i)
                {
                    using var assertion = JsonDocument.Parse(lines[i]);
                    Assert.Equal(labels[i], assertion.RootElement.GetProperty("check").GetString());
                    Assert.True(assertion.RootElement.GetProperty("passed").GetBoolean());
                    nativeChecks++;
                }
                if (idleD3d11Candidate)
                {
                    using var assertion = JsonDocument.Parse(lines[3]);
                    Assert.Equal("caller-idle-d3d11-reference-released", assertion.RootElement.GetProperty("check").GetString());
                    Assert.True(assertion.RootElement.GetProperty("passed").GetBoolean());
                    nativeChecks++;
                }
                using var terminal = JsonDocument.Parse(lines[^1]);
                Assert.True(terminal.RootElement.GetProperty("cleanup").GetBoolean());
                Assert.Equal(nativeChecks, terminal.RootElement.GetProperty("checks").GetInt32());
            }
            Assert.Empty(await stderr);
            output.WriteLine("ownedExit=0; cleanup=true");
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

        public async Task<ulong> CreateDeviceAsync()
        {
            await process.StandardInput.WriteLineAsync("c");
            await process.StandardInput.FlushAsync();
            using var result = await ReadMessageAsync("created");
            Assert.True(result.RootElement.GetProperty("created").GetBoolean());
            if (Api == "d3d9") Assert.Equal(256, result.RootElement.GetProperty("verifiedPixels").GetInt32());
            if (Api == "vulkan")
            {
                Assert.Equal(1024, result.RootElement.GetProperty("verifiedWords").GetInt32());
                Assert.Equal(1024, result.RootElement.GetProperty("oldDeviceWords").GetInt32());
            }
            output.WriteLine($"nativeDevice={result.RootElement.GetRawText()}");
            return result.RootElement.GetProperty("actualLuid").GetUInt64();
        }

        private async Task<JsonDocument> ReadMessageAsync(string expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            for (var i = 0; i < 256; ++i)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                var json = JsonDocument.Parse(Assert.IsType<string>(line));
                output.WriteLine(line);
                if (json.RootElement.TryGetProperty(expected, out _)) return json;
                using (json)
                {
                    Assert.Equal("vulkan", Api);
                    if (json.RootElement.TryGetProperty("check", out _))
                    {
                        Assert.True(json.RootElement.GetProperty("passed").GetBoolean());
                        nativeChecks++;
                    }
                    else if (json.RootElement.TryGetProperty("gpuReadback", out var readback))
                    {
                        Assert.True(readback.GetBoolean());
                        Assert.Equal(1024, json.RootElement.GetProperty("wordCount").GetInt32());
                    }
                    else
                    {
                        Assert.True(json.RootElement.GetProperty("retainedDeviceReadback").GetBoolean());
                        Assert.Equal(1024, json.RootElement.GetProperty("words").GetInt32());
                    }
                }
            }
            throw new InvalidOperationException("Owned probe exceeded its bounded response record count.");
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                output.WriteLine("forcedFailureCleanup=true");
                process.Kill();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            process.Dispose();
        }
    }
}
