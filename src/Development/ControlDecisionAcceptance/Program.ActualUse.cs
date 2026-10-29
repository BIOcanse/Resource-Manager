using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using ResourceManager.App.StateItems;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.ControlDecisionAcceptance;

internal static partial class Program
{
    // This mode uses the real DI graph and real Windows ETW, while retaining the
    // development executable's explicit denied-effect boundaries.
    private static async Task<int> RunActualUseAsync(string[] args)
    {
        if (args.Length != 8 || args[0] != "--registered-root" || args[2] != "--runtime-root"
            || args[4] != "--artifact-manifest" || args[6] != "--source-manifest")
            throw new ArgumentException("--actual-use --registered-root <registered task> --runtime-root <new child> --artifact-manifest <copy manifest> --source-manifest <bound sources>");
        if (!OperatingSystem.IsWindows() || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("The actual-use read-only probe requires an existing effective administrator context.");
        var registered = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
        var root = Path.GetFullPath(args[3]);
        var image = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        if (!registered.StartsWith("F:\\", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(registered, ".generated-workspace.json"))
            || !root.StartsWith(registered + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !image.StartsWith(registered + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(image + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(root))
            throw new InvalidDataException("Use separate copied image and fresh runtime children of a registered task on F:.");
        var artifacts = CopiedArtifact.Verify(args[5], args[7]);
        WindowsNativeLaunchErrorPolicy.InitializeForCurrentProcess();
        Directory.CreateDirectory(root);
        var evidence = Path.Combine(root, "evidence");
        Directory.CreateDirectory(evidence);
        Environment.SetEnvironmentVariable("RESOURCE_MANAGER_PACKAGE_ROOT", root);
        Directory.SetCurrentDirectory(root);
        var pipe = $"ResourceManager.ActualUseAcceptance.{Environment.ProcessId}.{Guid.NewGuid():N}";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root,
            EnvironmentName = "Development", ApplicationName = typeof(ResourceManagerServiceCollectionExtensions).Assembly.GetName().Name });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var pipeline = LoopbackApiPipelineConfigurator.CompileForCurrentProcess();
        pipeline.ConfigureServices(builder.Services);
        builder.Services.AddResourceManagerApp([], StartupCapabilitySet.Full, BackendHostEnvironment.Interactive);
        EffectBoundaries.Apply(builder.Services, root, pipe);
        // The product's optional orphan recovery is not allowed to touch another process's sessions here.
        ConfigureActualUseEtw(builder.Services);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();
        var app = builder.Build();
        var cases = new List<AcceptanceCaseResult>();
        var samples = new List<ActualUseSample>();
        var eventRates = new List<ActualUseEventRateInterval>();
        var nativeSamples = new List<ActualUseNativeSample>();
        var names = new[] { ResourceManagerEtwSessionNames.KernelPrefix, ResourceManagerEtwSessionNames.PresentFramesPrefix,
            ResourceManagerEtwSessionNames.GpuAllocationsPrefix, ResourceManagerEtwSessionNames.DxgkrnlVidMmPrefix }
            .Select(prefix => ResourceManagerEtwSessionNames.Create(prefix, ResourceManagerEtwSessionNames.CurrentOwner)).ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        IDisposable? residencyLease = null;
        string? address = null;
        var settings = ResourceManager.App.Application.Settings.AppSettingsDefaults.Create();
        settings = settings with { Performance = settings.Performance with { OptimizationMode = AppOptimizationModes.Normal },
            SystemIntegration = settings.SystemIntegration with { AutoStartEnabled = false },
            Debug = new(false, false, false, false), PublicService = new(false, false, false, false),
            Update = new(AppUpdateModes.Off, false), MemoryProtection = new() { Enabled = false } };
        var broker = app.Services.GetRequiredService<IKernelEtwSessionBroker>();
        var counters = app.Services.GetRequiredService<ActualUseEtwCounters>();
        var smartResidency = app.Services.GetRequiredService<ActualUseSmartResidencyObserver>();
        var residency = app.Services.GetRequiredService<EtwCpuCoreResidencyReader>();
        var interrupts = app.Services.GetRequiredService<EtwSystemInterruptSnapshotSource>();
        var capture = (EtwCpuResidencyCaptureOwner)(typeof(EtwCpuCoreResidencyReader)
            .GetProperty("Capture", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(residency)!);
        async Task ApplySettings(string mode, CancellationToken token)
        {
            settings = settings with { Performance = settings.Performance with { OptimizationMode = mode } };
            var applied = await app.Services.GetRequiredService<RuntimeSpecializationCoordinator>()
                .ApplyAppSettingsAsync(settings, "development-actual-use", token);
            if (applied.RuntimeApplicationDisposition != AppSettingsRuntimeApplicationDisposition.CommittedAndApplied)
                throw new InvalidOperationException($"Settings did not apply: {applied.RuntimeFailureCode}");
        }
        var presentTransitions = new ConcurrentQueue<object>();
        var present = app.Services.GetRequiredService<PresentFrameEtwSource>();
        var presentTransitionEvent = typeof(PresentFrameEtwSource).GetEvent("HoldersChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var transitionAuthority = app.Services.GetRequiredService<BusinessBlockScheduler>().Authority;
        Action onPresentTransition = () => presentTransitions.Enqueue(new { at = DateTimeOffset.UtcNow,
            graphicsApiInUse = transitionAuthority.Read<bool>(StateItemUids.GraphicsApiObservationInUse),
            holders = ReadPresentHolders(present) });
        presentTransitionEvent.GetAddMethod(nonPublic: true)!.Invoke(present, [onPresentTransition]);
        async Task SetConsumers(bool reports, bool smart, CancellationToken token)
        {
            var permits = app.Services.GetRequiredService<BackendHostPermitFacts>();
            foreach (var pair in new[] { (BlockUids.HostManagerReportCoordinatorOwner, reports), (BlockUids.HostManagerSmartCoordinator, smart), (BlockUids.Reports, reports), (BlockUids.GpuPlacementProcessHistoryObserver, reports) })
                await permits.SetAsync(pair.Item1, pair.Item2, new(ResourceManager.Shared.BusinessBlocks.BusinessBlockRequestKind.Configuration,
                    "actual-use-profile", pair.Item2 ? "real-consumer-admitted" : "real-consumer-excluded", 0));
            await SaveAsync(evidence, $"consumer-profile-{DateTime.UtcNow.Ticks}.json", new { at = DateTimeOffset.UtcNow, reports, smart, reportsDomain = reports, kernelLeakAdmission = "Reports parent domain", processHistory = reports,
                mechanism = "existing BackendHostPermitFacts, not a synthetic interrupt lease or altered report plan" });
            token.ThrowIfCancellationRequested();
        }
        async Task WaitForNoUsers(CancellationToken token)
        {
            string? last = null;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var holders = ReadActualUseHolders(app.Services, broker);
                    try
                    {
                        VerifyNoDemandOwners(ReadNoDemandOwners(app.Services, holders));
                        VerifyNoDemandNative(holders.Kernel, QueryOwnedKernel(names[0]));
                        if (names.Skip(1).Any(name => QueryOwnedKernel(name).Exists)) throw new InvalidDataException("Optional native session remains.");
                        return;
                    }
                    catch (InvalidDataException error) { last = error.Message; }
                    await Task.Delay(100, token);
                }
            }
            finally { await SaveAsync(evidence, "last-no-user-settlement.json", new { at = DateTimeOffset.UtcNow, last,
                holders = ReadActualUseHolders(app.Services, broker), controller = app.Services.GetRequiredService<BusinessBlockScheduler>().Read() }); }
        }
        async Task Observe(string phase, TimeSpan duration, bool? expectedInterrupts, CancellationToken token,
            bool enforceWindow = true, bool noUsers = false, bool requireMetadata = false)
        {
            var until = Stopwatch.StartNew();
            var bad = 0;
            var activeCount = 0;
            var idleCount = 0;
            var nativeBad = new int[3];
            var nativeComparisons = new int[3];
            var priorEvents = counters.Read();
            var priorTick = Stopwatch.GetTimestamp();
            var priorWindow = capture.IsWindowActive;
            var nextLogman = TimeSpan.Zero;
            while (until.Elapsed < duration)
            {
                token.ThrowIfCancellationRequested();
                var before = capture.IsWindowActive;
                var native = QueryOwnedKernel(names[0]);
                var after = capture.IsWindowActive;
                var managed = broker.GetSnapshot();
                var holders = ReadActualUseHolders(app.Services, broker);
                var expectedBefore = ReadNativeExpectedUse(app.Services);
                var nativeStates = names.Skip(1).Select(QueryOwnedKernel).ToArray();
                var expectedAfter = ReadNativeExpectedUse(app.Services);
                nativeSamples.Add(new(phase, DateTimeOffset.UtcNow, expectedBefore, nativeStates, expectedAfter, holders));
                samples.Add(new(phase, DateTimeOffset.UtcNow, after, native, managed, before == after, holders));
                for (var index = 0; index < expectedBefore.Length; index++)
                {
                    if (expectedBefore[index] != expectedAfter[index]) { nativeBad[index] = 0; continue; }
                    nativeComparisons[index]++;
                    var expected = expectedAfter[index];
                    var activityUid = index switch { 0 => StateItemUids.GraphicsApiObservationInUse, 1 => StateItemUids.ProcessDedicatedGpuMemoryInUse, _ => StateItemUids.VramResidualInUse };
                    var activity = holders.Activity.Single(value => value.Uid == activityUid);
                    var wrong = nativeStates[index].Exists != expected.Expected || expected.InUse && !expected.Available
                        || expected.InUse && activity.HolderIds.Length == 0;
                    nativeBad[index] = wrong ? nativeBad[index] + 1 : 0;
                    if (nativeBad[index] >= 2)
                        throw new InvalidDataException($"Native session {expected.Name}: inUse={expected.InUse}, expected={expected.Expected}, actual={nativeStates[index].Exists}, GPU capture={expected.GpuCapture}; {DescribeHolders(holders)}");
                }
                var events = counters.Read();
                var tick = Stopwatch.GetTimestamp();
                var seconds = Stopwatch.GetElapsedTime(priorTick, tick).TotalSeconds;
                if (seconds > 0)
                    eventRates.Add(EventInterval(phase, priorWindow == before && before == after
                        ? before ? "active" : "idle" : "transition", seconds, priorEvents, events));
                priorEvents = events;
                priorTick = tick;
                priorWindow = after;
                if (before == after)
                {
                    if (before) activeCount++; else idleCount++;
                    var metadataMask = (uint)(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread);
                    var csMask = (uint)KernelTraceEventParser.Keywords.ContextSwitch;
                    var irqMask = (uint)(KernelTraceEventParser.Keywords.Interrupt | KernelTraceEventParser.Keywords.DeferedProcedureCalls);
                    var imageMask = (uint)KernelTraceEventParser.Keywords.ImageLoad;
                    var wrong = enforceWindow && ((native.Flags & csMask) != 0) != before;
                    if (expectedInterrupts is { } expected)
                        wrong |= (native.Flags & irqMask) != (expected ? irqMask : 0)
                            || expected && (native.Flags & imageMask) == 0;
                    if (requireMetadata)
                    {
                        var expectedMask = metadataMask | (before ? csMask : 0)
                            | (holders.Kernel.Any(value => value.Id == "system-interrupts") ? imageMask : 0)
                            | (expectedInterrupts == true ? irqMask : 0);
                        wrong |= native.Flags != expectedMask;
                        wrong |= holders.Kernel.Any(value => value.Id is not ("cpu-core-residency" or "cpu-core-residency-window" or "system-interrupts" or "system-interrupts-events"));
                    }
                    if (noUsers) wrong |= native.Exists || native.Flags != 0 || holders.Kernel.Length != 0
                        || holders.Activity.Any(value => value.InUse) || holders.Present.Length != 0
                        || nativeStates.Any(value => value.Exists);
                    bad = wrong ? bad + 1 : 0;
                    if (bad >= 2) throw new InvalidDataException($"Native flags contradict {phase}: window={before}, flags=0x{native.Flags:X}, expected interrupts={expectedInterrupts}; {DescribeHolders(holders)}");
                }
                if (until.Elapsed >= nextLogman)
                {
                    await QueryOwnedSessionsAsync(evidence, phase + "-" + samples.Count, names, token);
                    await SaveAsync(evidence, phase + "-" + samples.Count + "-holders.json", holders);
                    await SaveAsync(evidence, phase + "-" + samples.Count + "-event-drops.json", EtwEventDropDiagnostics.Read());
                    nextLogman = until.Elapsed + TimeSpan.FromSeconds(10);
                }
                await Task.Delay(100, token);
            }
            var nativeUse = ReadNativeExpectedUse(app.Services);
            await SaveAsync(evidence, phase + "-native-use.json", nativeUse);
            if (nativeComparisons.Any(value => value == 0)) throw new InvalidDataException("No stable native-use comparison was observed.");
            var finalHolders = ReadActualUseHolders(app.Services, broker);
            if (noUsers)
            {
                var owners = ReadNoDemandOwners(app.Services, finalHolders);
                await SaveAsync(evidence, phase + "-sampling-owners.json", owners);
                VerifyNoDemandOwners(owners);
                VerifyNoDemandNative(finalHolders.Kernel, QueryOwnedKernel(names[0]));
            }
            var snapshot = residency.Read();
            await SaveAsync(evidence, phase + "-summary.json", new { activeCount, idleCount, observedSeconds = until.Elapsed.TotalSeconds,
                residency = snapshot is null ? null : new { snapshot.MeasuredFrom, snapshot.MeasuredThrough, snapshot.CapturedAt,
                    snapshot.SessionGeneration, processRows = snapshot.Processes.Count },
                interrupts = interrupts.Read(), diagnostics = residency.GetDiagnostics(), broker = broker.GetSnapshot(), holders = finalHolders,
                eventRates = SummarizeEventRates(eventRates.Where(value => value.Phase == phase)) });
        }


        try
        {
            var startup = await RunCaseAsync(cases, evidence, "startup", TimeSpan.FromSeconds(40), async token =>
            {
                await app.Services.GetRequiredService<IAppSettingsStore>().SaveAsync(settings, token);
                pipeline.ConfigureApplication(app);
                await app.StartAsync(token);
                await SetConsumers(false, false, token);
                // Portable discovery is an unrelated real ETW consumer. The
                // existing host admission switch excludes it from this probe,
                // allowing a/e to require actual session absence, not metadata.
                await app.Services.GetRequiredService<BackendHostPermitFacts>().SetAsync(
                    BlockUids.PortableSoftwareDiscoveryService, false,
                    new(ResourceManager.Shared.BusinessBlocks.BusinessBlockRequestKind.Configuration,
                        "actual-use-profile", "portable-discovery-excluded", 0));
                await WaitAsync(() => app.Services.GetRequiredService<BusinessBlockScheduler>().Read().Blocks
                    .Single(value => value.Id == BlockUids.PortableSoftwareDiscoveryService).State
                        == ResourceManager.Shared.BusinessBlocks.BusinessBlockState.Stopped, token);
                address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                if (new Uri(address).Port is 0 or 9321 or 9333) throw new InvalidDataException("Probe listener is not private.");
                await SaveAsync(evidence, "run-metadata.json", new { processId = Environment.ProcessId, names, address, pipe, image, root,
                    artifactManifest = artifacts, overrides = EffectBoundaries.Overrides, orphanRecovery = "disabled",
                    portableDiscovery = "existing host admission false in every phase; no portable ETW consumer",
                    backgroundConsumers = "existing Reports domain (including KernelLeak), report coordinator, Smart and process-history admissions false in a/b/e; original configured consumers admitted in c/d" });
            }, deadline.Token);
            if (startup)
            {
                await RunCaseAsync(cases, evidence, "a-no-subscriptions", TimeSpan.FromSeconds(160), async token =>
                {
                    await WaitForNoUsers(token);
                    await Observe("a-no-subscriptions", TimeSpan.FromSeconds(120), false, token, noUsers: true);
                }, deadline.Token);
                var residencyInterval = TimeSpan.FromSeconds(30);
                var residencyPlan = app.Services.GetRequiredService<RuntimePlanProvider>().Current.HostManager.RequirePublished();
                var residencyWindow = residencyPlan.CpuCoreResidency.ObservationWindow;
                var residencyGrace = TimeSpan.FromMilliseconds(residencyPlan.SamplingSubscription.HotPublish.Roles.Single(value => value.RoleId == 6).FreshnessGraceMilliseconds);
                var residencyTolerance = TimeSpan.FromMilliseconds(500);
                // The initial due can fail warming/continuity. Allow that cold cadence and
                // its complete window, then two steady publication periods and delivery grace.
                var residencyColdBudget = residencyInterval + residencyWindow + residencyGrace;
                var residencySteadyBudget = residencyInterval + residencyGrace;
                var residencyMeasurementBudget = residencyColdBudget + residencySteadyBudget + residencySteadyBudget + residencyTolerance;
                await RunCaseAsync(cases, evidence, "b-30-second-residency-two-windows",
                    residencyMeasurementBudget + residencyWindow + residencyGrace, async token =>
                {
                    var subscribedAt = DateTimeOffset.UtcNow;
                    residencyLease = residency.AcquireSubscription("actual-use:30s", residencyInterval);
                    var publications = new List<object>();
                    var lastRevision = residency.CanonicalRevision;
                    try
                    {
                    await WaitAsync(() => (broker.GetSnapshot().EnabledKeywords & (KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread)) == (KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread), token);
                    var windows = new List<ResidencyWindowEvidence>();
                    var started = Stopwatch.StartNew();
                    var active = false;
                    var idle = false;
                    while (started.Elapsed < residencyMeasurementBudget)
                    {
                        await Observe("b-residency", TimeSpan.FromSeconds(2), false, token, requireMetadata: true);
                        var revision = residency.CanonicalRevision;
                        if (revision != lastRevision)
                        {
                            // An unavailable publication is a real completed sample too.
                            // Preserve its reason and continuity diagnostics, not only non-null windows.
                            publications.Add(new { at = DateTimeOffset.UtcNow, revision,
                                observation = (object)residency.ReadObservation(), diagnostics = residency.GetDiagnostics() });
                            lastRevision = revision;
                        }
                        if (residency.Read() is { } published && published.MeasuredFrom >= subscribedAt
                            && windows.All(value => value.MeasuredThrough != published.MeasuredThrough))
                        {
                            if (published.MeasuredThrough <= published.MeasuredFrom || published.Processes.Count == 0)
                                throw new InvalidDataException("Residency publication contains no real closed-window process samples.");
                            windows.Add(new(published.MeasuredFrom, published.MeasuredThrough, published.SessionGeneration, published.Processes.Count));
                        }
                        active |= samples.Any(value => value.Phase == "b-residency" && value.WindowActive);
                        idle |= !capture.IsWindowActive;
                        if (windows.Count >= 3 && active && idle) break;
                    }
                    const double cadenceToleranceMilliseconds = 500;
                    await SaveAsync(evidence, "b-publications.json", new { windows, active, idle,
                        requestedIntervalMilliseconds = 30000, cadenceToleranceMilliseconds,
                        coldStartBudget = residencyColdBudget, steadyWindowBudget = residencySteadyBudget,
                        measurementBudget = residencyMeasurementBudget, observationWindow = residencyWindow, deliveryGrace = residencyGrace,
                        diagnostics = residency.GetDiagnostics(),
                        coldStart = "The first due can fail warming/continuity. One cold cadence and complete window, followed by two steady periods; no additional retry allowance." });
                    if (windows.Count < 3) throw new InvalidDataException("Did not observe cold start plus two steady-state residency windows.");
                    if (windows[0].SessionGeneration <= 0 || windows.Any(value => value.SessionGeneration != windows[0].SessionGeneration))
                        throw new InvalidDataException("Residency keyword window updates restarted the held kernel session.");
                    for (var index = 1; index < windows.Count; index++)
                    {
                        var previous = windows[index - 1];
                        var current = windows[index];
                        var spacing = (current.MeasuredThrough - previous.MeasuredThrough).TotalMilliseconds;
                        bool Active(ResidencyWindowEvidence window) => samples.Any(value => value.Phase == "b-residency"
                            && value.WindowActive && value.At >= window.MeasuredFrom && value.At <= window.MeasuredThrough
                            && (value.Native.Flags & (uint)KernelTraceEventParser.Keywords.ContextSwitch) != 0);
                        var separated = samples.Any(value => value.Phase == "b-residency" && !value.WindowActive
                            && value.At > previous.MeasuredThrough && value.At < current.MeasuredFrom
                            && (value.Native.Flags & (uint)KernelTraceEventParser.Keywords.ContextSwitch) == 0);
                        if (spacing < 30000 - cadenceToleranceMilliseconds || current.MeasuredFrom <= previous.MeasuredThrough
                            || !Active(previous) || !Active(current) || !separated)
                            throw new InvalidDataException($"Residency cadence/window separation failed: spacing={spacing}ms, idleBetween={separated}.");
                    }
                                        }
                    finally
                    {
                        residencyLease?.Dispose(); residencyLease = null;
                        await SaveAsync(evidence, "b-observations.json", publications);
                    }
                }, deadline.Token);
                await RunCaseAsync(cases, evidence, "c-tray-smart-with-interrupt-report",
                    TimeSpan.FromMilliseconds(2 * (Math.Max(
                        app.Services.GetRequiredService<RuntimePlanProvider>().Current.HostManager.RequirePublished().SmartCoordinator.HotPublish.NormalIntervalMilliseconds,
                        app.Services.GetRequiredService<RuntimePlanProvider>().Current.HostManager.RequirePublished().SmartCoordinator.HotPublish.EventIntervalMilliseconds)
                        + app.Services.GetRequiredService<RuntimePlanProvider>().Current.HostManager.RequirePublished().CpuCoreResidency.ObservationWindowMilliseconds
                        + app.Services.GetRequiredService<RuntimePlanProvider>().Current.HostManager.RequirePublished().SamplingSubscription.HotPublish.Roles[5].FreshnessGraceMilliseconds))
                        + TimeSpan.FromSeconds(135), async token =>
                {
                    var transitionAt = DateTimeOffset.UtcNow;
                    var previousResidency = residency.Read()?.MeasuredThrough;
                    var previousOutputs = smartResidency.SmartSnapshotOutputs;
                    var hostPlan = app.Services.GetRequiredService<RuntimePlanProvider>().Current.HostManager.RequirePublished();
                    var cadence = Math.Max(hostPlan.SmartCoordinator.HotPublish.NormalIntervalMilliseconds,
                        hostPlan.SmartCoordinator.HotPublish.EventIntervalMilliseconds);
                    var window = hostPlan.CpuCoreResidency.ObservationWindowMilliseconds;
                    var grace = hostPlan.SamplingSubscription.HotPublish.Roles[5].FreshnessGraceMilliseconds;
                    // One output can belong to the old/cold continuity interval. Allow a second real
                    // cadence plus its complete observation and delivery grace, not a fixed 35s wait.
                    var freshBudget = TimeSpan.FromMilliseconds(checked(2 * (cadence + window + grace)));
                    var waitStarted = Stopwatch.StartNew();
                    var conditions = new List<object>();
                    bool FreshSmartOutput()
                    {
                        var fresh = smartResidency.LastSmartSnapshot;
                        return interrupts.Read().Available && smartResidency.ActiveSmartSubscriptions == 1
                            && smartResidency.SmartSnapshotOutputs > previousOutputs
                            && fresh is not null && fresh.MeasuredFrom >= transitionAt && fresh.Processes.Count > 0
                            && (previousResidency is null || fresh.MeasuredThrough > previousResidency);
                    }
                    try
                    {
                        await ApplySettings(AppOptimizationModes.Smart, token);
                        await SetConsumers(true, true, token);
                        var software = await app.Services.GetRequiredService<ResourceManager.App.Application.Software.ISoftwareRegistryView>().GetSoftwareAsync(token);
                        await SaveAsync(evidence, "c-private-software-input.json", new { registeredSoftwareCount = software.Count,
                            processHistoryObserver = app.Services.GetRequiredService<BusinessBlockScheduler>().Read().Blocks.Single(value => value.Id == BlockUids.GpuPlacementProcessHistoryObserver).State,
                            scope = "Private copied graph registry; no installed registry copied, portable discovery excluded. A zero count cannot reproduce installed API-history attempts; unit gates cover that caller." });
                        while (!FreshSmartOutput())
                        {
                            var fresh = smartResidency.LastSmartSnapshot;
                            conditions.Add(new { at = DateTimeOffset.UtcNow, elapsed = waitStarted.Elapsed,
                                interruptsAvailable = interrupts.Read().Available, smartResidency.ActiveSmartSubscriptions,
                                smartResidency.SmartSnapshotOutputs, outputsAdvanced = smartResidency.SmartSnapshotOutputs > previousOutputs,
                                measuredFromAfterTransition = fresh is not null && fresh.MeasuredFrom >= transitionAt,
                                measuredThroughAdvanced = fresh is not null && (previousResidency is null || fresh.MeasuredThrough > previousResidency),
                                processRows = fresh?.Processes.Count, smartResidency.LastRequestedInterval,
                                sourceRunning = app.Services.GetRequiredService<BusinessBlockScheduler>().Read().Blocks.Single(value => value.Id == BlockUids.EtwCpuCoreResidencyReader).State,
                                residencyInUse = app.Services.GetRequiredService<BusinessBlockScheduler>().Authority.Read<bool>(StateItemUids.SamplingCpuResidencyInUse),
                                metadataBound = ReadKernelRegistrations(broker).Any(value => value.Id == "cpu-core-residency"),
                                capture.IsWindowActive, observation = residency.ReadObservation().GetType().Name,
                                diagnostics = residency.GetDiagnostics(), holders = ReadActualUseHolders(app.Services, broker) });
                            if (waitStarted.Elapsed >= freshBudget)
                                throw new InvalidDataException($"Smart output did not become fresh within two actual cadences/windows ({freshBudget}); see c-smart-wait-conditions.json.");
                            await Observe("c-tray-starting", TimeSpan.FromSeconds(2), null, token);
                        }
                    }
                    finally
                    {
                        var fresh = smartResidency.LastSmartSnapshot;
                        await SaveAsync(evidence, "c-smart-wait-conditions.json", new { transitionAt, previousResidency, previousOutputs,
                            cadenceMilliseconds = cadence, windowMilliseconds = window, freshnessGraceMilliseconds = grace,
                            freshBudgetMilliseconds = freshBudget.TotalMilliseconds, conditions,
                            final = new { at = DateTimeOffset.UtcNow, interruptsAvailable = interrupts.Read().Available,
                                smartResidency.ActiveSmartSubscriptions, smartResidency.SmartSnapshotOutputs,
                                measuredFrom = fresh?.MeasuredFrom, measuredThrough = fresh?.MeasuredThrough,
                                processRows = fresh?.Processes.Count, smartResidency.LastRequestedInterval } });
                    }
                    await Observe("c-tray-smart", TimeSpan.FromSeconds(120), true, token, requireMetadata: true);
                    var scheduler = app.Services.GetRequiredService<BusinessBlockScheduler>();
                    var holders = ReadActualUseHolders(app.Services, broker);
                    if (smartResidency.ActiveSmartSubscriptions != 1
                        || !scheduler.Authority.Read<bool>(StateItemUids.SamplingCpuResidencyInUse).GetValueOrDefault(false)
                        || scheduler.Read().Blocks.Single(value => value.Id == BlockUids.EtwCpuCoreResidencyReader).State
                            != ResourceManager.Shared.BusinessBlocks.BusinessBlockState.Running
                        || !holders.Interrupts.Any(value => value.InUse && value.Id.Contains("host-manager.reports.system-interrupts", StringComparison.Ordinal))
                        || holders.Interrupts.Concat(holders.Residency).Any(value => value.InUse && value.Id.Contains("actual-use:", StringComparison.Ordinal)))
                        throw new InvalidDataException($"Tray must use the actual report and Smart consumers, with no probe lease; {DescribeHolders(holders)}");
                    if (ReadInterruptModules(interrupts).Count == 0) throw new InvalidDataException("Real interrupt session did not retain any kernel module metadata.");
                }, deadline.Token);


                var interruptIntervals = ReadSamplingHolders(interrupts)
                    .Where(value => value.InUse && value.Interval is { } interval && interval > TimeSpan.Zero)
                    .Select(value => value.Interval!.Value).ToArray();
                if (interruptIntervals.Length == 0) throw new InvalidDataException("Interrupt report has no timed publication holder.");
                var interruptCadence = interruptIntervals.Min();
                var interruptGrace = TimeSpan.FromMilliseconds(app.Services.GetRequiredService<RuntimePlanProvider>().Current.HostManager.RequirePublished()
                    .SamplingSubscription.HotPublish.Roles.Single(value => value.RoleId == 5).FreshnessGraceMilliseconds);
                var interruptObservation = TimeSpan.FromSeconds(4);
                // Resume resets coverage. The first native due can be just before the
                // complete bucket; the following due publishes it, at most one cadence later.
                var interruptResumeBudget = SystemInterruptWindowAggregator.BucketDuration + interruptCadence + interruptGrace;
                var interruptPhaseBudget = interruptObservation + interruptResumeBudget + interruptObservation + TimeSpan.FromMilliseconds(500);
                await RunCaseAsync(cases, evidence, "d-interrupt-freeze-and-resume", interruptPhaseBudget, async token =>
                {
                    var transitions = new List<object>();
                    var conditions = new List<object>();
                    var phaseClock = Stopwatch.StartNew();
                    var stage = "freeze";
                    void Mark(string value)
                    {
                        stage = value;
                        var snapshot = interrupts.Read();
                        transitions.Add(new { stage, at = DateTimeOffset.UtcNow, elapsed = phaseClock.Elapsed,
                            snapshot.Available, snapshot.CapturedAt, snapshot.Window, providerState = snapshot.ProviderState.State });
                    }
                    var modules = ReadInterruptModules(interrupts);
                    var generation = broker.GetSnapshot().Generation;
                    try
                    {
                        interrupts.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
                        Mark("frozen-observation");
                        await Observe("d-interrupt-frozen", interruptObservation, false, token, requireMetadata: true);
                        var frozenNative = QueryOwnedKernel(names[0]);
                        if ((frozenNative.Flags & (uint)KernelTraceEventParser.Keywords.ImageLoad) == 0
                            || !ReferenceEquals(modules, ReadInterruptModules(interrupts)) || modules.Count == 0
                            || !interrupts.Read().ProviderState.State.Equals("frozen", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Freeze lost module metadata or ImageLoad ownership.");
                        interrupts.ApplyMode(ResourceManagerComputeZoneMode.Normal);
                        Mark("waiting-for-complete-resumed-publication");
                        var resumed = Stopwatch.StartNew();
                        await WaitAsync(() =>
                        {
                            var snapshot = interrupts.Read();
                            conditions.Add(new { at = DateTimeOffset.UtcNow, elapsed = resumed.Elapsed, snapshot.Available,
                                snapshot.CapturedAt, snapshot.Window, snapshot.EventCount, buckets = snapshot.Buckets.Count,
                                providerState = snapshot.ProviderState.State, broker = broker.GetSnapshot() });
                            if (snapshot.Available) return true;
                            if (resumed.Elapsed > interruptResumeBudget)
                                throw new InvalidDataException($"Interrupts did not publish complete resumed coverage within bucket + actual cadence + grace ({interruptResumeBudget}).");
                            return false;
                        }, token);
                        Mark("resumed-observation");
                        await Observe("d-interrupt-resumed", interruptObservation, true, token, requireMetadata: true);
                        if (broker.GetSnapshot().Generation != generation || !ReferenceEquals(modules, ReadInterruptModules(interrupts)))
                            throw new InvalidDataException("Freeze/resume replaced the held metadata session.");
                        Mark("complete");
                    }
                    finally
                    {
                        interrupts.ApplyMode(ResourceManagerComputeZoneMode.Normal);
                        await SaveAsync(evidence, "d-timing.json", new { at = DateTimeOffset.UtcNow, stage,
                            elapsed = phaseClock.Elapsed, interruptCadence, bucket = SystemInterruptWindowAggregator.BucketDuration,
                            interruptGrace, interruptResumeBudget, interruptObservation, interruptPhaseBudget, transitions, conditions,
                            final = interrupts.Read() });
                    }
                }, deadline.Token);
                await RunCaseAsync(cases, evidence, "gpu-allocation-filter", ActualUseGpuFilterBudget(app.Services),
                    token => RunActualUseGpuFilterPhaseAsync(app.Services, broker, evidence, token), deadline.Token);
                await RunCaseAsync(cases, evidence, "e-subscriptions-removed", TimeSpan.FromSeconds(75), async token =>
                {
                    residencyLease?.Dispose(); residencyLease = null;
                    await SetConsumers(false, false, token);
                    await ApplySettings(AppOptimizationModes.Normal, token);
                    await WaitForNoUsers(token);
                    await Observe("e-subscriptions-removed", TimeSpan.FromSeconds(30), false, token, noUsers: true);
                }, deadline.Token);
            }
            else foreach (var phase in new[] { "a-no-subscriptions", "b-30-second-residency-two-windows", "c-tray-smart-with-interrupt-report", "d-interrupt-freeze-and-resume", "gpu-allocation-filter", "e-subscriptions-removed" })
                await RecordNotRunAsync(cases, evidence, phase, "startup failed");
        }
        finally
        {
            residencyLease?.Dispose();
            await RunCaseAsync(cases, evidence, "cleanup", TimeSpan.FromSeconds(30), async token =>
            {
                await app.StopAsync(token);
                presentTransitionEvent.GetRemoveMethod(nonPublic: true)!.Invoke(present, [onPresentTransition]);
                await app.DisposeAsync().AsTask().WaitAsync(token);
            }, CancellationToken.None);
            presentTransitionEvent.GetRemoveMethod(nonPublic: true)!.Invoke(present, [onPresentTransition]);
            await SaveAsync(evidence, "present-holder-transitions.json", presentTransitions.ToArray());
            await SaveAsync(evidence, "keyword-samples.json", samples);
            await SaveAsync(evidence, "event-rate-intervals.json", eventRates);
            await SaveAsync(evidence, "event-rate-summary.json", SummarizeEventRates(eventRates));
            await SaveAsync(evidence, "native-session-samples.json", nativeSamples);
            await RunCaseAsync(cases, evidence, "owned-session-cleanup", TimeSpan.FromSeconds(15), async token =>
            {
                var result = await QueryOwnedSessionsAsync(evidence, "cleanup", names, token);
                if (result.Any(value => value.ExitCode == 0) || names.Any(name => QueryOwnedKernel(name).Exists))
                    throw new InvalidDataException("An owned ETW session remains after disposal.");
            }, CancellationToken.None);
            await SaveAsync(evidence, "acceptance.json", new { contract = "resource-manager-actual-use-real-copy-v2",
                status = cases.All(value => value.Status == "passed") ? "passed" : "failed", processId = Environment.ProcessId,
                names, address, pipe, image, root, cases, overrides = EffectBoundaries.Overrides,
                limitations = new[] { "Process and hardware effects rejected by development overrides.",
                    "Native keyword observations sampled every 100 ms; window boundary races require two consecutive contradictory observations.",
                    "Freeze validates retained real module map identity and ImageLoad keyword; it does not load or unload a kernel driver." } });
        }
        return cases.All(value => value.Status == "passed") ? 0 : 1;
    }

    private static IDictionary ReadInterruptModules(EtwSystemInterruptSnapshotSource source)
    {
        var session = source.GetType().GetField("currentSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)
            ?? throw new InvalidDataException("No real interrupt session.");
        var window = session.GetType().GetProperty("Window")!.GetValue(session)!;
        var modules = window.GetType().GetProperty("Modules")!.GetValue(window)!;
        return (IDictionary)modules.GetType().GetField("modules", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(modules)!;
    }

    private sealed record OwnedSessionQuery(string Name, int ExitCode, string Output, string Error);
    private static async Task<OwnedSessionQuery[]> QueryOwnedSessionsAsync(string evidence, string phase, string[] names, CancellationToken token)
    {
        var results = new List<OwnedSessionQuery>();
        foreach (var name in names)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "logman.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "query", name, "-ets" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("logman did not start.");
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            results.Add(new(name, process.ExitCode, await output, await error));
        }
        await SaveAsync(evidence, phase + "-owned-logman.json", results);
        return results.ToArray();
    }

    private sealed record NativeKernelQuery(bool Exists, uint Flags, uint EventsLost);
    private sealed record ActualUseSample(string Phase, DateTimeOffset At, bool WindowActive,
        NativeKernelQuery Native, KernelEtwSessionSnapshot Managed, bool StableWindow, ActualUseHolders Holders);

    private sealed record NativeExpectedUse(string Name, bool InUse, bool Permitted, bool Available, bool Unfrozen, bool Expected,
        ActualUseGpuCapture? GpuCapture = null);
    private sealed record ActualUseNativeSample(string Phase, DateTimeOffset At, NativeExpectedUse[] Before,
        NativeKernelQuery[] Actual, NativeExpectedUse[] After, ActualUseHolders Holders);
    private static NativeExpectedUse[] ReadNativeExpectedUse(IServiceProvider services)
    {
        var authority = services.GetRequiredService<BusinessBlockScheduler>().Authority;
        NativeExpectedUse Read(string name, int block, int inUseUid, int availableUid, int? unfrozenUid)
        {
            var use = authority.Read<bool>(inUseUid);
            var permit = authority.Read<bool>(new StateItemKey(StateItemUids.HostStartPermitted, block));
            var available = authority.Read<bool>(availableUid);
            var mode = unfrozenUid is { } uid ? authority.Read<bool>(uid) : StateRead<bool>.FromValue(true);
            var unfrozen = mode.HasValue ? mode.Value : mode.NoValueReason == NoValueReason.NotPublished;
            if (!use.HasValue || !permit.HasValue || !available.HasValue)
                throw new InvalidDataException($"Native ETW input facts missing for {name}.");
            return new(name, use.Value, permit.Value, available.Value, unfrozen,
                use.Value && permit.Value && available.Value && unfrozen);
        }
        var gpu = Read("GpuAllocation", BlockUids.GpuAllocationCollector, StateItemUids.ProcessDedicatedGpuMemoryInUse, StateItemUids.GpuAllocationNativeSessionAvailable, null);
        var capture = ReadGpuCaptureUse(services.GetRequiredService<GpuAllocationCollector>());
        gpu = gpu with { Expected = gpu.Expected && capture.NeedsNativeSession, GpuCapture = capture };
        return [Read("Present", BlockUids.PresentFrameEtwSource, StateItemUids.GraphicsApiObservationInUse, StateItemUids.PresentNativeSessionAvailable, StateItemUids.PresentUnfrozen), gpu,
            Read("VidMm", BlockUids.DxgkrnlVidMmEtwTelemetryZone, StateItemUids.VramResidualInUse, StateItemUids.VidMmNativeSessionAvailable, StateItemUids.VidMmUnfrozen)];
    }

    private sealed record KernelRegistrationEvidence(string Id, KernelTraceEventParser.Keywords Keywords);
    private static KernelRegistrationEvidence[] ReadKernelRegistrations(IKernelEtwSessionBroker broker)
        => broker is ActualUseEtwCounters counters ? counters.ReadRegistrations()
            : throw new InvalidDataException("The probe kernel broker is not instrumented.");
    private static void VerifyNoDemandNative(KernelRegistrationEvidence[] holders, NativeKernelQuery native)
    {
        if (holders.Length != 0 || native.Exists || native.Flags != 0)
            throw new InvalidDataException($"No-demand state must have no kernel session: holders=[{string.Join(",", holders.Select(value => $"{value.Id}:0x{(ulong)value.Keywords:X}"))}], exists={native.Exists}, flags=0x{native.Flags:X}.");
    }
    private static unsafe NativeKernelQuery QueryOwnedKernel(string name)
    {
        // ControlTrace QUERY only. There is deliberately no update/stop entrypoint here.
        var size = sizeof(QueryTraceProperties);
        var buffer = new byte[size + 4096];
        fixed (byte* bytes = buffer)
        {
            var value = (QueryTraceProperties*)bytes;
            value->Wnode.BufferSize = checked((uint)buffer.Length);
            value->Wnode.Flags = 0x00020000;
            value->LoggerNameOffset = (uint)size;
            value->LogFileNameOffset = (uint)(size + 2048);
            name.AsSpan().CopyTo(new Span<char>(bytes + size, 1024));
            var status = QueryControlTrace(0, name, value, 0);
            if (status == 4201) return new(false, 0, 0);
            if (status != 0) throw new Win32Exception((int)status, "Read-only query of own kernel session failed.");
            return new(true, value->EnableFlags, value->EventsLost);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct QueryWnode { public uint BufferSize, ProviderId; public ulong HistoricalContext, Timestamp; public Guid Guid; public uint ClientContext, Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct QueryTraceProperties
    {
        public QueryWnode Wnode;
        public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset, LoggerNameOffset;
    }
    [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern unsafe uint QueryControlTrace(ulong handle, string name, QueryTraceProperties* properties, uint queryOnly);
}
