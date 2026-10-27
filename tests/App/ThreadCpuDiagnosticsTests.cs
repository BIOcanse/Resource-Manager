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

public sealed class ThreadCpuDiagnosticsTests
{
    [Fact]
    public async Task ThreadDemandResumesPausedResidencyAndWaitsForAFreshWindow()
    {
        using var target = new NamedThread();
        using var process = Process.GetCurrentProcess();
        var birth = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        using var reader = Reader(sampling, broker);
        broker.WindowReady = () => FeedWindow(reader, sampling, process.Id, birth, target.Id, false);
        using var slow = reader.AcquireSubscription("ordinary.paused", TimeSpan.FromSeconds(30));
        await reader.StartAsync(default);
        try
        {
            await Until(() => reader.Read() is not null && Owner(reader).State == "Idle");
            var previous = reader.Read()!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = Assert.IsType<CpuProcessThreadWindow>(await reader.CaptureThreadWindowAsync(
                "paused", process.Id, birth, timeout.Token));
            Assert.True(result.MeasuredFrom > previous.MeasuredFrom);
            Assert.Equal(process.Id, result.Process.ProcessId);
            Assert.Equal(NamedThread.Description, Assert.Single(result.Process.Threads).Diagnostic!.Description);
            Assert.Equal(0, Owner(reader).DiagnosticRequestCount);
        }
        finally { await reader.StopAsync(default); }
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
        var broker = new Broker();
        using var reader = Reader(sampling, broker);
        broker.WindowReady = () =>
        {
            FeedWindow(reader, sampling, process.Id, birth, target.Id, exited: true,
                secondTid: survivor.Id, duringWindow: !initiallyPaused || broker.Windows > 1 ? target.BurnAndExit : null);
        };
        using var slow = initiallyPaused ? reader.AcquireSubscription("endpoint.paused", TimeSpan.FromSeconds(30)) : null;
        await using var app = BuildDebugApp(reader);
        await app.StartAsync();
        using var client = DebugClient(app, TimeSpan.FromSeconds(20));
        await reader.StartAsync(default);
        try
        {
            if (initiallyPaused) await Until(() => reader.Read() is not null && Owner(reader).State == "Idle");
            using var response = await client.GetAsync($"/api/debug/thread-cpu?processId={process.Id}&processStartKey={birth}");
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
            var evidence = Environment.GetEnvironmentVariable("RM_THREAD_CPU_EXAMPLE_PATH");
            if (!string.IsNullOrWhiteSpace(evidence)) await File.WriteAllTextAsync(evidence, json);
        }
        finally { await reader.StopAsync(default); }
    }

