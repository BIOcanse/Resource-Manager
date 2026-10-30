using Resource_Manager_APP.Tests.KernelEtwFixtures;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed class ThreadCpuDiagnosticsTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task ThreadDemandResumesPausedResidencyAndWaitsForAFreshWindow()
    {
        using var target = new NamedThread();
        using var process = Process.GetCurrentProcess();
        var birth = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        using var reader = Reader(sampling, broker);
        using var slow = reader.AcquireSubscription("ordinary.paused", TimeSpan.FromSeconds(30));
        await reader.StartAsync(default);
        try
        {
            var first = await FeedPreparedWindowAsync(reader, sampling, process.Id, birth, target.Id, false);
            await Until(() => reader.Read() is not null && Owner(reader).State == "Idle");
            var previous = reader.Read()!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var capture = reader.CaptureThreadWindowAsync("paused", process.Id, birth, timeout.Token);
            await Until(() => Owner(reader).DiagnosticRequestCount == 1);
            await FeedPreparedWindowAsync(reader, sampling, process.Id, birth, target.Id, false,
                previousWindow: first.Window, previousEnd: first.End);
            var result = Assert.IsType<CpuProcessThreadWindow>(await capture);
            Assert.True(result.MeasuredFrom > previous.MeasuredFrom);
            Assert.Equal(process.Id, result.Process.ProcessId);
            Assert.Equal(NamedThread.Description, Assert.Single(result.Process.Threads).Diagnostic!.Description);
            Assert.Equal(0, Owner(reader).DiagnosticRequestCount);
            await Until(() => !Owner(reader).IsWindowActive);
            Assert.Equal(3, broker.Acquires);
            Assert.Equal(2, broker.Releases);
        }
        finally { await reader.StopAsync(default); }
        Assert.Equal(3, broker.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestedProcessHasNamedThreadPartialExitAndMatchingExecutionTotalThroughDebugEndpoint(bool initiallyPaused)
    {
        using var target = new NamedThread();
        using var survivor = new NamedThread();
        using var process = Process.GetCurrentProcess();
        var birth = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        using var reader = Reader(sampling, broker);
        using var slow = initiallyPaused ? reader.AcquireSubscription("endpoint.paused", TimeSpan.FromSeconds(30)) : null;
        await using var app = BuildDebugApp(reader);
        await app.StartAsync();
        using var client = DebugClient(app, TimeSpan.FromSeconds(20));
        try
        {
            (object? Window, long End) first = (null, 0);
            if (initiallyPaused)
            {
                await reader.StartAsync(default);
                first = await FeedPreparedWindowAsync(reader, sampling, process.Id, birth, target.Id,
                    exited: true, secondTid: survivor.Id);
                await Until(() => reader.Read() is not null && Owner(reader).State == "Idle");
            }
            var pendingResponse = client.GetAsync($"/api/debug/thread-cpu?processId={process.Id}&processStartKey={birth}");
            await Until(() => Owner(reader)?.DiagnosticRequestCount == 1);
            if (!initiallyPaused) await reader.StartAsync(default);
            await FeedPreparedWindowAsync(reader, sampling, process.Id, birth, target.Id, exited: true,
                secondTid: survivor.Id, duringWindow: target.BurnAndExit,
                previousWindow: first.Window, previousEnd: first.End);
            using var response = await pendingResponse;
            response.EnsureSuccessStatusCode();
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<CpuProcessThreadWindow>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.NotNull(result);
            Assert.Equal(2, result.Process.Threads.Count);
            var row = Assert.Single(result.Process.Threads, thread => thread.ThreadId == target.Id);
            Assert.Equal(NamedThread.Description, Assert.Single(result.Process.Threads, thread => thread.ThreadId == survivor.Id).Diagnostic!.Description);
            Assert.Equal(target.Id, row.ThreadId);
            Assert.InRange(Math.Abs(result.Process.Threads.Sum(thread => thread.ExecutionTimeMilliseconds)
                - result.Process.ExecutionTimeMilliseconds), 0, result.Process.Threads.Count * 0.001);
            Assert.InRange(row.ExecutionTimeMilliseconds, 0.001, (result.MeasuredThrough - result.MeasuredFrom).TotalMilliseconds / 2);
            Assert.Equal(NamedThread.Description, row.Diagnostic!.Description);
            Assert.True(row.Diagnostic.CreationFileTime100Ns > 0);
            Assert.NotNull(row.Diagnostic.StartAddress);
            Assert.NotNull(row.Diagnostic.StartModulePath);
            var counters = Assert.IsType<CpuThreadCounterDelta>(row.Diagnostic.Counters);
            Assert.True(counters.UserTime100Ns + counters.KernelTime100Ns > 0);
            Assert.True(counters.ReadThrough >= counters.ReadFrom);
            Assert.False(target.IsAlive);
            await Until(() => Owner(reader).DiagnosticRequestCount == 0);
            if (initiallyPaused) await Until(() => !Owner(reader).IsWindowActive);
            Assert.Equal(initiallyPaused ? 3 : 2, broker.Acquires);
            Assert.Equal(initiallyPaused ? 2 : 0, broker.Releases);
            var evidence = Environment.GetEnvironmentVariable("RM_THREAD_CPU_EXAMPLE_PATH");
            if (!string.IsNullOrWhiteSpace(evidence)) await File.WriteAllTextAsync(evidence, json);
        }
        finally { await reader.StopAsync(default); }
        Assert.Equal(initiallyPaused ? 3 : 2, broker.Releases);
    }

    [Fact]
    public async Task DebugTimeoutReportsPublishedWindowsThatNeverBecomeFresh()
    {
        using var process = Process.GetCurrentProcess();
        var birth = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        using var reader = Reader(sampling, broker);
        using var slow = reader.AcquireSubscription("timeout.paused", TimeSpan.FromSeconds(30));
        await using var app = BuildDebugApp(reader);
        await app.StartAsync();
        using var client = DebugClient(app, TimeSpan.FromSeconds(40));
        await reader.StartAsync(default);
        try
        {
            var first = await FeedPreparedWindowAsync(reader, sampling, process.Id, birth, 10, false,
                timestampOffset: -TimeSpan.FromMinutes(2));
            await Until(() => reader.Read() is not null && Owner(reader).State == "Idle");
            var pendingResponse = client.GetAsync($"/api/debug/thread-cpu?processId={process.Id}&processStartKey={birth}");
            await Until(() => Owner(reader).DiagnosticRequestCount == 1);
            await FeedPreparedWindowAsync(reader, sampling, process.Id, birth, 10, false,
                timestampOffset: -TimeSpan.FromMinutes(2), previousWindow: first.Window, previousEnd: first.End);
            using var response = await pendingResponse;
            Assert.Equal(System.Net.HttpStatusCode.GatewayTimeout, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("thread-cpu-window-timeout", json.RootElement.GetProperty("error").GetString());
            var wait = json.RootElement.GetProperty("wait");
            Assert.Equal("fresh-closed-window", wait.GetProperty("waitingFor").GetString());
            Assert.True(wait.GetProperty("publicationsObserved").GetInt32() > 1);
            Assert.True(wait.GetProperty("lastWindow").GetProperty("measuredFrom").GetDateTimeOffset()
                < wait.GetProperty("requestedAt").GetDateTimeOffset());
            Assert.True(wait.GetProperty("revisionAtCancellation").GetInt64()
                > wait.GetProperty("revisionAtRequest").GetInt64());
            Assert.True(wait.GetProperty("hasSessionLease").GetBoolean());
            Assert.Equal("Running", wait.GetProperty("residency").GetProperty("state").GetString());
            Assert.Equal(0, Owner(reader).DiagnosticRequestCount);
            await Until(() => !Owner(reader).IsWindowActive);
            Assert.Equal(3, broker.Acquires);
            Assert.Equal(2, broker.Releases);
        }
        finally { await reader.StopAsync(default); }
        Assert.Equal(3, broker.Releases);
    }

    private static WebApplication BuildDebugApp(EtwCpuCoreResidencyReader reader)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.NormalReadOnly);
        TrustedAccessEndpointTestAuthentication.Add(builder.Services, "processes.read");
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.AddSingleton(reader);
        var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod("MapDebugEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [app]);
        return app;
    }

    private static HttpClient DebugClient(WebApplication app, TimeSpan timeout)
        => TrustedAccessEndpointTestAuthentication.Apply(new HttpClient
        {
            BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)),
            Timeout = timeout
        }, app.Services);

    [Fact]
    public async Task AHandleExitedBeforeTheWindowCannotEnrichLaterExecutionUsingTheSameThreadId()
    {
        using var target = new NamedThread();
        using var process = Process.GetCurrentProcess();
        var birth = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        using var reader = Reader(sampling, broker);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var capture = reader.CaptureThreadWindowAsync("reused-tid", process.Id, birth, timeout.Token);
        await reader.StartAsync(default);
        try
        {
            // Native registration runs on the existing flow. Retain the real
            // handle while its thread is alive, before testing its later exit.
            await Until(() => Owner(reader).IsWindowActive);
            var owner = Owner(reader);
            var resourceGate = typeof(EtwCpuResidencyCaptureOwner).GetField("durationResourceGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
            lock (resourceGate) broker.Resource.Publish(broker.Resource.Tick);
            target.BurnAndExit();
            // The retained native instance has ended. Later replayed execution under this numeric TID is not it.
            await FeedPreparedWindowAsync(reader, sampling, process.Id, birth, target.Id, false);
            var result = Assert.IsType<CpuProcessThreadWindow>(await capture);
            var row = Assert.Single(result.Process.Threads);
            Assert.True(row.ExecutionTimeMilliseconds > 0);
            Assert.Null(row.Diagnostic);
        }
        finally { await reader.StopAsync(default); }
    }
    [Fact]
    public async Task OrdinaryResidencyDemandDoesNoThreadDiagnosticNativeWork()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        using var reader = Reader(sampling, broker);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = reader.SubscribeAsync("ordinary", TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator();
        var next = stream.MoveNextAsync().AsTask();
        await reader.StartAsync(default);
        try
        {
            await FeedPreparedWindowAsync(reader, sampling, 100, 1000, 10, false);
            Assert.True(await next);
            Assert.NotNull(stream.Current);
            broker.Resource.Publish(broker.Resource.Tick);
            var usage = broker.Resource.DiagnosticUsage();
            Assert.Equal(0UL, usage.NativeReads); Assert.Equal(0UL, usage.OpenHandles);
            Assert.Equal(0, Owner(reader).DiagnosticRequestCount);

        }
        finally { await reader.StopAsync(default); }
    }

    [Fact]
    public async Task CancellationReleasesItsOwnRequestWithoutStartingAnotherSampler()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        using var reader = Reader(sampling, broker);
        using var process = Process.GetCurrentProcess();
        using var cancellation = new CancellationTokenSource();
        var capture = reader.CaptureThreadWindowAsync("cancel", process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc(), cancellation.Token);
        Assert.Equal(1, Owner(reader).DiagnosticRequestCount);
        cancellation.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        var wait = Assert.IsType<ThreadCpuWindowWaitCanceledException>(canceled).Diagnostics;
        Assert.Equal("fresh-closed-window", wait.WaitingFor);
        Assert.Equal(0, wait.PublicationsObserved);
        Assert.Null(wait.LastWindow);
        Assert.Null(wait.WorkerStatus);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.Equal(0, Owner(reader).DiagnosticRequestCount);
        Assert.Equal(0, broker.Acquires);
    }

    [Fact]
    public async Task ReusedProcessIdentityNeverReceivesCurrentThreadMetadata()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        using var reader = Reader(sampling, broker);
        using var process = Process.GetCurrentProcess();
        var birth = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var capture = reader.CaptureThreadWindowAsync("wrong-birth", process.Id, birth + 1, timeout.Token);
        await reader.StartAsync(default);
        try
        {
            await FeedPreparedWindowAsync(reader, sampling, process.Id, birth, 10, false);
            Assert.Null(await capture); Assert.Equal(0, Owner(reader).DiagnosticRequestCount);
        }
        finally { await reader.StopAsync(default); }
    }

    [Fact]
    public async Task FailedCaptureReturnsEmptyAndReleasesDemand()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        using var reader = Reader(sampling, broker);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var capture = reader.CaptureThreadWindowAsync("failed", 123, 123, timeout.Token);
        await reader.StartAsync(default);
        try
        {
            // Failing metadata Acquire itself is not this scenario: the later
            // duration Acquire may legitimately reconcile and replace that run.
            // Fail the current held flow after both real holds have been installed.
            await Until(() => Owner(reader).IsWindowActive && broker.DurationAcquires == 1);
            var owner = Owner(reader);
            var resourceGate = typeof(EtwCpuResidencyCaptureOwner).GetField("durationResourceGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
            long generation;
            lock (resourceGate)
            {
                Assert.Equal(1, broker.Resource.Creations);
                Assert.Equal(2, broker.Acquires);
                Assert.True(broker.GetSnapshot().IsRunning);
                generation = broker.Resource.Generation;
                broker.Resource.Fail(3);
                var failed = broker.GetSnapshot();
                Assert.False(failed.IsRunning);
                Assert.Equal(generation, failed.Generation);
                Assert.Equal(1, broker.Resource.Creations);
            }
            Assert.Null(await capture);
            Assert.Equal(0, Owner(reader).DiagnosticRequestCount);
            Assert.Equal(generation, broker.Resource.Generation);
            Assert.Equal(1, broker.Resource.Creations);
        }
        catch
        {
            // Distinguish a failed current flow from broker recovery during the
            // second (duration-keyword) acquire before changing product behavior.
            output.WriteLine($"failed capture fixture creations={broker.Resource.Creations}; stops={broker.Resource.Stops}; "
                + $"metadataAcquires={broker.MetadataAcquires}; durationAcquires={broker.DurationAcquires}; "
                + $"broker={broker.GetSnapshot()}; worker={reader.ExecuteTask?.Status}; workerError={reader.ExecuteTask?.Exception}; "
                + $"captureState={Owner(reader).State}; captureMessage={Owner(reader).Message}.");
            throw;
        }
        finally { await reader.StopAsync(default); }
    }

    private static EtwCpuCoreResidencyReader Reader(HostManagerSamplingSubscriptionTestFixture sampling, Broker broker)
        => new(broker, sampling.Provider, sampling.Owner, NullLogger<EtwCpuCoreResidencyReader>.Instance);
    private static EtwCpuResidencyCaptureOwner Owner(EtwCpuCoreResidencyReader reader)
        => (EtwCpuResidencyCaptureOwner)typeof(EtwCpuCoreResidencyReader).GetField("captureOwner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    private static async Task<(object Window, long End)> FeedPreparedWindowAsync(
        EtwCpuCoreResidencyReader reader, HostManagerSamplingSubscriptionTestFixture sampling,
        int pid, long birth, int tid, bool exited, int secondTid = 0, Action? duringWindow = null,
        TimeSpan timestampOffset = default, object? previousWindow = null, long previousEnd = 0)
    {
        object? window = null;
        await Until(() =>
        {
            var owner = Owner(reader);
            if (owner is null || !owner.IsWindowActive) return false;
            if (typeof(EtwCpuResidencyCaptureOwner).GetField("durationSubscription",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner) is null) return false;
            var session = typeof(EtwCpuResidencyCaptureOwner)
                .GetField("activeSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
            window = session.GetType().GetProperty("Window")!.GetValue(session)!;
            return !ReferenceEquals(window, previousWindow);
        });
        var end = FeedWindow(reader, sampling, pid, birth, tid, exited, secondTid, duringWindow,
            timestampOffset, previousEnd);
        // This controlled fixture owns its consumer explicitly. Pump only the
        // already requested cold diagnostic operation to its acknowledged bank;
        // no test worker or hidden production sampling path is introduced.
        if (timestampOffset == TimeSpan.Zero)
        {
            var native = NativeCpuReaderTestFeed.Resource(Owner(reader));
            for (var round = 0; round < 1000 && Owner(reader).DiagnosticRequestCount != 0; round++)
            {
                native.Publish(native.Tick + 1);
                await Task.Delay(5);
            }
        }
        return (window!, end);
    }

    private static long FeedWindow(EtwCpuCoreResidencyReader reader, HostManagerSamplingSubscriptionTestFixture sampling,
        int pid, long birth, int tid, bool exited, int secondTid = 0, Action? duringWindow = null,
        TimeSpan timestampOffset = default, long previousEnd = 0)
    {
        var owner = Owner(reader);
        var native = NativeCpuReaderTestFeed.Resource(owner);
        var resourceGate = typeof(EtwCpuResidencyCaptureOwner).GetField("durationResourceGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        lock (resourceGate)
        {
            native.Publish(native.Tick);
            var plan = sampling.Provider.Current.HostManager.CpuCoreResidency;
            var processors = plan.PhysicalCoreByLogicalProcessor.Keys.Order().ToArray();
            var start = Math.Max(Stopwatch.GetTimestamp(), checked(previousEnd + 1));
            var now = DateTimeOffset.UtcNow + timestampOffset;
            var duration = Stopwatch.Frequency * plan.ObservationWindowMilliseconds / 1000;
            native.ProcessStart(start, now, pid, (ulong)pid, birth, "controlled-thread-cpu");
            native.ThreadStart(start, now, pid, tid);
            if (secondTid != 0) native.ThreadStart(start, now, pid, secondTid);
            for (var raw = 0; raw < processors.Length; raw++)
                native.CSwitch(start, now, raw, 0, 0, raw == 0 ? pid : 0, raw == 0 ? tid : 0, "controlled-thread-cpu");
            duringWindow?.Invoke();
            native.CSwitch(start + duration / 4, now + plan.ObservationWindow / 4,
                0, pid, tid, secondTid != 0 ? pid : 0, secondTid);
            if (exited) native.ThreadEnd(start + duration / 4, now + plan.ObservationWindow / 4, pid, tid);
            if (secondTid != 0) native.CSwitch(start + duration * 3 / 4, now + plan.ObservationWindow * 3 / 4, 0, pid, secondTid, 0, 0);
            for (var raw = 0; raw < processors.Length; raw++)
                native.CSwitch(start + duration, now + plan.ObservationWindow, raw, 0, 0, 0, 0);
            native.Publish(native.Tick + plan.ObservationWindowMilliseconds);
            return start + duration;
        }
    }
    private sealed class Broker : CpuResidencyHeldTestBroker
    {
        internal Broker() : base(configuration: LiveDiagnosticConfiguration(),
            diagnosticSource: NativeKernelEtwTestResource.DiagnosticSourceMode.LiveWindows) { }
        private static NativeKernelEtwSessionConfig LiveDiagnosticConfiguration()
        {
            // These named-thread tests explicitly use the real current process
            // and the native Windows diagnostic source, with finite test caps.
            var config = NativeKernelEtwTestResource.SmallConfiguration();
            config.Residency.MaximumThreads = 8192;
            // Native start-module lookup enumerates the entire target process,
            // including the vstest/ASP.NET host. The small scripted fixture's 16
            // modules are not this input's bound. Reserve the observed host count
            // plus a bounded allowance for libraries loaded by these endpoint tests.
            using var process = Process.GetCurrentProcess();
            var moduleCount = process.Modules.Count;
            Assert.InRange(moduleCount, 1, 1024);
            config.Interrupts.MaximumModules = checked((uint)(moduleCount + 64));
            return config;
        }
    }
    private sealed class NamedThread : IDisposable
    {
        internal const string Description = "rm-thread-cpu-test";
        private readonly ManualResetEventSlim ready = new(), run = new();
        private readonly Thread thread;
        internal int Id;
        internal bool IsAlive => thread.IsAlive;
        internal NamedThread()
        {
            thread = new Thread(() =>
            {
                Marshal.ThrowExceptionForHR(SetThreadDescription(GetCurrentThread(), Description));
                Id = (int)GetCurrentThreadId(); ready.Set(); run.Wait();
                // GetThreadTimes advances per clock tick. A fixed wall-clock burn can receive less than one
                // tick under a loaded test host, so burn until this thread's own counters show CPU time.
                var watch = Stopwatch.StartNew();
                while (OwnCpuTime100Ns() < MinimumBurn100Ns && watch.ElapsedMilliseconds < 3_000) Thread.SpinWait(1024);
            }) { IsBackground = true };
            thread.Start(); Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        }
        internal void BurnAndExit() { run.Set(); Assert.True(thread.Join(TimeSpan.FromSeconds(5))); }
        public void Dispose() { run.Set(); Assert.True(thread.Join(TimeSpan.FromSeconds(5))); ready.Dispose(); run.Dispose(); }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SetThreadDescription(IntPtr thread, string description);
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint GetCurrentThreadId();
        private const long MinimumBurn100Ns = 500_000;
        private static long OwnCpuTime100Ns()
        {
            Assert.True(GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user));
            return kernel + user;
        }
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
    }
}
