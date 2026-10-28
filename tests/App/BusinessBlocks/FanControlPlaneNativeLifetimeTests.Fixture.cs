using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed partial class FanControlPlaneNativeLifetimeTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly IHost host;
        private int opened;
        private int closed;
        internal readonly double?[] Duties = new double?[2];
        internal readonly FanControlCoreClient Core;
        internal readonly ControlPlane Plane;
        internal readonly PausingWriter Writer;
        internal readonly PausingLayer Layer;
        internal readonly ControlActualStateReader? Actual;
        internal readonly ControlledClock Clock = new();
        private int reads;
        internal bool FailWrites;
        internal int Opened => Volatile.Read(ref opened);
        internal int Closed => Volatile.Read(ref closed);
        internal int Reads => Volatile.Read(ref reads);
        internal BusinessBlockScheduler Scheduler => host.Services.GetRequiredService<BusinessBlockScheduler>();

        internal Fixture(bool observeActual = false)
        {
            Core = new(new FanTestEnvironment(), (_, _) =>
            {
                Interlocked.Increment(ref opened);
                return new DelegateFanSession(Handle, () =>
                {
                    Array.Clear(Duties);
                    Interlocked.Increment(ref closed);
                });
            });
            Writer = new(new FanControlWriter(Core));
            var catalog = new Catalog(Core);
            Layer = new(new ControlWriteLayer(catalog, [Writer], new Access()));
            Plane = new(new Store(), Layer, fan: Core);
            if (observeActual) Actual = new(catalog, [new FanControlWriter(Core)], Clock);
            host = Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureServices(services =>
                {
                    services.AddSingleton(Core);
                    services.AddSingleton<IControlPlane>(Plane);
                    services.AddSingleton(new RuntimePlanProvider(new HostManagerDeploymentState()));
                    services.AddSingleton<BackendDecisionStateBindings>();
                    services.AddBusinessBlockHostedService<FanControlCoreLifetimeService>(BackendUids.FanControlCoreLifetimeService,
                        _ => new FanControlCoreLifetimeService(Core));
                    if (Actual is { } actual)
                        services.AddBusinessBlockHostedService<ControlActualStateReader>(BackendUids.ControlActualStateReader,
                            _ => actual);
                    NativeBackendBlockTestSupport.AddFullTreeHostFixture(services);
                }).Build();
        }

        private JsonElement Handle(JsonElement request)
        {
            var operation = request.GetProperty("op").GetString();
            if (operation == "describe")
                return JsonSerializer.SerializeToElement(new { ok = true, available = true, backend = "fixture",
                    fans = Enumerable.Range(0, 2).Select(index => new {
                        index, writable = true, supportsDuty = true, role = "cpu", supportsSoftwareCurve = true
                    }).ToArray() });
            var fan = request.TryGetProperty("fan", out var indexValue) ? indexValue.GetInt32() : 0;
            if (operation is "set" or "boost")
            {
                if (FailWrites) return JsonSerializer.SerializeToElement(new { ok = false, error = "injected-write-failure" });
                Duties[fan] = operation == "boost" ? 100 : request.GetProperty("percent").GetDouble();
            }
            if (operation == "auto") Duties[fan] = null;
            if (operation == "read") Interlocked.Increment(ref reads);
            return JsonSerializer.SerializeToElement(new { ok = true,
                fans = Enumerable.Range(0, 2).Select(index => new {
                    index, automaticControl = Duties[index] is null, dutyPercent = Duties[index], rpm = 1800
                }).ToArray() });
        }

        internal async Task StartAsync()
        {
            await host.StartAsync();
            Assert.False(Core.PendingCommands);
            Assert.False(Plane.AppliedFanControlExists);
            Assert.Equal(BusinessBlockState.Stopped, Scheduler.ReadState(BackendUids.FanControlCoreLifetimeService));
            Assert.Equal(0, Opened);
        }

        internal Task WaitRunningAsync() => BusinessBlockTestSupport.State(Scheduler,
            BackendUids.FanControlCoreLifetimeService, BusinessBlockState.Running);
        internal Task WaitStoppedAsync() => BusinessBlockTestSupport.State(Scheduler,
            BackendUids.FanControlCoreLifetimeService, BusinessBlockState.Stopped);

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            if (host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync(); else host.Dispose();
            await Core.DisposeAsync();
        }
    }

    private sealed class Catalog(FanControlCoreClient core) : IControlObjectCatalog
    {
        public bool ActualReadingsUseFanCore => true;
        private static readonly ControlCapability[] Capabilities = [
            new("fan.duty", ControlValueKinds.Number, true, Range: new(0, 100, 1, "%")),
            new("fan.lock-maximum", ControlValueKinds.Toggle, true),
            new("fan.rpm", ControlValueKinds.Number, false)];
        public ControlObjectCatalog ReadObjects() => new(core.Describe().Select(fan => new ControlObject(
            "fan:oem" + fan.Index, ControlObjectKinds.Fan, null,
            new(ControlOperatingSystems.Windows, ControlVendors.Oem), Capabilities)).ToArray(), DateTimeOffset.UtcNow);
        public async Task<ControlObjectCatalog> ReadObjectsAsync(CancellationToken token)
        {
            await core.DiscoverAsync(token);
            return ReadObjects();
        }
    }

    private sealed class ControlledClock : TimeProvider
    {
        private readonly Channel<ControlledTimer> timers = Channel.CreateUnbounded<ControlledTimer>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ControlledTimer(callback, state);
            timers.Writer.TryWrite(timer);
            return timer;
        }
        internal async Task<ControlledTimer> NextTimerAsync()
            => await timers.Reader.ReadAsync().AsTask().WaitAsync(BusinessBlockTestSupport.TestTimeout);
    }

    private sealed class ControlledTimer(TimerCallback callback, object? state) : ITimer
    {
        private int disposed;
        internal bool Disposed => Volatile.Read(ref disposed) != 0;
        internal void Fire() { if (!Disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Access : IControlAccessLevel
    {
        public string Current => "normal";
        public Task SetAsync(string value, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class Store : IControlDesiredStateStore
    {
        private ControlDesiredState current = ControlDesiredState.Empty;
        public Task<ControlDesiredState> LoadAsync(CancellationToken token) => Task.FromResult(current);
        public Task SaveAsync(ControlDesiredState value, CancellationToken token)
        { current = value; return Task.CompletedTask; }
    }

    private sealed class PausingWriter(FanControlWriter inner) : IControlWriter
    {
        internal Func<Task>? AfterWrite;
        public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability) => inner.Probe(target, capability);
        public async Task<ControlApplyOutcome> WriteAsync(ControlObject target, ControlCapability capability,
            ControlSetting setting, CancellationToken token)
        {
            var outcome = await inner.WriteAsync(target, capability, setting, token);
            if (AfterWrite is { } pause) await pause();
            return outcome;
        }
        public Task<bool> RestoreAsync(ControlObject target, ControlCapability capability,
            IReadOnlySet<string> retained, CancellationToken token, ControlSetting? previousSetting = null)
            => inner.RestoreAsync(target, capability, retained, token, previousSetting);
    }

    private sealed class PausingLayer(ControlWriteLayer inner) : IControlWriteLayer
    {
        internal Func<Task>? AfterWrite;
        internal Func<Task>? AfterRelease;
        public async Task<ControlApplyReport> WriteAsync(ControlDesiredState desired, CancellationToken token)
        {
            var result = await inner.WriteAsync(desired, token);
            if (AfterWrite is { } pause) await pause();
            return result;
        }
        public async Task<ControlApplyReport> ReleaseAsync(ControlDesiredState removed,
            ControlDesiredState remaining, CancellationToken token)
        {
            var result = await inner.ReleaseAsync(removed, remaining, token);
            if (AfterRelease is { } pause) await pause();
            return result;
        }
    }
}
