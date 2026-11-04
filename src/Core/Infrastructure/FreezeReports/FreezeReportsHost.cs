using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.SystemHealth;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.Shared.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.FreezeReports;

// Host adapters only: native owns report state and settings. Readers never acquire sampling.
public sealed class FreezeReportsHost : IDisposable
{
    private readonly object gate = new();
    private readonly NativeFreezeReportsSession native;
    private readonly FreezeRecorderSampleAdapter samples;
    private readonly ILogger<FreezeReportsHost> logger;
    private readonly RuntimePlanProvider plans;
    private readonly HashSet<(uint Stream, Channel<JsonElement> Channel)> readers = [];
    private CancellationTokenSource? recorderSettings;
    private bool disposed;
    private int pending;
    public event Action? SettingsChanged;

    public FreezeReportsHost(IHostEnvironment environment, ResourceManagerLedgerOwner ledger,
        IMetricSnapshotPushSource hardware, IResourceBreakdownPushSource processes,
        ISystemInterruptSnapshotPushSource interrupts,
        ILogger<FreezeReportsHost> logger, RuntimePlanProvider plans)
    {
        this.logger = logger;
        this.plans = plans;
        samples = new(hardware, processes, interrupts);
        native = new(Path.Combine(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath), "UserData"), ledger);
        native.Changed += Signal;
    }
    public bool SystemEventsEnabled { get { lock (gate) return !disposed && native.Setting(0) == 1; } }
    public bool RecorderEnabled { get { lock (gate) return !disposed && native.Setting(1) == 1; } }
    public JsonElement Request(uint operation, string? id = null)
    { lock (gate) return RequestLocked(operation, id is null ? [] : Encoding.UTF8.GetBytes(id)); }
    public JsonElement Patch(JsonElement input)
    { lock (gate) return RequestLocked(8, Encoding.UTF8.GetBytes(input.GetRawText())); }
    private JsonElement RequestLocked(uint operation, ReadOnlySpan<byte> input)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var json = JsonDocument.Parse(native.Request(operation, input));
        return json.RootElement.Clone();
    }
    public async IAsyncEnumerable<JsonElement> SubscribeAsync(uint stream,
        [EnumeratorCancellation] CancellationToken token)
    {
        var channel = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        lock (gate)
        {
            channel.Writer.TryWrite(RequestLocked(stream == 0 ? 0u : 2u, []));
            readers.Add((stream, channel));
        }
        try { await foreach (var value in channel.Reader.ReadAllAsync(token)) yield return value; }
        finally { lock (gate) readers.Remove((stream, channel)); }
    }
    // This callback only posts a wake-up. Native may invoke it while holding its own lock.
    private void Signal(uint stream)
    {
        if (Interlocked.Or(ref pending, stream == 2 ? 7 : stream == 0 ? 1 : 2) == 0)
            ThreadPool.QueueUserWorkItem(static owner => owner.PublishChanges(), this, false);
    }
    private void PublishChanges()
    {
        var changes = 0;
        try
        {
            lock (gate)
            {
                if (disposed) return;
                // Keep pending nonzero while waiting for the native-call gate, so
                // event replay cannot queue another blocked worker for every event.
                changes = Interlocked.Exchange(ref pending, 0);
                if (changes == 0) return;
                if ((changes & 4) != 0) recorderSettings?.Cancel();
                foreach (var stream in new uint[] { 0, 1 })
                {
                    if ((changes & (1 << (int)stream)) == 0 || !readers.Any(reader => reader.Stream == stream)) continue;
                    var value = RequestLocked(stream == 0 ? 0u : 2u, []);
                    foreach (var reader in readers)
                        if (reader.Stream == stream) reader.Channel.Writer.TryWrite(value);
                }
            }
            if ((changes & 4) != 0) SettingsChanged?.Invoke();
        }
        catch (Exception error) { logger.LogError(error, "Native freeze report publication failed."); }
    }
    internal async Task RunEventsAsync(BusinessBlockRunContext context)
    {
        context.StopNotification.ThrowIfCancellationRequested();
        var plan = await plans.WaitForPublishedAsync(context.StopNotification);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            native.SetCommitThreshold(plan.MemoryProtection.ThresholdPercent);
            native.StartEvents();
        }
        try
        {
            context.ReportRunning();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.StopNotification);
        }
        catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
        finally { lock (gate) if (!disposed) native.StopEvents(); }
    }
    internal async Task RunRecorderAsync(BusinessBlockRunContext context)
    {
        var token = context.StopNotification;
        token.ThrowIfCancellationRequested();
        var initialPlan = await plans.WaitForPublishedAsync(token);
        // Event-driven settings input from the existing plan, never another metric reader.
        var immediateThreshold = initialPlan.MemoryProtection.ThresholdPercent;
        void PlanChanged(CompiledRuntimePlan plan) => Volatile.Write(ref immediateThreshold, plan.MemoryProtection.ThresholdPercent);
        plans.Published += PlanChanged;
        PlanChanged(plans.Current);
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                native.SetCommitThreshold(Volatile.Read(ref immediateThreshold));
                native.StartRecorder();
            }
            context.ReportRunning();
            while (!token.IsCancellationRequested)
            {
                using var settings = CancellationTokenSource.CreateLinkedTokenSource(token);
                TimeSpan interval;
                lock (gate) { recorderSettings = settings; interval = TimeSpan.FromMilliseconds(native.Setting(2)); }
                try
                {
                    await samples.RunAsync(interval, value =>
                    {
                        lock (gate) { if (!disposed) native.Submit(value); }
                    }, value =>
                    {
                        lock (gate) { if (!disposed) native.UpdateProcesses(value); }
                    }, () => Volatile.Read(ref immediateThreshold),
                        error => logger.LogWarning(error, "Freeze recorder interrupt source is unavailable; continuing other recording."), settings.Token);
                }
                catch (OperationCanceledException) when (settings.IsCancellationRequested) { }
                finally { lock (gate) if (ReferenceEquals(recorderSettings, settings)) recorderSettings = null; }
            }
        }
        finally
        {
            plans.Published -= PlanChanged;
            lock (gate) if (!disposed) native.StopRecorder();
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            recorderSettings?.Cancel();
            native.Changed -= Signal;
            native.Dispose();
            foreach (var reader in readers) reader.Channel.Writer.TryComplete();
            readers.Clear();
        }
    }
}

internal sealed class SystemEventReportsBlock(FreezeReportsHost owner) : IBusinessBlock
{ public Task RunAsync(BusinessBlockRunContext context) => owner.RunEventsAsync(context); }
internal sealed class FreezeRecorderBlock(FreezeReportsHost owner) : IBusinessBlock
{ public Task RunAsync(BusinessBlockRunContext context) => owner.RunRecorderAsync(context); }
