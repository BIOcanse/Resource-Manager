using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Principal;
using System.Text.Json;
using FanControlCore;
using FanControlCore.Discovery;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Security;

namespace ResourceManager.ControlDecisionAcceptance;

internal static partial class Program
{
    private static async Task<int> RunActualTimingAsync(string[] args)
    {
        if (args.Length != 10 || args[0] != "--registered-root" || args[2] != "--runtime-root"
            || args[4] != "--artifact-manifest" || args[6] != "--source-manifest" || args[8] != "--label")
            throw new ArgumentException("--actual-timing --registered-root <task> --runtime-root <fresh child> --artifact-manifest <manifest> --source-manifest <manifest> --label <before|after>");
        if (args[9] is not ("before" or "after")) throw new ArgumentException("Unknown timing label.");
        if (!OperatingSystem.IsWindows() || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Run the copied timing helper in the owner's effective administrator context.");
        var registered = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
        var root = Path.GetFullPath(args[3]);
        var image = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        if (!File.Exists(Path.Combine(registered, ".generated-workspace.json"))
            || !root.StartsWith(registered + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !image.StartsWith(registered + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(image + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(root))
            throw new InvalidDataException("Image and fresh runtime must be separate children of the supplied registered task.");
        var artifacts = CopiedArtifact.Verify(args[5], args[7]);
        WindowsNativeLaunchErrorPolicy.InitializeForCurrentProcess();
        Directory.CreateDirectory(root);
        var evidence = Path.Combine(root, "evidence");
        Directory.CreateDirectory(evidence);
        Environment.SetEnvironmentVariable("RESOURCE_MANAGER_PACKAGE_ROOT", root);
        Environment.SetEnvironmentVariable("FANCONTROLCORE_STATE_DIRECTORY", Path.Combine(root, "UserData", "FanCore"));
        Directory.SetCurrentDirectory(root);
        var id = Guid.NewGuid().ToString("N");
        var pipe = $"ResourceManager.ActualTiming.{id}";
        var mutexName = $"Local\\ResourceManager.ActualTiming.{id}";
        using var mutex = new Mutex(false, mutexName);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        // Native firmware reads cannot be cancelled. The memory-limit Job owns
        // this helper; an absolute watchdog also bounds final DI disposal/drain.
        using var watchdog = new Timer(_ => Environment.Exit(124), null, TimeSpan.FromMinutes(7), Timeout.InfiniteTimeSpan);
        using var logs = new ActualTimingLogProvider();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root,
            EnvironmentName = "Development", ApplicationName = typeof(ResourceManagerServiceCollectionExtensions).Assembly.GetName().Name });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(20));
        var pipeline = LoopbackApiPipelineConfigurator.CompileForCurrentProcess();
        pipeline.ConfigureServices(builder.Services);
        builder.Services.AddResourceManagerApp([], StartupCapabilitySet.Full, BackendHostEnvironment.Interactive);
        ApplyActualTimingReadBoundaries(builder.Services, root, pipe);
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(logs);
        await using var app = builder.Build();
        var timings = new List<object>();
        var responses = new List<(string File, string Body)>();
        var failures = new List<string>();
        string? address = null;
        var stopped = false;
        object? preparation = null;
        var processStartedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        try
        {
            var settings = AppSettingsDefaults.Create();
            settings = settings with { Performance = settings.Performance with { OptimizationMode = AppOptimizationModes.Normal },
                SystemIntegration = settings.SystemIntegration with { AutoStartEnabled = false },
                Debug = new(false, false, false, false), PublicService = new(false, false, false, false),
                Update = new(AppUpdateModes.Off, false), MemoryProtection = new() { Enabled = false } };
            await app.Services.GetRequiredService<IAppSettingsStore>().SaveAsync(settings, deadline.Token);
            pipeline.ConfigureApplication(app);
            var preparationAt = DateTimeOffset.UtcNow;
            var preparationStart = Stopwatch.GetTimestamp();
            await app.StartAsync(deadline.Token);
            var hostStartedAt = DateTimeOffset.UtcNow;
            // The maintenance observation was taken against an already running
            // service. Wait for the original startup catalog subscription, never
            // issue a harness fetch, readiness GET or replacement warm-up probe.
            var catalog = app.Services.GetRequiredService<HardwareMetricCatalogState>();
            using (var preparing = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                preparing.CancelAfter(TimeSpan.FromSeconds(90));
                var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void ObserveCatalog() { if (catalog.Current is not null) published.TrySetResult(); }
                catalog.Changed += ObserveCatalog;
                try { ObserveCatalog(); await published.Task.WaitAsync(preparing.Token); }
                finally { catalog.Changed -= ObserveCatalog; }
            }
            preparation = new { startedAt = preparationAt, hostStartedAt, catalogObservedAt = DateTimeOffset.UtcNow,
                milliseconds = Stopwatch.GetElapsedTime(preparationStart).TotalMilliseconds,
                condition = "Existing startup topology publication observed; no harness metrics fetch. Individual owner preparation has no independent public completion boundary." };
            address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new InvalidOperationException("No private listener.");
            var uri = new Uri(address);
            if (uri.Host != "127.0.0.1" || uri.Port is 0 or 9321 or 9333)
                throw new InvalidDataException("Listener isolation failed.");
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(100) };
            // Deliberately no readiness request, warm-up or delay between these GETs.
            for (var index = 1; index <= 3; index++)
            {
                var at = DateTimeOffset.UtcNow;
                var start = Stopwatch.GetTimestamp();
                try
                {
                    using var response = await client.GetAsync("/api/control/actual", deadline.Token);
                    var body = await response.Content.ReadAsStringAsync(deadline.Token);
                    var completed = DateTimeOffset.UtcNow;
                    var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    responses.Add(($"get-{index}.json", body));
                    timings.Add(new { operation = $"GET-{index}", path = "/api/control/actual", startedAt = at,
                        completedAt = completed, milliseconds, statusCode = (int)response.StatusCode, bodyFile = $"get-{index}.json" });
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception error)
                {
                    failures.Add($"GET-{index}: {error.Message}");
                    if (timings.Count < index)
                        timings.Add(new { operation = $"GET-{index}", path = "/api/control/actual", startedAt = at,
                            completedAt = DateTimeOffset.UtcNow, milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                            error = error.Message });
                }
            }
            using var subscription = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            subscription.CancelAfter(TimeSpan.FromSeconds(100));
            var subscriptionAt = DateTimeOffset.UtcNow;
            var subscriptionStart = Stopwatch.GetTimestamp();
            try
            {
                // /subscribe is a selector in the real multiplexed channel, not a standalone HTTP route.
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream") {
                    Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                        new { id = "actual-timing", path = "/api/control/actual/subscribe" } } }) };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, subscription.Token);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(subscription.Token);
                using var reader = new StreamReader(stream);
                var frame = await reader.ReadLineAsync(subscription.Token) ?? throw new InvalidDataException("Channel ended before its first frame.");
                var completed = DateTimeOffset.UtcNow;
                var milliseconds = Stopwatch.GetElapsedTime(subscriptionStart).TotalMilliseconds;
                using var parsed = JsonDocument.Parse(frame);
                if (parsed.RootElement.GetProperty("subscriptionId").GetString() != "actual-timing"
                    || parsed.RootElement.GetProperty("value").TryGetProperty("error", out _))
                    throw new InvalidDataException("First channel frame was not a control actual-state value.");
                await File.WriteAllTextAsync(Path.Combine(evidence, "subscribe-first-frame.ndjson"), frame + "\n", deadline.Token);
                timings.Add(new { operation = "subscribe-first-frame", path = "/api/control/actual/subscribe",
                    transport = "POST /api/subscriptions/stream", startedAt = subscriptionAt, completedAt = completed, milliseconds });
            }
            catch (Exception error)
            {
                failures.Add($"subscribe-first-frame: {error.Message}");
                timings.Add(new { operation = "subscribe-first-frame", path = "/api/control/actual/subscribe",
                    transport = "POST /api/subscriptions/stream", startedAt = subscriptionAt, completedAt = DateTimeOffset.UtcNow,
                    milliseconds = Stopwatch.GetElapsedTime(subscriptionStart).TotalMilliseconds, error = error.Message });
            }
            finally { subscription.Cancel(); }
            foreach (var response in responses)
                await File.WriteAllTextAsync(Path.Combine(evidence, response.File), response.Body, deadline.Token);
        }
        catch (Exception error) { failures.Add(error.ToString()); }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await app.StopAsync(stop.Token); stopped = true; }
            catch (Exception error) { failures.Add("stop: " + error); }
            foreach (var response in responses)
                await File.WriteAllTextAsync(Path.Combine(evidence, response.File), response.Body);
            await File.WriteAllLinesAsync(Path.Combine(evidence, "product-logs.ndjson"), logs.Rows.Select(value => JsonSerializer.Serialize(value)));
            await SaveAsync(evidence, "actual-timing.json", new {
                contract = "resource-manager-control-actual-timing-v1", label = args[9],
                status = failures.Count == 0 && timings.Count == 4 && stopped ? "passed" : "failed",
                image, root, address, pipe, mutexName, processStartedAt, artifacts, preparation, timings, failures,
                logRows = logs.Rows.Count, logRowsDropped = logs.Dropped,
                stageAttribution = new[] {
                    "Use emitted product log timestamps only; no product state, counter or sampling instrumentation was added.",
                    "Block start and before-only sampler first sample have no guaranteed normal-path log boundary: not independently attributable unless present in product-logs.ndjson.",
                    "Fan DiscoverAsync and writer reads have no complete normal-path start/end logs: their individual durations cannot be derived from HTTP totals.",
                    "Hardware bridge executable is absent in this read-only image; its helper-start latency is not measured. processStartedAt describes this dev helper only." },
                boundaries = new[] {
                    "Real DI, native decision tree, Windows catalog, writer Probe/Read and OEM FanCore discovery/read.",
                    "Control writes/restores, process mutations, GPU/registry actions and HMP effects denied by development adapters.",
                    "Normal Full startup capability and original startup catalog preparation are retained. Only runtime specialization, sampling subscription lifetime, metrics, actual reader and fan lifetime hosted aliases admitted; unrelated hosted background work is excluded.",
                    "FanCore uses real OEM/WMI routes behind a read-only backend adapter. SuperIO/PawnIO/driver routes excluded. Fresh private baseline/journal directory.",
                    "General metrics uses existing DriverFreeMetrics adapters: no driver, MSR, SMU, SMN, HWM or OEM metric providers; driver-backed values may be unavailable.",
                    "Private GUID launch mutex/pipe; OEM EC channel synchronization remains the library's machine-wide hardware lock." }, stopped });
        }
        return failures.Count == 0 && timings.Count == 4 && stopped ? 0 : 1;
    }

    private static object CreateTimingService(ServiceDescriptor descriptor, IServiceProvider provider)
        => descriptor.ImplementationInstance ?? descriptor.ImplementationFactory?.Invoke(provider)
            ?? ActivatorUtilities.GetServiceOrCreateInstance(provider, descriptor.ImplementationType
                ?? throw new InvalidOperationException("Unsupported keyed timing descriptor."));

    private static void ApplyActualTimingReadBoundaries(IServiceCollection services, string root, string pipe)
    {
        var originals = services.Where(value => value.ServiceType == typeof(IControlWriter)).ToArray();
        EffectBoundaries.Apply(services, root, pipe);
        services.AddSingleton(provider => new ActualTimingWriters(originals.Select(value =>
            (Writer: (IControlWriter)CreateTimingService(value, provider), Owned: value.ImplementationType is not null)).ToArray()));
        services.RemoveAll<IControlWriter>();
        for (var index = 0; index < originals.Length; index++)
        {
            var slot = index;
            services.AddSingleton<IControlWriter>(provider => new ActualTimingReadOnlyWriter(provider.GetRequiredService<ActualTimingWriters>().Values[slot]));
        }
        // The production detection-cache factory searches concrete writer types in the enumerable.
        services.RemoveAll<IControlDetectionCache>();
        services.AddSingleton<IControlDetectionCache>(provider => provider.GetRequiredService<WindowsChassisKindReader>());
        services.AddSingleton<IControlDetectionCache>(provider => provider.GetRequiredService<FanControlCoreClient>());
        services.AddSingleton<IControlDetectionCache>(provider => provider.GetRequiredService<ActualTimingWriters>().Values.OfType<NvidiaGpuControlWriter>().Single());
        services.Replace(ServiceDescriptor.Singleton<FanControlCoreClient>(provider => new FanControlCoreClient(
            provider.GetRequiredService<IHostEnvironment>(), (state, library) => new ActualTimingFanSession(state, library),
            provider.GetRequiredService<ILogger<FanControlCoreClient>>())));
        services.Replace(ServiceDescriptor.Singleton<IControlObjectCatalog, WindowsControlObjectCatalog>());
        var hosted = services.Select(value => value.ImplementationInstance).OfType<BusinessBlockRegistration>()
            .Where(value => value.HostedServiceType?.Name is "RuntimeSpecializationCoordinator" or "NativeEtwLifetimeService"
                or "HostManagerSamplingSubscriptionOwner" or "WindowsHardwareMetricSampler" or "ControlActualStateReader" or "FanControlCoreLifetimeService").ToArray();
        services.RemoveAll<IHostedService>();
        services.AddSingleton<IHostedService, BusinessBlockDependencyLifetime>();
        // Keep ALL original HostedBusinessBlockAlias declarations: excluded blocks
        // must retain a false host permit. Never construct an excluded alias/service.
        foreach (var declaration in hosted)
            services.AddSingleton<IHostedService>(provider => (IHostedService)(Activator.CreateInstance(
                typeof(NonOwningHostedService<>).MakeGenericType(declaration.HostedServiceType!),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                binder: null, args: [provider, declaration.Id, false], culture: null)
                ?? throw new InvalidOperationException("Could not create the original hosted alias.")));
    }

    private sealed class ActualTimingWriters((IControlWriter Writer, bool Owned)[] entries) : IDisposable
    {
        internal IReadOnlyList<IControlWriter> Values { get; } = entries.Select(value => value.Writer).ToArray();
        public void Dispose()
        {
            foreach (var entry in entries)
                if (entry.Owned && entry.Writer is IDisposable disposable) disposable.Dispose();
        }
    }
    private sealed class ActualTimingReadOnlyWriter(IControlWriter real) : IControlWriter
    {
        public string? ChannelOf(ControlObject target, string capabilityId) => real.ChannelOf(target, capabilityId);
        public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability) => real.Probe(target, capability);
        public Task<ControlActualValue?> ReadAsync(ControlObject target, ControlCapability capability, CancellationToken token) => real.ReadAsync(target, capability, token);
        public Task<IReadOnlyList<ControlCurvePoint>?> ReadCurveAsync(ControlObject target, CancellationToken token) => real.ReadCurveAsync(target, token);
        public Task<ControlApplyOutcome> WriteAsync(ControlObject target, ControlCapability capability, ControlSetting setting, CancellationToken token)
            => throw new InvalidOperationException(EffectBoundaries.Reason);
        public Task<bool> RestoreAsync(ControlObject target, ControlCapability capability, IReadOnlySet<string> configured, CancellationToken token, ControlSetting? previousSetting = null)
            => throw new InvalidOperationException(EffectBoundaries.Reason);
    }

    private sealed class ActualTimingFanSession : IFanControlCoreSession
    {
        private readonly FanControlSession session;
        internal ActualTimingFanSession(string state, string? library)
        {
            // The entire package root was created fresh by this invocation.
            // Recovery/release calls from the library still cross the denying backend.
            var options = new FanCoreOptions(state, library);
            var routes = FanChannelRoute.For(FanPlatform.Windows, options).Where(route => route.Category != FanChannelCategory.SuperIo);
            session = new FanControlSession(new FanCore(routes.Select(route => new ActualTimingFanBackend(route.Create())), options));
        }
        public JsonElement Handle(JsonElement request)
        {
            if (request.GetProperty("op").GetString() is not ("describe" or "read" or "curve"))
                throw new InvalidOperationException(EffectBoundaries.Reason);
            return session.Handle(request);
        }
        public void PushTemperatures(IReadOnlyList<FanTemperatureSample> samples) { }
        public IReadOnlyList<string> SoftwareCurveTemperatureKinds => [];
        public event Action<IReadOnlyList<string>>? SoftwareCurveTemperatureKindsChanged { add { } remove { } }
        public bool SoftwareCurveRunNeeded => false;
        public event Action<bool>? SoftwareCurveRunNeededChanged { add { } remove { } }
        public Task RunSoftwareCurveAsync(CancellationToken stop) => Task.Delay(Timeout.Infinite, stop);
        public void SetDiagnosticSink(Action<string> sink) => session.SetDiagnosticSink(sink);
        public string? InFlightOperation => session.InFlightOperation;
        public void Dispose() => session.Dispose();
    }

    private sealed class ActualTimingFanBackend(IFanBackend real) : IFanBackend
    {
        public string Name => real.Name;
        public IReadOnlyList<string> Diagnostics => real.Diagnostics;
        public string RecoveryIdentity(int index) => real.RecoveryIdentity(index);
        public bool TryOpen(out string? reason) => real.TryOpen(out reason);
        public bool TryOpen(out FanOpenFailureKind kind, out string? reason) => real.TryOpen(out kind, out reason);
        public void RefreshDetection() => real.RefreshDetection();
        public IReadOnlyList<FanDescription> Describe() => real.Describe();
        public IReadOnlyList<FanState> Read() => real.Read();
        public IReadOnlyList<FanCurvePoint>? ReadCurve(int index) => real.ReadCurve(index);
        public bool HasPendingTemporaryRestore(int index) => real.HasPendingTemporaryRestore(index);
        private static bool Deny(out string? reason) { reason = EffectBoundaries.Reason; return false; }
        public bool TryWriteCurve(int index, IReadOnlyList<FanCurvePoint> curve, out string? reason) => Deny(out reason);
        public bool TryDriveDuty(int index, double duty, out string? reason) => Deny(out reason);
        public bool TrySetDuty(int index, double duty, out string? reason) => Deny(out reason);
        public bool TrySetFullSpeed(int index, out string? reason) => Deny(out reason);
        public bool TrySetAutomatic(int index, out string? reason) => Deny(out reason);
        public bool TryResetFirmwareCurve(int index, out string? reason) => Deny(out reason);
        public bool TryReleaseOverride(int index, out string? reason) => Deny(out reason);
        public void Dispose() => real.Dispose();
    }

    private sealed record ActualTimingLog(DateTimeOffset At, long MonotonicTicks, string Category, string Level, int EventId, string Message, string? Error);
    private sealed class ActualTimingLogProvider : ILoggerProvider
    {
        internal readonly ConcurrentQueue<ActualTimingLog> Rows = new();
        private int accepted;
        internal int Dropped;
        public ILogger CreateLogger(string category) => new ActualTimingLogger(this, category);
        public void Dispose() { }
        private sealed class ActualTimingLogger(ActualTimingLogProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter)
            {
                if (Interlocked.Increment(ref owner.accepted) > 20000) { Interlocked.Increment(ref owner.Dropped); return; }
                owner.Rows.Enqueue(new(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), category, level.ToString(), eventId.Id, formatter(state, error), error?.ToString()));
            }
        }
    }
}
