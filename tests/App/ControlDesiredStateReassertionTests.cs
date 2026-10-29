using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Text.Json;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class ControlDesiredStateReassertionTests
{
    [Fact]
    public async Task FirstWriteWaitsAnIntervalAndStopDisposesTimerWithoutFinalWrite()
    {
        var plane = new Plane();
        var settings = new Settings();
        var plans = new RuntimePlanProvider(new HostManagerDeploymentState());
        var clock = new Clock();
        using var worker = new ControlDesiredStateReassertion(plane, settings, plans, timeProvider: clock);
        await worker.StartAsync(default);
        await clock.NextTimerAsync();
        clock.Advance(119);
        Assert.Equal(0, plane.Writes);
        clock.Advance(1);
        await plane.NextWriteAsync();
        await clock.NextTimerAsync();
        Assert.Equal(1, plane.Writes);
        clock.Advance(120);
        await plane.NextWriteAsync();
        await clock.NextTimerAsync();
        Assert.Equal(2, plane.Writes);
        await worker.StopAsync(default);
        Assert.Equal(0, clock.ActiveTimers);
        clock.Advance(1200);
        Assert.Equal(2, plane.Writes);
        Assert.Equal(0, plane.Initializations);
    }

    [Fact]
    public async Task PublicationRecalculatesDueTimeFromLastPassWithoutWriting()
    {
        var plane = new Plane();
        var settings = new Settings();
        var plans = new RuntimePlanProvider(new HostManagerDeploymentState());
        var clock = new Clock();
        using var worker = new ControlDesiredStateReassertion(plane, settings, plans, timeProvider: clock);
        await worker.StartAsync(default);
        await clock.NextTimerAsync();
        clock.Advance(40);
        settings.Interval = 60;
        Publish();
        await clock.NextTimerAsync();
        Assert.Equal(0, plane.Writes);
        clock.Advance(19);
        Assert.Equal(0, plane.Writes);
        clock.Advance(1);
        await plane.NextWriteAsync();
        await clock.NextTimerAsync();
        clock.Advance(10);
        settings.Interval = 180;
        Publish();
        await clock.NextTimerAsync();
        clock.Advance(169);
        Assert.Equal(1, plane.Writes);
        clock.Advance(1);
        await plane.NextWriteAsync();
        await worker.StopAsync(default);
        Assert.Equal(2, plane.Writes);

        void Publish() => plans.Publish(CompiledRuntimePlan.Default with
        { Version = plans.Current.Version + 1, HostManager = HostManagerTestPlanFactory.CreatePlan() });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HttpMetadataAndPatchCarryKeepAlive(bool enabled)
    {
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var settings = AppSettingsDefaults.Create() with { Control = new(17, enabled) };
        var wire = JsonSerializer.Serialize(settings, options);
        using var document = JsonDocument.Parse(wire);
        Assert.Equal(enabled, document.RootElement.GetProperty("control").GetProperty("keepAlive").GetBoolean());
        Assert.Equal(settings.Control, JsonSerializer.Deserialize<AppSettings>(wire, options)!.Control);
        var patch = JsonSerializer.SerializeToElement(new { control = new { keepAlive = enabled, reassertionIntervalSeconds = 17 } });
        Assert.Equal(settings.Control, AppSettingsPatchApplier.Apply(AppSettingsDefaults.Create(), patch).Control);
    }

    internal sealed class Clock : TimeProvider
    {
        private long ticks;
        private readonly ConcurrentBag<Timer> timers = [];
        private readonly Channel<Timer> created = Channel.CreateUnbounded<Timer>();
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        internal int ActiveTimers => timers.Count(timer => timer.Active);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            timer.Change(dueTime, period);
            timers.Add(timer);
            created.Writer.TryWrite(timer);
            return timer;
        }
        internal async Task NextTimerAsync() { await created.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        internal void Advance(int seconds)
        {
            Interlocked.Add(ref ticks, TimeSpan.FromSeconds(seconds).Ticks);
            foreach (var timer in timers) timer.Fire();
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            private long due = long.MaxValue;
            private bool disposed;
            internal bool Active { get { lock (this) return !disposed && due != long.MaxValue; } }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (this)
                {
                    if (disposed) return false;
                    due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks;
                    return true;
                }
            }
            internal void Fire()
            {
                lock (this)
                {
                    if (disposed || due > clock.GetTimestamp()) return;
                    due = long.MaxValue;
                }
                callback(state);
            }
            public void Dispose() { lock (this) { disposed = true; due = long.MaxValue; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Settings : IAppSettingsStore
    {
        internal int Interval = 120;
        public Task<AppSettingsUpdateResult> LoadReadOnlyAsync(CancellationToken token) => Task.FromResult(new AppSettingsUpdateResult(
            AppSettingsDefaults.Create() with { Control = new(Interval, true) }, DateTimeOffset.UtcNow, "fixture",
            new(AppSettingsSourceKind.TestFixture, "test", "", "", false)));
        public Task<AppSettingsUpdateResult> LoadAsync(CancellationToken token) => LoadReadOnlyAsync(token);
        public Task<AppSettingsUpdateResult> SaveAsync(AppSettings value, CancellationToken token) => throw new NotSupportedException();
        public Task<AppSettingsUpdateResult> SaveValidatedAsync(AppSettings value, Func<AppSettingsUpdateResult, CancellationToken, Task> validate, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Plane : IControlPlane
    {
        internal int Writes;
        internal int Initializations;
        private readonly Channel<bool> written = Channel.CreateUnbounded<bool>();
        internal async Task NextWriteAsync() { await written.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        public Task InitializeAsync(CancellationToken token) { Initializations++; return Task.CompletedTask; }
        public Task<ControlApplyReport> ReassertAsync(CancellationToken token)
        { Interlocked.Increment(ref Writes); written.Writer.TryWrite(true); return Task.FromResult(ControlApplyReport.Empty); }
        public Task ForgetInstanceAsync(string id, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlStateView> RetryObjectAsync(string id, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlStateView> ReadStateAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<ControlStateView> SetObjectSettingsAsync(string id, IReadOnlyList<ControlSetting> settings, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlStateView> ApplyDesiredStateAsync(ControlDesiredState desired, CancellationToken token) => throw new NotSupportedException();
    }
}
