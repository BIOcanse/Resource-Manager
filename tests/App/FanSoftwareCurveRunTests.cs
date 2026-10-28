using System.Text.Json;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed class FanSoftwareCurveRunTests
{
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
        internal Action? InitialRead;
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
            if (request.GetProperty("op").GetString() == "temp") TemperaturePushed?.Invoke();
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
        await client.PushTemperaturesAsync(new Dictionary<string, ResourceManager.App.Infrastructure.Control.FanTemperatureSample> {
            ["cpu"] = new(60, DateTime.UtcNow.Ticks) }, default);
        var result = await client.SendAsync("setcurve", new Dictionary<string, object?> {
            ["fan"] = 0, ["execution"] = "software",
            ["curve"] = new[] { new { temperatureCelsius = 60, percent = 40 } }
        }, default);
        Assert.True(result!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.Value.GetProperty("execution").ValueKind);
        Assert.True(client.NeedsTemperatures());
        Assert.True(backend.Read()[0].AutomaticControl);
    }
}
