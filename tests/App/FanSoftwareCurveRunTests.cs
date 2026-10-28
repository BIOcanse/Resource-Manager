using System.Text.Json;
using ResourceManager.App.Infrastructure.Control.Writers;
using FanControlCore;

namespace Resource_Manager_APP.Tests;

public sealed class FanSoftwareCurveRunTests
{
    [Fact]
    public async Task KindsSubscribeBeforeInitialReadAndCannotOverwriteConcurrentNotification()
    {
        var session = new RunSession();
        session.InitialKindsRead = () => session.SetKinds("gpu");
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => session);
        await client.StartSession();
        Assert.True(session.KindsReadAfterSubscription);
        Assert.Equal(["gpu"], client.SoftwareCurveTemperatureKinds);
    }

    [Fact]
    public async Task KindsPublishOrderedValuesOutsideOwnerLockOnlyOnChangeAndClearOnClose()
    {
        var session = new RunSession();
        session.SetKinds();
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => session);
        await client.StartSession();
        var edges = new List<string>();
        var outsideLock = false;
        client.SoftwareCurveTemperatureKindsChanged += kinds =>
        {
            edges.Add(string.Join(',', kinds));
            if (kinds.SequenceEqual(["cpu"]))
                outsideLock = Task.Run(() => session.SetKinds("gpu")).Wait(TimeSpan.FromSeconds(2));
        };
        session.SetKinds("cpu");
        session.SetKinds("gpu");
        session.SetKinds("any");
        await client.StopSessionAsync();
        session.SetKinds("cpu");
        Assert.Empty(client.SoftwareCurveTemperatureKinds);
        Assert.True(outsideLock);
        Assert.Equal(["cpu", "gpu", "any", ""], edges);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-10)]
    [InlineData(1)]
    public async Task PackagedSdkRecordsSoftwarePlanWithoutFreshTemperature(int? ageSeconds)
    {
        using var backend = new ProbeFanBackend();
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
            new LibraryFanSession(new FanControlCore.Discovery.FanCore(backend)));
        await client.StartSession();
        if (ageSeconds is { } age)
            await client.PushTemperaturesAsync([new("cpu", 60, DateTime.UtcNow.AddSeconds(age).Ticks)], default);
        var result = (await client.SendAsync("setcurve", new Dictionary<string, object?> {
            ["fan"] = 0, ["execution"] = "software", ["curve"] = new[] { new { temperatureCelsius = 60, percent = 40 } }
        }, default))!.Value;
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.True(result.GetProperty("softwarePlanRecorded").GetBoolean());
        Assert.True(client.SoftwareCurveRunNeeded);
        Assert.Equal(["cpu"], client.SoftwareCurveTemperatureKinds);
        Assert.True(backend.Read()[0].AutomaticControl);
    }

    [Fact]
    public async Task InitialSnapshotCannotOverwriteANotificationDeliveredDuringRead()
    {
        var session = new RunSession();
        session.InitialRead = () => session.SetNeeded(true);
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => session);
        await client.StartSession();
        Assert.True(client.SoftwareCurveRunNeeded);
    }

    [Fact]
    public async Task OwnerNotificationsDrainInOrderOutsideTheOwnerLock()
    {
        var session = new RunSession(initial: true);
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => session);
        var edges = new List<bool>();
        var outsideLock = false;
        client.SoftwareCurveRunNeededChanged += () =>
        {
            var value = client.SoftwareCurveRunNeeded;
            edges.Add(value);
            if (value) outsideLock = Task.Run(() => session.SetNeeded(false)).Wait(TimeSpan.FromSeconds(2));
        };
        await client.StartSession();
        Assert.True(outsideLock);
        Assert.Equal(new[] { true, false }, edges);
    }

    [Fact]
    public async Task SubscribesBeforeInitialReadPublishesOnlyEdgesAndClearsOnClose()
    {
        var session = new RunSession(initial: true);
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => session);
        var edges = new List<bool>();
        client.SoftwareCurveRunNeededChanged += () => edges.Add(client.SoftwareCurveRunNeeded);
        await client.StartSession();
        Assert.True(session.ReadAfterSubscription);
        session.SetNeeded(true);
        session.SetNeeded(false);
        session.SetNeeded(false);
        session.SetNeeded(true);
        await client.StopSessionAsync();
        Assert.False(client.SoftwareCurveRunNeeded);
        session.SetNeeded(true); // A retired session no longer owns the publication.
        Assert.Equal(new[] { true, false, true, false }, edges);
    }

    internal sealed class RunSession(bool initial = false) : IFanControlCoreSession
    {
        private bool needed = initial;
        private Action<bool>? observer;
        internal bool ReadAfterSubscription;
        internal bool Disposed;
        internal Func<CancellationToken, Task>? Run;
        internal Action? TemperaturePushed;
        internal Action<IReadOnlyList<FanTemperatureSample>>? Push;
        internal Action? InitialRead;
        internal Action? InitialKindsRead;
        internal bool KindsReadAfterSubscription;
        private IReadOnlyList<string> kinds = ["cpu"];
        private Action<IReadOnlyList<string>>? kindsObserver;
        public IReadOnlyList<string> SoftwareCurveTemperatureKinds
        {
            get
            {
                KindsReadAfterSubscription = kindsObserver is not null;
                var value = kinds;
                var read = InitialKindsRead;
                InitialKindsRead = null;
                read?.Invoke();
                return value;
            }
        }
        public event Action<IReadOnlyList<string>>? SoftwareCurveTemperatureKindsChanged
        { add => kindsObserver += value; remove => kindsObserver -= value; }
        internal void SetKinds(params string[] value) { kinds = value; kindsObserver?.Invoke(value); }
        public void PushTemperatures(IReadOnlyList<FanTemperatureSample> samples)
        { TemperaturePushed?.Invoke(); Push?.Invoke(samples); }
        public bool SoftwareCurveRunNeeded
        {
            get
            {
                ReadAfterSubscription = observer is not null;
                var value = needed;
                var read = InitialRead;
                InitialRead = null;
                read?.Invoke();
                return value;
            }
        }
        public event Action<bool>? SoftwareCurveRunNeededChanged { add => observer += value; remove => observer -= value; }
        internal void SetNeeded(bool value) { needed = value; observer?.Invoke(value); }
        public Task RunSoftwareCurveAsync(CancellationToken stop) => Run?.Invoke(stop) ?? Task.Delay(Timeout.Infinite, stop);
        public JsonElement Handle(JsonElement request)
        {
            return JsonSerializer.SerializeToElement(new { ok = true, available = true, fans = Array.Empty<object>() });
        }
        public void Dispose() => Disposed = true;
    }
    [Fact]
    public async Task RecordedPlanNeedsRunEvenBeforeExecutionStarts()
    {
        using var backend = new ProbeFanBackend();
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
            new LibraryFanSession(new FanControlCore.Discovery.FanCore(backend)));
        await client.StartSession();
        var result = await client.SendAsync("setcurve", new Dictionary<string, object?> {
            ["fan"] = 0, ["execution"] = "software",
            ["curve"] = new[] { new { temperatureCelsius = 60, percent = 40 } }
        }, default);
        Assert.True(result!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.Value.GetProperty("execution").ValueKind);
        Assert.True(client.NeedsTemperatures());
        Assert.Equal(["cpu"], client.SoftwareCurveTemperatureKinds);
        Assert.True(backend.Read()[0].AutomaticControl);
    }
}
