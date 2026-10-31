using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Security.Principal;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.StateItems;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace ResourceManager.ControlDecisionAcceptance;

internal static partial class Program
{
    private static async Task<int> RunPresentObservationAsync(string[] args)
    {
        if (args.Length != 8 || args[0] != "--registered-root" || args[2] != "--runtime-root"
            || args[4] != "--artifact-manifest" || args[6] != "--source-manifest")
            throw new ArgumentException("--present-observation requires registered-root, runtime-root, artifact-manifest and source-manifest.");
        if (!OperatingSystem.IsWindows() || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Use the existing elevated control session.");
        var registered = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
        var root = Path.GetFullPath(args[3]);
        var image = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        if (!registered.StartsWith("F:\\", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(registered, ".generated-workspace.json"))
            || !root.StartsWith(registered + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !image.StartsWith(registered + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(image + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(root))
            throw new InvalidDataException("Use a bound copy and a fresh separate runtime child of the registered task.");
        var artifacts = CopiedArtifact.Verify(args[5], args[7]);
        WindowsNativeLaunchErrorPolicy.InitializeForCurrentProcess();
        Directory.CreateDirectory(root);
        var evidence = Path.Combine(root, "evidence");
        Directory.CreateDirectory(evidence);
        Environment.SetEnvironmentVariable("RESOURCE_MANAGER_PACKAGE_ROOT", root);
        Directory.SetCurrentDirectory(root);
        var transitions = new ConcurrentQueue<object>();
        var closed = new ConcurrentDictionary<(string Name, long Generation), (DateTimeOffset At, EtwSessionEventDrops Drops)>();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root,
            EnvironmentName = "Development", ApplicationName = typeof(ResourceManagerServiceCollectionExtensions).Assembly.GetName().Name });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var pipeline = LoopbackApiPipelineConfigurator.CompileForCurrentProcess();
        pipeline.ConfigureServices(builder.Services);
        builder.Services.AddResourceManagerApp([], StartupCapabilitySet.Full, BackendHostEnvironment.Interactive);
        EffectBoundaries.Apply(builder.Services, root, $"ResourceManager.PresentObservation.{Environment.ProcessId}.{Guid.NewGuid():N}");
        // Keep admission metadata (default false for hosted aliases), but omit
        // these unrelated host adapters: their StartAsync would grant admission.
        var excluded = new HashSet<int> { BlockUids.PortableSoftwareDiscoveryService,
            BlockUids.HostManagerSmartCoordinator, BlockUids.HostManagerReportCoordinatorOwner };
        for (var index = builder.Services.Count - 2; index >= 0; index--)
            if (builder.Services[index].ImplementationInstance is HostedBusinessBlockAlias alias && excluded.Contains(alias.Id))
            {
                if (builder.Services[index + 1].ServiceType != typeof(IHostedService))
                    throw new InvalidDataException("Hosted alias admission registration changed.");
                builder.Services.RemoveAt(index + 1);
            }
        // This probe measures Present only. Even an unexpected unrelated reader
        // cannot create/reconcile a kernel session through the copied host.
        builder.Services.Replace(ServiceDescriptor.Singleton<KernelEtwSessionBroker>(provider => new(
            new PresentOnlyKernelFactory(), provider.GetRequiredService<ILogger<KernelEtwSessionBroker>>())));
        builder.Services.Replace(ServiceDescriptor.Singleton<PresentFrameEtwSource>(provider => new(
            provider.GetRequiredService<ILogger<PresentFrameEtwSource>>(), provider.GetRequiredService<BusinessBlockScheduler>(),
            provider.GetRequiredService<BusinessBlockDiagnosticsRegistry>(), provider.GetRequiredService<NativeEtwSessionAvailability>(),
            name =>
            {
                RequireOwnSessionName(ResourceManagerEtwSessionNames.PresentFramesPrefix, name);
                return new PresentMeasurementSession(name, ReadPublishedMonitoring(provider).PresentSessionBuffers,
                    provider.GetRequiredService<ILogger<PresentFrameEtwSource>>(), transitions, closed,
                    () => provider.GetRequiredService<PresentFrameEtwSource>().ReadHolders());
            }, TimeProvider.System, provider.GetService<IEtwConsumerThreadObserver>())));
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();
        await using var app = builder.Build();
        var settings = ResourceManager.App.Application.Settings.AppSettingsDefaults.Create();
        settings = settings with { Performance = settings.Performance with { OptimizationMode = AppOptimizationModes.Normal },
            SystemIntegration = settings.SystemIntegration with { AutoStartEnabled = false },
            Debug = new(false, false, false, false), PublicService = new(false, false, false, false),
            Update = new(AppUpdateModes.Off, false), MemoryProtection = new() { Enabled = false } };
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var permits = app.Services.GetRequiredService<BackendHostPermitFacts>();
        foreach (var block in new[] { BlockUids.PortableSoftwareDiscoveryService, BlockUids.HostManagerSmartCoordinator,
            BlockUids.HostManagerReportCoordinatorOwner, BlockUids.GpuAllocationCollector })
            await permits.SetAsync(block, false, new(ResourceManager.Shared.BusinessBlocks.BusinessBlockRequestKind.Configuration,
                "present-measurement", "unrelated-consumer-excluded", 0));
        await app.Services.GetRequiredService<IAppSettingsStore>().SaveAsync(settings, deadline.Token);
        pipeline.ConfigureApplication(app);
        app.MapEventDropsDebugEndpoint();
        await app.StartAsync(deadline.Token);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        if (new Uri(address).Port is 0 or 9321 or 9333) throw new InvalidDataException("Measurement listener must be private.");
        var sessions = new Dictionary<(string Name, long Generation), EtwSessionEventDrops>();
        var elapsed = Stopwatch.StartNew();
        var measuredSeconds = 0d;
        var finishedAt = DateTimeOffset.MaxValue;
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(address) };
            // This named measurement consumer requests the GPU facts it measures.
            // The production observer itself remains a passive publication reader.
            using var facts = app.Services.GetRequiredService<ISchedulingProcessFactObservationSource>().AcquireSubscription(
                "present-measurement-gpu-facts", SchedulingProcessMetricMask.GpuUsage, TimeSpan.FromSeconds(5));
            while (elapsed.Elapsed < TimeSpan.FromMinutes(10))
            {
                var drops = await client.GetFromJsonAsync<EtwEventDropsSnapshot>("/api/debug/event-drops", deadline.Token)
                    ?? throw new InvalidDataException("No drop snapshot.");
                foreach (var session in drops.Sessions.Where(item => item.Name.StartsWith(ResourceManagerEtwSessionNames.PresentFramesPrefix, StringComparison.Ordinal)))
                    sessions[(session.Name, session.Generation)] = session;
                await File.AppendAllTextAsync(Path.Combine(evidence, "present-event-drops.jsonl"),
                    JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, seconds = elapsed.Elapsed.TotalSeconds,
                        presentState = app.Services.GetRequiredService<BusinessBlockScheduler>().ReadState(BlockUids.PresentFrameEtwSource).ToString(),
                        holders = app.Services.GetRequiredService<PresentFrameEtwSource>().ReadHolders(), drops }, JsonSerializerOptions.Web) + "\n", deadline.Token);
                await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
            }
            var final = await client.GetFromJsonAsync<EtwEventDropsSnapshot>("/api/debug/event-drops", deadline.Token)
                ?? throw new InvalidDataException("No final drop snapshot.");
            foreach (var session in final.Sessions.Where(item => item.Name.StartsWith(ResourceManagerEtwSessionNames.PresentFramesPrefix, StringComparison.Ordinal)))
                sessions[(session.Name, session.Generation)] = session;
            measuredSeconds = elapsed.Elapsed.TotalSeconds;
            finishedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            try { await app.StopAsync(CancellationToken.None); }
            finally { await SaveAsync(evidence, "present-session-transitions.json", transitions.ToArray()); }
        }
        // The endpoint retains only the newest generation for a reused name.
        // Preserve its same diagnostic snapshot on close so a short generation
        // between HTTP polls cannot disappear from the rate denominator/counts.
        foreach (var entry in closed.Where(item => item.Value.At <= finishedAt)) sessions[entry.Key] = entry.Value.Drops;
        var received = sessions.Values.Sum(session => session.Kinds.Sum(kind => kind.Received));
        var dropped = sessions.Values.Sum(session => session.Kinds.Sum(kind => kind.Dropped));
        await SaveAsync(evidence, "present-observation.json", new { status = "passed", measuredSeconds,
            address, artifacts, received, dropped, droppedPerSecond = dropped / measuredSeconds,
            droppedFraction = received == 0 ? (double?)null : (double)dropped / received,
            rateSource = "/api/debug/event-drops plus final snapshots of the same counters on close, before their session name is reused",
            gpuFactSubscription = "present-measurement-gpu-facts at 5 seconds; observer holds no sampling subscription",
            workload = "Run with the existing browser and game launcher; no process is launched by the probe.",
            sessions = sessions.Values.ToArray() });
        return 0;
    }

    private sealed class PresentMeasurementSession : IPresentFrameEtwSession
    {
        private readonly TraceEventPresentFrameEtwSession inner;
        private readonly string name;
        private readonly ConcurrentQueue<object> transitions;
        private readonly ConcurrentDictionary<(string Name, long Generation), (DateTimeOffset At, EtwSessionEventDrops Drops)> closed;
        private readonly Func<PresentFrameEtwSource.PresentHolder[]> readHolders;
        private int opened;
        internal PresentMeasurementSession(string name, EtwSessionBuffers buffers, ILogger logger, ConcurrentQueue<object> transitions,
            ConcurrentDictionary<(string Name, long Generation), (DateTimeOffset At, EtwSessionEventDrops Drops)> closed,
            Func<PresentFrameEtwSource.PresentHolder[]> readHolders)
        {
            this.name = name;
            this.transitions = transitions;
            this.closed = closed;
            this.readHolders = readHolders;
            inner = new(name, buffers, logger);
        }
        public int EventsLost => inner.EventsLost;
        public void Bind(Action<TraceEvent> handler) => inner.Bind(handler);
        public void BindUsage(Func<TraceEvent, bool> handler) => inner.BindUsage(handler);
        public void Process() => inner.Process();
        public void EnableProvider(Guid provider, TraceEventLevel level, ulong keywords, int[] ids, int[]? pids = null)
        {
            inner.EnableProvider(provider, level, keywords, ids, pids);
            var generation = EtwEventDropDiagnostics.Read().Sessions.Single(item => item.Name == name).Generation;
            transitions.Enqueue(new { at = DateTimeOffset.UtcNow, name, generation, transition = "enable",
                provider, ids, pids, holders = readHolders() });
            if (Interlocked.Exchange(ref opened, 1) == 0)
                transitions.Enqueue(new { at = DateTimeOffset.UtcNow, name, generation, transition = "open", holders = readHolders() });
        }
        public void DisableProvider(Guid provider) => inner.DisableProvider(provider);
        public void Flush() => inner.Flush();
        public void Dispose()
        {
            inner.Dispose();
            if (Volatile.Read(ref opened) != 0)
            {
                var at = DateTimeOffset.UtcNow;
                var drops = EtwEventDropDiagnostics.Read().Sessions.Single(item => item.Name == name);
                closed[(name, drops.Generation)] = (at, drops);
                transitions.Enqueue(new { at, name, generation = drops.Generation, transition = "close", holders = readHolders(), drops });
            }
        }
    }

    private sealed class PresentOnlyKernelFactory : IKernelEtwSessionRuntimeFactory
    {
        public bool TryGetAvailability(out string state, out string message)
        { state = "Unavailable"; message = "Kernel ETW is excluded from the Present-only measurement."; return false; }
        public IKernelEtwSessionRuntime Create(string name, KernelTraceEventParser.Keywords keywords,
            KernelEtwSessionContext context, IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions)
            => throw new InvalidOperationException("Kernel ETW is excluded from the Present-only measurement.");
    }
}
