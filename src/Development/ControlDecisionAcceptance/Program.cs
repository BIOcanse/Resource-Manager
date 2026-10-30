using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.MemoryProtection;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks;
using BusinessBlockScheduler = ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks.BusinessBlockScheduler;

namespace ResourceManager.ControlDecisionAcceptance;

internal static partial class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        try { return await RunAsync(args); }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] == "--actual-use") return await RunActualUseAsync(args[1..]);
        if (args.Length > 0 && args[0] == "--gpu-filter-only") return await RunGpuFilterOnlyAsync(args[1..]);
        if (args.Length > 0 && args[0] == "--present-observation") return await RunPresentObservationAsync(args[1..]);
        if (args.Length > 0 && args[0] == "--actual-timing")
            return await RunActualTimingAsync(args[1..]);
#if !RM_STATE_NAMES
        throw new InvalidOperationException("This development-only acceptance executable requires ResourceManagerStateNames=true.");
#else
        if (args.Length != 8 || args[0] != "--registered-root" || args[2] != "--runtime-root" || args[4] != "--artifact-manifest" || args[6] != "--source-manifest")
            throw new ArgumentException("Usage: --registered-root <registered real-copy task> --runtime-root <new private absolute root> --artifact-manifest <accepted copied output manifest> --source-manifest <bound source input manifest>");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        WindowsNativeLaunchErrorPolicy.InitializeForCurrentProcess();
        var image = Path.GetFullPath(AppContext.BaseDirectory);
        var root = Path.GetFullPath(args[3]);
        var registeredRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1])) + Path.DirectorySeparatorChar;
        if (!Path.IsPathFullyQualified(args[1]) || !File.Exists(Path.Combine(registeredRoot, ".generated-workspace.json"))
            || !Path.IsPathFullyQualified(args[3]) || !root.StartsWith(registeredRoot, StringComparison.OrdinalIgnoreCase)
            || !image.StartsWith(registeredRoot, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(image, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(root))
            throw new InvalidDataException("Run only a verified copied image with a new separate private runtime child under the registered real-copy task.");
        var artifacts = CopiedArtifact.Verify(args[5], args[7]);
        Directory.CreateDirectory(root);
        var evidence = Path.Combine(root, "evidence");
        Directory.CreateDirectory(evidence);
        Environment.SetEnvironmentVariable("RESOURCE_MANAGER_PACKAGE_ROOT", root);
        Directory.SetCurrentDirectory(root);
        var pipeName = $"ResourceManager.ControlDecisionAcceptance.{Environment.ProcessId}.{Guid.NewGuid():N}";
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            Args = [], ContentRootPath = root, EnvironmentName = "Development",
            ApplicationName = typeof(ResourceManagerServiceCollectionExtensions).Assembly.GetName().Name
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(20));
        var pipeline = LoopbackApiPipelineConfigurator.CompileForCurrentProcess();
        pipeline.ConfigureServices(builder.Services);
        builder.Services.AddResourceManagerApp([], StartupCapabilitySet.Full, BackendHostEnvironment.Interactive);
        EffectBoundaries.Apply(builder.Services, root, pipeName);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();
        var app = builder.Build();
        var cases = new List<AcceptanceCaseResult>();
        ProbeJob? job = null;
        Process? ownProcess = null;
        object? processEffectScope = null;
        BusinessBlockScheduler? controller = null;
        AppSettings? privateSettings = null;
        TopologyCaseEvidence? topology = null;
        string? address = null;
        var pointCreated = false;
        var enabledStageReached = false;
        Task? disposalTask = null;
        var stopSucceeded = false;
        var disposeSucceeded = false;
        try
        {
            var startupSucceeded = await RunCaseAsync(cases, evidence, "startup", TimeSpan.FromSeconds(30), async token =>
            {
                job = new ProbeJob();
                var processScope = app.Services.GetRequiredService<HostManagerProcessEffectValidationScopeAuthority>();
                ownProcess = Process.GetCurrentProcess();
                var scope = processScope.Open(new(Guid.NewGuid(), job.Name, DateTimeOffset.UtcNow.AddMinutes(5),
                    Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                    [new(Environment.ProcessId, ownProcess.StartTime.ToUniversalTime().ToFileTimeUtc())]));
                if (scope.State != "active") throw new InvalidOperationException("The production process-effect scope did not close admission.");
                processEffectScope = scope;
                var defaults = ResourceManager.App.Application.Settings.AppSettingsDefaults.Create();
                var reserveRoot = Path.Combine(root, "UserData", "ProbeMemoryReserve");
                Directory.CreateDirectory(reserveRoot);
                var cluster = HostMemoryDiskReserveNative.Volume(reserveRoot).Cluster;
                privateSettings = defaults with {
                    Performance = defaults.Performance with { OptimizationMode = AppOptimizationModes.Smart },
                    SystemIntegration = defaults.SystemIntegration with { AutoStartEnabled = false },
                    Debug = new(false, false, false, false), PublicService = new(false, false, false, false),
                    Update = new(AppUpdateModes.Off, false),
                    MemoryProtection = (defaults.MemoryProtection ?? new HostMemoryProtectionSettings()) with
                        { Enabled = false, Reserves = [new(reserveRoot, cluster)] }
                };
                await app.Services.GetRequiredService<IAppSettingsStore>().SaveAsync(privateSettings, token);
                pipeline.ConfigureApplication(app);
                // This read-only route never takes Page or sampling demand.
                app.MapGet("/probe/controller", () => app.Services.GetRequiredService<BusinessBlockScheduler>().Read());
                await app.StartAsync(token);
                controller = app.Services.GetRequiredService<BusinessBlockScheduler>();
                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
                    ?? throw new InvalidOperationException("No listener address.");
                address = addresses.Single();
                var uri = new Uri(address);
                if (uri.Host != "127.0.0.1" || uri.Port is 0 or 9321) throw new InvalidOperationException("Listener isolation failed.");

                await SaveAsync(evidence, "run-metadata.json", new {
                    processId = Environment.ProcessId, address, pipeName, image, privatePackageRoot = root,
                    artifactManifest = artifacts, overrides = EffectBoundaries.Overrides, processEffectScope
                });
            }, deadline.Token, () => controller);

            if (startupSucceeded)
            {
                // Independent evidence does not depend on HMP or native ETW availability.
                await RunCaseAsync(cases, evidence, "cpu-topology", TimeSpan.FromSeconds(30), async token =>
                { topology = await VerifyTopologyAsync(app.Services, controller!, evidence, token); },
                    deadline.Token, () => controller);
                await RunCaseAsync(cases, evidence, "private-http", TimeSpan.FromSeconds(7), async token =>
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    using var response = await client.GetAsync(address + "/probe/controller", token);
                    response.EnsureSuccessStatusCode();
                    await File.WriteAllTextAsync(Path.Combine(evidence, "private-http-controller.json"),
                        await response.Content.ReadAsStringAsync(token), token);
                }, deadline.Token, () => controller);
                await RunCaseAsync(cases, evidence, "hmp-point-and-enable", TimeSpan.FromSeconds(30), async token =>
                {
                    try
                    {
                        var hmp = app.Services.GetRequiredService<HostMemoryProtectionService>();
                        await hmp.CreatePointAsync(token);
                        await WaitAsync(() => hmp.ReadPoint() is not null, token);
                        pointCreated = true;
                        var settings = privateSettings ?? throw new InvalidOperationException("Private settings were not prepared.");
                        var applied = await app.Services.GetRequiredService<RuntimeSpecializationCoordinator>().ApplyAppSettingsAsync(
                            settings with { MemoryProtection = settings.MemoryProtection! with { Enabled = true } },
                            "development-real-copy-acceptance", token);
                        await SaveAsync(evidence, "tray-settings.json", applied);
                        if (applied.RuntimeApplicationDisposition != AppSettingsRuntimeApplicationDisposition.CommittedAndApplied)
                            throw new InvalidOperationException($"Private settings not fully applied: {applied.RuntimeApplicationDisposition} {applied.RuntimeFailureCode}");
                        enabledStageReached = true;
                        await WaitAsync(() => controller!.Read().Blocks.Single(block => block.Id == BlockUids.HostMemoryProtection)
                            .State == BusinessBlockState.Running, token);
                        await SaveAsync(evidence, "tray-hmp.json", await hmp.ReadStatusAsync(token));
                    }
                    finally
                    {
                        await SaveAsync(evidence, "hmp-stages.json", new { pointCreated, enabledStageReached,
                            sequence = "real-create-point-then-apply-enabled" });
                    }
                }, deadline.Token, () => controller);
                await RunCaseAsync(cases, evidence, "strict-tray", TimeSpan.FromSeconds(30), async token =>
                {
                    await SaveAsync(evidence, "tray-controller-unsettled.json", controller!.Read());
                    await SettleTrayAsync(controller!, evidence, "strict", token);
                }, deadline.Token, () => controller);
            }
            else
            {
                foreach (var name in new[] { "cpu-topology", "private-http", "hmp-point-and-enable", "strict-tray" })
                    await RecordNotRunAsync(cases, evidence, name, "startup did not complete");
            }
        }
        catch (Exception error)
        {
            await RecordFailureAsync(cases, evidence, "preparation", error);
        }
        finally
        {
            // Cleanup remains real; an exception is evidence of failure, never proof of returned resources.
            stopSucceeded = await RunCaseAsync(cases, evidence, "cleanup-stop", TimeSpan.FromSeconds(20),
                token => app.StopAsync(token), CancellationToken.None, () => controller);
            disposeSucceeded = await RunCaseAsync(cases, evidence, "cleanup-dispose", TimeSpan.FromSeconds(20), async token =>
            {
                disposalTask = app.DisposeAsync().AsTask();
                await disposalTask.WaitAsync(token);
            }, CancellationToken.None);
            ownProcess?.Dispose();
            job?.Dispose(); // Own Job has no kill-on-close or termination flags.
        }
        var passed = cases.All(result => result.Status == "passed");
        await SaveAsync(evidence, "acceptance.json", new {
            contract = "resource-manager-control-decision-real-copy-v2",
            status = passed ? "passed-with-listed-development-overrides" : "failed-with-listed-development-overrides",
            processId = Environment.ProcessId, address, pipeName, image, privatePackageRoot = root,
            artifactManifest = artifacts, overrides = EffectBoundaries.Overrides, cases,
            pointCreated, enabledStageReached,
            firstPublicationMilliseconds = topology?.First.ElapsedMilliseconds,
            resumedPublicationMilliseconds = topology?.Resumed.ElapsedMilliseconds,
            cleanup = new { stopSucceeded, disposeSucceeded, disposeTaskCreated = disposalTask is not null,
                disposeTaskCompleted = disposalTask?.IsCompleted, completedWithoutObservedError = stopSucceeded && disposeSucceeded },
            limitations = new[] {
                "Effects intentionally disabled; not a fully unmodified product.",
                "Driver/OEM providers and optional renderer observation are unavailable.",
                "General metrics uses native scratch; measured role 7 uses actual production Ledger scratch.",
                "Role 7 diagnostics prove its own borrowed lease/output scratch returned, not closure of shared owner workspace."
            }
        });
        return passed ? 0 : 1;
