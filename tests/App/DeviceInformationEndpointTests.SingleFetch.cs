using System.Text.Json;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Theory]
    [InlineData("/api/device-topology/fields", false)]
    [InlineData("/api/device-topology/state", true)]
    public async Task SnapshotHttpFetchesNewSuccessFailureAndRecoveryWithoutOtherSubscribers(
        string path, bool stateEnvelope)
    {
        var first = DeviceInformationTestData.Snapshot() with
            { Batteries = [new("battery", "first", "serial", "chemistry")] };
        var recovered = first with { Batteries = [new("battery", "recovered", "serial", "chemistry")] };
        var reader = new Reader(first, new InvalidOperationException("recorded new capture failed"), recovered);
        using var fixture = new Fixture(reader);
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        Assert.Equal(0, reader.ReadCount);

        for (var phase = 0; phase < 3; phase++)
        {
            var started = DateTimeOffset.UtcNow;
            using var response = JsonDocument.Parse(await client.GetStringAsync(path));
            var fields = stateEnvelope ? response.RootElement.GetProperty("informationFields") : response.RootElement;
            var manufacturer = Field(fields, "battery", "battery_all_manufacturer_declared");
            Assert.Equal("battery", manufacturer.GetProperty("definition").GetProperty("resource").GetString());
            Assert.Equal("windows.battery", manufacturer.GetProperty("definition").GetProperty("sourceId").GetString());
            if (phase == 1)
            {
                Assert.Equal(JsonValueKind.Null, manufacturer.GetProperty("value").ValueKind);
                Assert.True(!manufacturer.TryGetProperty("observedAt", out var observedAt)
                    || observedAt.ValueKind == JsonValueKind.Null);
            }
            else
            {
                Assert.Equal(phase == 0 ? "first" : "recovered", manufacturer.GetProperty("value").GetString());
                // Declared battery fields have no per-field observation timestamp;
                // the original information frame and provider attempt own freshness.
                Assert.True(fields.GetProperty("observedAt").TryGetDateTimeOffset(out _));
            }
            Assert.True(fixture.Provider.ReadState().LastAttemptAt >= started);
            await fixture.WaitForNoDemandAndIdleAsync();
            Assert.Equal(phase + 1, reader.ReadCount);
        }
        var count = reader.ReadCount;
        await Task.Delay(1100);
        Assert.Equal(count, reader.ReadCount);
    }

    [Fact]
    public async Task SnapshotHttpCancellationReleasesDemandAfterTheAcceptedReadSettles()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var reader = new Reader(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Recorded read was not released.");
            return DeviceInformationTestData.Snapshot();
        });
        using var fixture = new Fixture(reader);
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetStringAsync("/api/device-topology/fields", cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally { release.Set(); }
        await fixture.WaitForNoDemandAndIdleAsync();
        Assert.Equal(1, reader.ReadCount);
        await Task.Delay(1100);
        Assert.Equal(1, reader.ReadCount);
    }
}
