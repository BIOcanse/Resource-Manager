using System.Text.Json;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed class FanCoreLifetimeReentryTests
{
    [Fact]
    public async Task SameLifetimeRestartsWithFreshCoreSessionAndAcceptsCommands()
    {
        var opened = 0;
        var closed = 0;
        using var core = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
        {
            Interlocked.Increment(ref opened);
            return new DelegateFanSession(_ => JsonSerializer.SerializeToElement(new { ok = true, fans = Array.Empty<object>() }),
                () => Interlocked.Increment(ref closed));
        });
        var lifetime = new FanControlCoreLifetimeService(core);
        for (var run = 1; run <= 2; run++)
        {
            await lifetime.StartAsync(default);
            await lifetime.StartupTask.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                var response = await core.SendAsync("read", null, default);
                Assert.True(response!.Value.GetProperty("ok").GetBoolean());
                Assert.Equal(run, opened);
            }
            finally { await lifetime.StopAsync(default); }
            Assert.Equal(run, closed);
        }
    }
}
