using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using ResourceManager.App.Infrastructure.Monitoring;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class GpuRuntimeRecreationNativeTests(ITestOutputHelper output)
{
    private sealed class RuntimeRecreationTheoryAttribute : TheoryAttribute
    {
        public RuntimeRecreationTheoryAttribute(string probeVariable = "RM_GPU_RECREATE_PROBE")
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(Environment.GetEnvironmentVariable(probeVariable))
                || !File.Exists(Environment.GetEnvironmentVariable("RM_GPU_WINDOW_ACTION"))
                || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RM_GPU_RECREATE_RESULTS")))
                Skip = "Requires explicitly built continuous renderer, owned window worker and result directory.";
        }
    }

    [RuntimeRecreationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductTriggersExistingRendererRecoveryWithoutARecreateCommand(bool resize)
        => await Run(resize, 0);

    [RuntimeRecreationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolateCreationHooksFromPassivePresentationHooks(bool observe)
        => await Run(false, observe ? 2 : 1);

    [RuntimeRecreationTheory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task IsolateDefaultPolicyAndNoProviderControls(int control)
        => await Run(false, control);

    [RuntimeRecreationTheory]
    [InlineData(5)]
    public async Task CancelledExpiredAndMismatchedRequestsNeverRecreate(int control)
        => await Run(false, control);

    [RuntimeRecreationTheory]
    [InlineData(6)]
    public async Task SavedCombinedD3dHistoryUsesTheSameRuntimeTrigger(int control)
        => await Run(false, control);

    [RuntimeRecreationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlipModelUsesPresent1AndResizeBuffers(bool resize)
        => await Run(resize, 0, true);

    [RuntimeRecreationTheory("RM_GPU_RECREATE_D3D12_PROBE")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task D3d12QueueRecoversAndContinuesRendering(bool resize, bool extended)
        => await Run(resize, 0, extended, GpuGraphicsApi.D3D12);

    private async Task Run(bool resize, int control, bool extended = false, GpuGraphicsApi api = GpuGraphicsApi.D3D11)
    {
        var product = control is 0 or 6;
        var registeredApi = control == 6 ? GpuGraphicsApi.D3D11 | GpuGraphicsApi.D3D12 : api;
        var root = Path.Combine(Environment.GetEnvironmentVariable("RM_GPU_RECREATE_RESULTS")!,
            $"control{control}-" + (resize ? "resize-" : "present-") + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executable = Path.GetFullPath(Environment.GetEnvironmentVariable(api == GpuGraphicsApi.D3D12
            ? "RM_GPU_RECREATE_D3D12_PROBE" : "RM_GPU_RECREATE_PROBE")!);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        string[] arguments = ["--duration-ms", "12000", "--no-activate", "--ready-stdout",
            "--frame-interval-ms", "16", "--copy-passes", "2", "--copy-texture-size", "256",
            "--output", Path.Combine(root, "renderer.json")];
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        if (extended) start.ArgumentList.Add("--extended-swap-chain");
        if (control is > 0 and < 5)
            foreach (var arg in new[] { "--allow-unsafe-migration-probe", "--auto-trigger-ms", "2500",
                "--auto-trigger-method", "presentDeviceRemoved", "--explicit-low-power-adapter-on-recreate" })
                start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        Assert.True(GetProcessTimes(child.Handle, out var birth, out _, out _, out _));
        var identity = new GpuPlacementProcessInstance(child.Id, birth, Path.GetFileNameWithoutExtension(executable), executable);
        output.WriteLine($"ownedPid={child.Id}; filetime={birth}; path={root}");
        await File.WriteAllTextAsync(Path.Combine(root, "launcher.json"), JsonSerializer.Serialize(new { identity, arguments = start.ArgumentList.ToArray(), control }));
        var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            var line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Assert.NotNull(line);
            await File.WriteAllTextAsync(Path.Combine(root, "ready.json"), line);
            using var ready = JsonDocument.Parse(line);
            var originalLuid = ParseLuid(ready.RootElement.GetProperty("initialLuid").GetString()!);
            var window = new nint(ready.RootElement.GetProperty("hwnd").GetInt64());
            Assert.NotEqual(0UL, originalLuid);
            Assert.NotEqual(window, GetForegroundWindow());
            var adapter = new WindowsGpuAdapterOrderReader().ReadInventory().Adapters
                .First(item => !item.IsSoftware && Pack(item.Luid.HighPart, item.Luid.LowPart) != originalLuid);
            var target = Pack(adapter.Luid.HighPart, adapter.Luid.LowPart);
            using var files = new D3d11ProxyShimRuntimeTests.PolicyFiles();
            var software = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("renderer", "Renderer") with
            {
                EnabledMode = GpuPlacementPolicyModes.Auto, SchedulingMode = GpuPlacementSchedulingModes.Precise,
                RuntimeSchedulingMode = GpuPlacementRuntimeSchedulingModes.Precise, RuntimeHotSwitchEnabled = true,
                AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim]
            };
            var store = new JsonGpuPlacementProcessHistoryStore(files);
            var injector = new WindowsGpuPlacementInjector(NullLogger<WindowsGpuPlacementInjector>.Instance);
            var service = new WindowsRunningGpuPlacementActionService(NullLogger<WindowsRunningGpuPlacementActionService>.Instance,
                files.Runtime, injector, store, new GpuGraphicsApiIdentificationTests.Plans(software), new());
            var calls = new List<object>();
            async Task<GpuRemoteCallSnapshot> Execute(GpuRemoteCallExecution call, CancellationToken token)
            {
                var request = call.Request;
                var result = await GpuRemoteCallTestOwner.ExecuteCompletedAsync(call, token);
                calls.Add(new { request, result });
                await File.WriteAllTextAsync(Path.Combine(root, "remote-calls.json"), JsonSerializer.Serialize(calls));
                return result;
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var request = new RunningGpuPlacementActionRequest("live-renderer", software.SoftwareId, "Renderer",
                [identity], "background", target, resize ? GpuPlacementRuntimeSwitchMethods.WindowRerender : GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover);
            RunningGpuPlacementPreparation preparation;
            if (control is 1 or 3 or 4 or 5 or 6)
            {
                // Explicit control input only; this branch does not claim API recognition.
                await store.SaveFirstGraphicsApiAsync(software.SoftwareId, "Renderer", identity, registeredApi, default);
                preparation = await service.PrepareAsync(request, deadline.Token);
            }
            else preparation = await service.PrepareWithFirstApiObservationAsync(request,
                new RunningGpuApiObservationExecution(500, Execute, (_, duration, token) => Task.Delay(duration, token), CancellationToken.None), deadline.Token);
            Assert.NotNull(preparation.Plan);
            Assert.Equal(registeredApi, preparation.Plan.GraphicsApis[child.Id]);
            Assert.Equal(registeredApi, Assert.Single((await store.GetSoftwareHistoryAsync(software.SoftwareId, "Renderer", default)).Processes).GraphicsApi);
            var policy = files.Runtime.CapturePolicyRecord(request.TargetId, preparation.Plan.PolicyValue)!;
            Assert.True(files.Runtime.TryApplyPolicyRecord(policy));
            var windowCalls = 0;
            var execution = new RunningGpuPlacementExecution(1, async action =>
            {
                Assert.True(resize);
                ++windowCalls;
                await using var executor = new WindowsGpuWindowActionExecutor(Environment.GetEnvironmentVariable("RM_GPU_WINDOW_ACTION")!,
                    new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), 4096, 4096), deadline.Token);
                var result = await executor.RunAsync(action, prepared => File.WriteAllTextAsync(Path.Combine(root, "window-prepared.json"), JsonSerializer.Serialize(prepared)));
                await File.WriteAllTextAsync(Path.Combine(root, "window-result.json"), JsonSerializer.Serialize(new { executor.Worker, result }));
                Assert.Equal(GpuWindowActionOutcome.WindowRestored, result.Outcome);
                Assert.True(result.Cleanup.Complete);
                Assert.False(result.Cleanup.TerminationRequested);
                Assert.Equal(0u, result.Cleanup.ActiveProcessCount);
                return new(result.Outcome, null, true);
            }, Execute, GpuRemoteCallTestOwner.RejectPreparation) { RecreationDeadlineMilliseconds = checked((ulong)Environment.TickCount64 + 2000) };
            RunningGpuPlacementActionResult applied;
            if (!product)
            {
                if (control == 3) await File.WriteAllTextAsync(files.Runtime.GetPolicyPath(request.TargetId), "mode=default\n");
                if (control != 4)
                {
                    using var owner = await injector.OpenAndConfigureAsync(identity, GpuGraphicsApi.D3D11,
                        files.Runtime.GetPolicyPath(request.TargetId), Execute, deadline.Token);
                    Assert.True(owner.Result.Success);
                    if (control == 5) await AssertRequestLifecycleAsync(owner, target, deadline.Token);
                }
                applied = new([], "Creation-hook isolation control; no native recreation request.", RunningGpuPlacementActionStatuses.Prepared);
            }
            else applied = await service.TryApplyAsync(preparation.Plan, execution, deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(root, "action.json"), JsonSerializer.Serialize(applied));
            Assert.Equal(resize ? 1 : 0, windowCalls);
            Assert.Equal(product, applied.Triggered);
            Assert.False(applied.Applied);
            if (product) Assert.Equal(originalLuid, Assert.Single(applied.Processes).Recreation!.SourceAdapterKey);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, child.ExitCode);
            Assert.Equal(string.Empty, await stderr);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "renderer.json")));
            var fact = report.RootElement;
            output.WriteLine(fact.GetRawText());
            Assert.True(fact.GetProperty("success").GetBoolean());
            Assert.Equal(control == 5 ? originalLuid : target, ParseLuid(fact.GetProperty("finalLuid").GetString()!));
            Assert.Equal(control == 5 ? 0 : 1, fact.GetProperty("recreateCount").GetInt32());
            Assert.Equal(!resize && product ? 1 : 0, fact.GetProperty("actualPresentDeviceRemovedCount").GetInt32());
            Assert.Equal(resize ? 1 : 0, fact.GetProperty("actualResizeBuffersDeviceRemovedCount").GetInt32());
            Assert.Equal(control is > 0 and < 5 ? 1 : 0, fact.GetProperty("simulatedPresentDeviceRemovedCount").GetInt32());
            Assert.Equal(0, fact.GetProperty("simulatedResizeBuffersDeviceRemovedCount").GetInt32());
            Assert.Equal(0u, fact.GetProperty("lastPresentResult").GetUInt32());
            if (extended) Assert.True(fact.GetProperty("extendedSwapChain").GetBoolean());
            if (api == GpuGraphicsApi.D3D12)
            {
                Assert.True(fact.GetProperty("observationRead").GetBoolean());
                Assert.Equal(0UL, fact.GetProperty("observedD3D11Devices").GetUInt64());
                Assert.Equal(1UL, fact.GetProperty("observedD3D12Devices").GetUInt64());
            }
            Assert.True(fact.GetProperty(control == 5 ? "successfulPresents" : "presentsAfterRecovery").GetUInt64() > 10);
            Assert.Equal(control is > 0 and < 5, fact.GetProperty("explicitLowPowerAdapterOnRecreate").GetBoolean());
            if (control != 3) Assert.True(files.Runtime.RestorePolicyRecord(policy).CanRemoveReceipt);
            output.WriteLine($"normalExit=0; sameProcess=true; control={control}; productSignal={product}; source={originalLuid}; target={target}");
        }
        finally
        {
            if (!child.HasExited)
            {
                // The renderer has its own twelve-second lifetime, including on assertion failure.
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(16)); }
                catch (TimeoutException) { output.WriteLine("forcedFailureCleanup=true"); child.Kill(); await child.WaitForExitAsync(); }
            }
            await File.WriteAllTextAsync(Path.Combine(root, "stderr.log"), await stderr);
        }
    }

    private static async Task AssertRequestLifecycleAsync(WindowsGpuPlacementInjector.ProviderProcess owner,
        ulong target, CancellationToken token)
    {
        byte[] Request(GpuGraphicsApi api, bool resize, ulong deadline)
            => WindowsGpuPlacementInjector.CreateRecreationRequest(api, resize, deadline, target);
        async Task Expect(byte[] request, GpuRemoteCallKind kind, GpuRecreationState expected)
            => Assert.Equal(expected, (await owner.RecreateAsync(kind, request, token)).State);

        await Expect(Request(GpuGraphicsApi.D3D11, false, 1), GpuRemoteCallKind.ArmRecreation, GpuRecreationState.None);
        // The renderer is continuously presenting, but no resize is requested by this test.
        var resize = Request(GpuGraphicsApi.D3D11, true, checked((ulong)Environment.TickCount64 + 2000));
        await Expect(resize, GpuRemoteCallKind.ArmRecreation, GpuRecreationState.Armed);
        await Expect(resize, GpuRemoteCallKind.ArmRecreation, GpuRecreationState.None);
        var other = (byte[])resize.Clone();
        other[24] ^= 4;
        await Expect(other, GpuRemoteCallKind.CancelRecreation, GpuRecreationState.None);
        await Expect(resize, GpuRemoteCallKind.CancelRecreation, GpuRecreationState.Cancelled);
        await Expect(resize, GpuRemoteCallKind.FinishRecreation, GpuRecreationState.None);

        resize = Request(GpuGraphicsApi.D3D11, true, checked((ulong)Environment.TickCount64 + 100));
        await Expect(resize, GpuRemoteCallKind.ArmRecreation, GpuRecreationState.Armed);
        await Expect(resize, GpuRemoteCallKind.FinishRecreation, GpuRecreationState.Expired);
        var wrongApi = Request(GpuGraphicsApi.D3D12, false, checked((ulong)Environment.TickCount64 + 100));
        await Expect(wrongApi, GpuRemoteCallKind.ArmRecreation, GpuRecreationState.Armed);
        await Expect(wrongApi, GpuRemoteCallKind.FinishRecreation, GpuRecreationState.Expired);
    }

    private static ulong ParseLuid(string text)
    {
        var parts = text.Split('_');
        return (ulong.Parse(parts[1].AsSpan(2), NumberStyles.HexNumber) << 32) | ulong.Parse(parts[2].AsSpan(2), NumberStyles.HexNumber);
    }
    private static ulong Pack(int high, uint low) => ((ulong)(uint)high << 32) | low;
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(nint handle, out ulong birth, out ulong exit, out ulong kernel, out ulong user);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
