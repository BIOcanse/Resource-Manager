using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.Preparation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using static Resource_Manager_APP.Tests.GpuWindowLedgerTestData;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [OpenGlProductNativeFact]
    public async Task OpenGlProductActionUsesTargetPreparationAndOriginalLedgerRestoreWithoutReconfigure()
    {
        var root = NewRoot("opengl-product-native");
        var executable = Path.GetFullPath(Environment.GetEnvironmentVariable("RM_OPENGL_PRODUCT_TARGET")!);
        var adapter = ulong.Parse(Environment.GetEnvironmentVariable("RM_OPENGL_PRODUCT_ADAPTER")!, CultureInfo.InvariantCulture);
        var startup = Path.Combine(root, "startup-policy.txt");
        await File.WriteAllTextAsync(startup, $"0x{adapter >> 32:x8}_0x{adapter & uint.MaxValue:x8}");
        using var target = new Process { StartInfo = new(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        } };
        target.StartInfo.ArgumentList.Add(root);
        target.StartInfo.ArgumentList.Add(adapter.ToString(CultureInfo.InvariantCulture));
        target.StartInfo.Environment[D3d11ProxyShimRuntime.PolicyEnvironmentVariableName] = startup;
        Assert.True(target.Start());
        var errors = target.StandardError.ReadToEndAsync();
        var complete = false;
        try
        {
            var ready = await Read("ready");
            var identity = new GpuPlacementProcessInstance(target.Id, checked((ulong)target.StartTime.ToFileTimeUtc()),
                Path.GetFileNameWithoutExtension(executable), executable);
            Assert.Equal(identity.ProcessStartKey, ready.GetProperty("creationFileTime").GetUInt64());
            Assert.True(ready.GetProperty("inJob").GetBoolean());
            var sourceAdapter = ready.GetProperty("sourceLuid").GetUInt64();
            Assert.NotEqual(adapter, sourceAdapter);
            var historyEnvironment = new GpuPolicyEnvironment(root);
            // Explicit known-WGL fixture input; the action test does not cover first-use observation.
            var observed = await new JsonGpuPlacementProcessHistoryStore(historyEnvironment)
                .SaveFirstGraphicsApiAsync("software", "OpenGL fixture", identity, GpuGraphicsApi.OpenGL, default);
            Assert.Equal(GpuGraphicsApi.OpenGL, Assert.Single(observed.Processes).GraphicsApi);
            var history = new JsonGpuPlacementProcessHistoryStore(historyEnvironment);
            var software = GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software", "OpenGL fixture") with
            {
                EnabledMode = GpuPlacementPolicyModes.Auto, SchedulingMode = GpuPlacementSchedulingModes.Precise,
                RuntimeSchedulingMode = GpuPlacementRuntimeSchedulingModes.Precise, RuntimeHotSwitchEnabled = true,
                AllowedProviders = [GpuPlacementProviderIds.D3dDeviceCreateShim], TargetGpu = GpuPlacementTargets.IntegratedGpu
            };
            var callbackRuntime = new WindowsGpuCallbackPreparationRuntime();
            D3d11ProxyShimRuntime runtime = null!;
            var path = Path.Combine(root, "recovery.json");
            using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
                WindowsHostManagerRollbackStateFileCommitter.Instance, WindowsHostManagerDurableRootManifestCommitter.Instance);
            await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
                rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false,
                runningGpuActionsFactory: (fixtureRoot, plans) =>
                {
                    runtime = new(new GpuPolicyEnvironment(fixtureRoot));
                    return new WindowsRunningGpuPlacementActionService(NullLogger<WindowsRunningGpuPlacementActionService>.Instance,
                        runtime, new(NullLogger<WindowsGpuPlacementInjector>.Instance), history, plans, callbackRuntime);
                }, gpuCallbackRuntime: callbackRuntime);
            fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
            {
                Version = fixture.RuntimePlan.Version + 1,
                GpuPlacement = new GpuGraphicsApiIdentificationTests.Plans(software).Current.GpuPlacement
            });
            const string targetId = "opengl-product";
            Assert.True(runtime.TryWritePolicy(targetId, null, []));
            var plannedProcess = AutomaticGpuProcess() with
            {
                TargetId = targetId, SoftwareId = "software", DisplayName = "OpenGL fixture",
                ProcessId = identity.ProcessId, ProcessStartKey = identity.ProcessStartKey,
                ProcessName = identity.ProcessName, ExecutablePath = executable,
                Policy = ResolvedGpuPlacementPolicy.FromSoftware(software)
            };
            var desired = await CreateGpuDesired(fixture.Coordinator, plannedProcess, null, adapter);
            Assert.NotNull(desired);
            Assert.NotNull(desired.RuntimeGpuAction);
            Assert.Equal(GpuGraphicsApi.OpenGL, Assert.Single(desired.RuntimeGpuAction.GraphicsApis).Value);
            Assert.Empty(runtime.ReadPolicy(targetId)!);
            var original = desired.Record;
            var state = (await store.ReserveNativeHostSessionIncarnationAsync(default)) with { AppliedPlacements = [desired.Placement] };
            await store.SaveAsync(state, default);
            Assert.True(runtime.TryApplyPolicyRecord(original));
            var placement = state.AppliedPlacements[0];
            var projected = HostManagerPlacementCoordinatorProjection.ProjectDesired([desired], new NativePlacementDesiredInput[1]).Records.Single();
            var running = (Task<(HostManagerRollbackStateDocument State, bool CanContinue)>)typeof(HostManagerSmartCoordinator)
                .GetMethod("ExecuteGpuShimActionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.Coordinator, [state, projected, checked((ulong)Environment.TickCount64 + 30000), CancellationToken.None])!;
            var applied = await running.WaitAsync(TimeSpan.FromSeconds(35));
            Assert.True(applied.CanContinue);
            var saved = ReadCanonical(path);
            var policy = Assert.Single(saved.AppliedPlacements[0].Records, r => r.Kind == HostManagerAppliedRecordKinds.GpuShimPolicy);
            using var preparation = JsonDocument.Parse(policy.Metadata!["openGlPreparation"]);
            var prepared = preparation.RootElement;
            Assert.Equal(adapter, prepared.GetProperty("CallbackAdapter").GetUInt64());
            Assert.Equal(0U, prepared.GetProperty("Cleanup").GetProperty("ExitCode").GetUInt32());
            Assert.False(prepared.GetProperty("Cleanup").GetProperty("TerminationRequested").GetBoolean());
            Assert.Equal(0U, prepared.GetProperty("Cleanup").GetProperty("ActiveProcessCount").GetUInt32());
            var summary = JsonSerializer.Deserialize<RunningGpuPlacementActionResult>(policy.Metadata["runtimeActionResult"])!;
            Assert.Equal(RunningGpuPlacementActionStatuses.Prepared, summary.Status);
            var process = Assert.Single(summary.Processes);
            Assert.True(process.Configured);
            Assert.Equal(0UL, process.Before!.Snapshot!.OpenGL.ReturnedDeviceCount);
            Assert.Equal(process.Before.Snapshot, process.After!.Snapshot);
            var calls = saved.AppliedPlacements[0].Records.Where(GpuRemoteCallRecord.IsActionFact).ToArray();
            Assert.Equal(4, calls.Length);
            Assert.All(calls, record => { Assert.True(GpuRemoteCallRecord.TryRead(record, out var call)); Assert.False(call.BlocksProcess); });
            await Command("after-install", "after-install");
            await Command("create", "created");
            await Command("render", "rendered");
            Assert.True(runtime.RestorePolicyRecord(policy).CanRemoveReceipt);
            Assert.Empty(runtime.ReadPolicy(targetId)!);
            Assert.Equal(adapter, (await Command("retained-target", "retained-target")).GetProperty("actualLuid").GetUInt64());
            foreach (var command in new[] { "default-create", "default-layer", "default-attributes", "fallback-create" })
            {
                if (command == "fallback-create")
                {
                    Assert.True(runtime.TryWritePolicy(targetId, [], null));
                    var absent = runtime.CapturePolicyRecord(targetId, desired.RuntimeGpuAction.PolicyValue)!;
                    var next = saved with { AppliedPlacements = [placement with { Records = [absent, .. calls] }] };
                    await store.SaveAsync(next, default);
                    Assert.True(runtime.TryApplyPolicyRecord(absent));
                    Assert.True(runtime.RestorePolicyRecord(absent).CanRemoveReceipt);
                    Assert.Null(runtime.ReadPolicy(targetId));
                }
                await Command(command, "candidate-created");
                var rendered = await Command("candidate-render", "candidate-rendered");
                Assert.True(rendered.GetProperty("originalsPreserved").GetBoolean());
                Assert.Equal(command == "fallback-create" ? adapter : sourceAdapter, rendered.GetProperty("actualLuid").GetUInt64());
                await Command("candidate-delete", "candidate-deleted");
            }
            var done = await Command("stop", "complete");
            await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, target.ExitCode);
            Assert.Empty(await errors);
            Assert.True(done.GetProperty("foregroundUnchanged").GetBoolean());
            Assert.Equal(4, ReadCanonical(path).AppliedPlacements[0].Records.Count(GpuRemoteCallRecord.IsActionFact));
            await File.WriteAllTextAsync(Path.Combine(root, "product-result.json"), JsonSerializer.Serialize(new
            {
                passed = true, identity, sourceAdapter, targetAdapter = adapter, preparation = prepared.Clone(), summary,
                calls, native = done, targetExitCode = target.ExitCode, productActionAndLedger = true,
                normalHistoryAndDesiredAdmission = true, observed,
                currentDeviceObservationsAfterAction = false, additionalConfigureForRestore = false, productSupported = false
            }));
            complete = true;
        }
        finally
        {
            if (!complete && !target.HasExited) { target.Kill(); await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            await File.WriteAllTextAsync(Path.Combine(root, "target.stderr.log"), await errors);
        }

        async Task<JsonElement> Command(string command, string stage)
        {
            await target.StandardInput.WriteLineAsync(command);
            await target.StandardInput.FlushAsync();
            return await Read(stage);
        }
        async Task<JsonElement> Read(string stage)
        {
            var line = await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(35));
            Assert.NotNull(line);
            await File.AppendAllTextAsync(Path.Combine(root, "target.stdout.log"), line + "\n");
            using var document = JsonDocument.Parse(line);
            Assert.Equal(stage, document.RootElement.GetProperty("stage").GetString());
            return document.RootElement.Clone();
        }
    }
}

internal sealed class OpenGlProductNativeFactAttribute : FactAttribute
{
    public OpenGlProductNativeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RM_OPENGL_PRODUCT_TARGET")))
            Skip = "Requires the explicitly owned OpenGL target and retained current native images.";
    }
}