    [Fact]
    public async Task DebugTimeoutReportsPublishedWindowsThatNeverBecomeFresh()
    {
        using var process = Process.GetCurrentProcess();
        var birth = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        using var reader = Reader(sampling, broker);
        broker.WindowReady = () => FeedWindow(reader, sampling, process.Id, birth, 10, false,
            timestampOffset: -TimeSpan.FromMinutes(2));
        using var slow = reader.AcquireSubscription("timeout.paused", TimeSpan.FromSeconds(30));
        await using var app = BuildDebugApp(reader);
        await app.StartAsync();
        using var client = DebugClient(app, TimeSpan.FromSeconds(40));
        await reader.StartAsync(default);
        try
        {
            await Until(() => reader.Read() is not null && Owner(reader).State == "Idle");
            using var response = await client.GetAsync($"/api/debug/thread-cpu?processId={process.Id}&processStartKey={birth}");
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
        }
        finally { await reader.StopAsync(default); }
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
        var broker = new Broker();
        using var reader = Reader(sampling, broker);
        broker.WindowReady = () =>
        {
            target.BurnAndExit();
            // The retained native instance has ended. Later replayed execution under this numeric TID is not it.
            FeedWindow(reader, sampling, process.Id, birth, target.Id, false);
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var capture = reader.CaptureThreadWindowAsync("reused-tid", process.Id, birth, timeout.Token);
        await reader.StartAsync(default);
        try
        {
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
        var broker = new Broker();
        using var reader = Reader(sampling, broker);
        broker.WindowReady = () => FeedWindow(reader, sampling, 100, 1000, 10, false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = reader.SubscribeAsync("ordinary", TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator();
        var next = stream.MoveNextAsync().AsTask();
        await reader.StartAsync(default);
        try
        {
            Assert.True(await next);
            Assert.NotNull(stream.Current);
            Assert.Equal(0, Owner(reader).DiagnosticNativeReads);
            Assert.Equal(0, Owner(reader).DiagnosticRequestCount);
            Assert.Null(typeof(EtwCpuResidencyCaptureOwner).GetField("diagnosticThreadStarted", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Owner(reader)));
        }
        finally { await reader.StopAsync(default); }
    }

    [Fact]
    public async Task CancellationReleasesItsOwnRequestWithoutStartingAnotherSampler()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
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
        Assert.Equal(0, broker.Windows);
    }

    [Fact]
    public async Task ReusedProcessIdentityNeverReceivesCurrentThreadMetadata()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker();
        using var reader = Reader(sampling, broker);
        using var process = Process.GetCurrentProcess();
        var birth = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        broker.WindowReady = () => FeedWindow(reader, sampling, process.Id, birth, 10, false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var capture = reader.CaptureThreadWindowAsync("wrong-birth", process.Id, birth + 1, timeout.Token);
        await reader.StartAsync(default);
        try { Assert.Null(await capture); Assert.Equal(0, Owner(reader).DiagnosticRequestCount); }
        finally { await reader.StopAsync(default); }
    }

    [Fact]
    public async Task FailedCaptureReturnsEmptyAndReleasesDemand()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new Broker { Running = false };
        using var reader = Reader(sampling, broker);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var capture = reader.CaptureThreadWindowAsync("failed", 123, 123, timeout.Token);
        await reader.StartAsync(default);
        try { Assert.Null(await capture); Assert.Equal(0, Owner(reader).DiagnosticRequestCount); }
        finally { await reader.StopAsync(default); }
    }

    private static EtwCpuCoreResidencyReader Reader(HostManagerSamplingSubscriptionTestFixture sampling, Broker broker)
        => new(broker, sampling.Provider, sampling.Owner, NullLogger<EtwCpuCoreResidencyReader>.Instance, static (_, _) => { });
    private static EtwCpuResidencyCaptureOwner Owner(EtwCpuCoreResidencyReader reader)
        => (EtwCpuResidencyCaptureOwner)typeof(EtwCpuCoreResidencyReader).GetField("captureOwner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    private static void FeedWindow(EtwCpuCoreResidencyReader reader, HostManagerSamplingSubscriptionTestFixture sampling,
        int pid, long birth, int tid, bool exited, int secondTid = 0, Action? duringWindow = null,
        TimeSpan timestampOffset = default)
    {
        var session = typeof(EtwCpuResidencyCaptureOwner).GetField("activeSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Owner(reader))!;
        var buffer = (CpuResidencyAggregationBuffer)session.GetType().GetProperty("Buffer")!.GetValue(session)!;
        var completion = (CpuResidencyWindowCompletion)session.GetType().GetProperty("Window")!.GetValue(session)!;
        var plan = sampling.Provider.Current.HostManager.CpuCoreResidency;
        var processors = plan.PhysicalCoreByLogicalProcessor.Keys.Order().ToArray();
        var start = Stopwatch.GetTimestamp();
        var now = DateTimeOffset.UtcNow + timestampOffset;
        var duration = Stopwatch.Frequency * plan.ObservationWindowMilliseconds / 1000;
        buffer.ObserveProcessStart(start, now, pid, (ulong)pid, birth, "recorded-thread-cpu");
        buffer.ObserveThreadStart(start, now, pid, tid);
        if (secondTid != 0) buffer.ObserveThreadStart(start, now, pid, secondTid);
        foreach (var cpu in processors)
        {
            buffer.ObserveContextSwitch(start, now, cpu, 0, 0, cpu == processors[0] ? pid : 0, "recorded-thread-cpu", cpu == processors[0] ? tid : 0);
            completion.Observe(cpu, start);
        }
        duringWindow?.Invoke();
        buffer.ObserveContextSwitch(start + duration / 4, now + plan.ObservationWindow / 4,
            processors[0], pid, tid, secondTid != 0 ? pid : 0, null, secondTid);
        if (exited) buffer.ObserveThreadEnd(start + duration / 4, now + plan.ObservationWindow / 4, pid, tid);
        if (secondTid != 0) buffer.ObserveContextSwitch(start + duration * 3 / 4, now + plan.ObservationWindow * 3 / 4,
            processors[0], pid, secondTid, 0, null, 0);
        foreach (var cpu in processors)
        {
            buffer.ObserveContextSwitch(start + duration, now + plan.ObservationWindow, cpu, 0, 0, 0, null, 0);
            completion.Observe(cpu, start + duration);
        }
    }
    private sealed class Broker : IKernelEtwSessionBroker
    {
        public Action? WindowReady;
        public bool Running = true;
        public int Windows;
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            if (!Running) throw new ObjectDisposedException(nameof(Broker));
            var source = new KernelEtwEventSource();
            request.Bind(source, new(7, DateTimeOffset.UtcNow));
            source.Activate();
            if (request.Keywords.HasFlag(KernelTraceEventParser.Keywords.ContextSwitch)) { Windows++; WindowReady?.Invoke(); }
            return new Lease(this, source);
        }
        public KernelEtwSessionSnapshot GetSnapshot() => new(Running ? "Running" : "Stopped", "recorded", 7,
            DateTimeOffset.UtcNow, KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.ContextSwitch, 1, 0);
        private sealed class Lease(Broker owner, KernelEtwEventSource source) : IKernelEtwSubscription
        {
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
            public void Dispose() => source.Cancel();
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