#endif
    }

    private sealed record TopologyResources(int SourceRegistrations, bool PendingSessionLease,
        ulong OutputScratchBytes, ulong PendingOutputCloseBytes, bool OutputUseActive, int ManagedOutputRows)
    {
        public bool Returned => SourceRegistrations == 0 && !PendingSessionLease && OutputScratchBytes == 0
            && PendingOutputCloseBytes == 0 && !OutputUseActive && ManagedOutputRows == 0;
    }
    private static TopologyResources ReadResources(CpuTopologySnapshotProvider source)
    {
        var value = source.ReadSamplingResourceDiagnostics();
        return new(value.SourceRegistrations, value.PendingSessionLease, value.OutputScratchBytes,
            value.PendingOutputCloseBytes, value.OutputUseActive, value.ManagedOutputRows);
    }
    private sealed record TopologyPublication(CpuTopologySnapshot Snapshot, double ElapsedMilliseconds, TopologyResources ActiveResources);
    private static async Task<TopologyPublication> CaptureTopologyAsync(CpuTopologySnapshotProvider source, string id, TimeSpan interval,
        DateTimeOffset? previous, CancellationToken token)
    {
        using var subscription = CancellationTokenSource.CreateLinkedTokenSource(token);
        var started = Stopwatch.GetTimestamp();
        await using var enumerator = source.SubscribeAsync(id, interval, subscription.Token).GetAsyncEnumerator(subscription.Token);
        try
        {
            while (await enumerator.MoveNextAsync())
            {
                if (enumerator.Current is not { LogicalProcessorCount: > 0 } snapshot
                    || snapshot.LogicalProcessors.Count == 0 || previous is { } before && snapshot.CapturedAt <= before) continue;
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var resources = ReadResources(source);
                if (resources.SourceRegistrations != 1 || resources.OutputScratchBytes == 0)
                    throw new InvalidOperationException("Expected the sole actual probe registration and nonempty production output scratch.");
                return new(snapshot, elapsed, resources);
            }
            throw new InvalidOperationException("CPU topology source ended without a fresh valid publication.");
        }
        finally { subscription.Cancel(); }
    }
    private static async Task<BusinessBlocksSnapshot> SettleTrayAsync(BusinessBlockScheduler scheduler,
        string evidence, string phase, CancellationToken token)
    {
        try
        {
            while (true)
            {
                var snapshot = scheduler.Read();
                var comparison = TrayExpectation.Compare(snapshot, scheduler.Authority);
                if (comparison.Matches)
                {
                    token.ThrowIfCancellationRequested();
                    await SaveAsync(evidence, $"tray-{phase}-controller.json", snapshot);
                    await SaveAsync(evidence, $"tray-{phase}-expectation.json", comparison);
                    return snapshot;
                }
                await Task.Delay(25, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            var snapshot = scheduler.Read();
            await SaveAsync(evidence, $"tray-{phase}-controller.json", snapshot);
            await SaveAsync(evidence, $"tray-{phase}-expectation.json", TrayExpectation.Compare(snapshot, scheduler.Authority));
            throw;
        }
    }
    private static async Task WaitAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(25, token);
    }
    private static async Task SaveAsync(string root, string file, object value)
    {
        await File.WriteAllTextAsync(Path.Combine(root, file), JsonSerializer.Serialize(value, Json));
        if (value is BusinessBlocksSnapshot { NativeSnapshot: { } native })
            await File.WriteAllTextAsync(Path.Combine(root, Path.GetFileNameWithoutExtension(file) + ".native.json"), native.GetRawText());
    }
}











