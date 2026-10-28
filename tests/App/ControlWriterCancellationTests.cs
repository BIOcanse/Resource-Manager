using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed class ControlWriterCancellationTests
{
    [Fact]
    public async Task CancellationDuringFanSessionInitializationPreventsMutation()
    {
        using var cancellation = new CancellationTokenSource();
        var mutations = 0;
        await using var client = new FanControlCoreClient(new EnvironmentInfo(), (_, _) =>
        {
            cancellation.Cancel();
            return new DelegateFanSession(request =>
            {
                if (request.GetProperty("op").GetString() == "boost") mutations++;
                return System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{\"ok\":true}");
            });
        });

        var command = client.SendAsync("boost", null, cancellation.Token);
        Assert.True(client.PendingCommands);
        await client.StartSession().WaitAsync(BusinessBlockTestSupport.TestTimeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
        Assert.Equal(0, mutations);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("restore")]
    [InlineData("read")]
    [InlineData("curve")]
    public async Task CanceledFanRequestDoesNotOpenTheHardwareSession(string operation)
    {
        var opened = 0;
        using var client = new FanControlCoreClient(new EnvironmentInfo(), (_, _) =>
        {
            opened++;
            throw new InvalidOperationException("Canceled requests must not open hardware.");
        });
        var writer = new FanControlWriter(client);
        var target = new ControlObject("fan:oem0", ControlObjectKinds.Fan, "Fixture fan",
            new(ControlOperatingSystems.Windows, ControlVendors.Oem), []);
        var capability = new ControlCapability("fan.lock-maximum", ControlValueKinds.Toggle, true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            switch (operation)
            {
                case "write":
                    await writer.WriteAsync(target, capability, new(capability.Id, Toggle: true), cancellation.Token);
                    break;
                case "restore":
                    await writer.RestoreAsync(target, capability, new HashSet<string> { "fan.duty" }, cancellation.Token);
                    break;
                case "read":
                    await writer.ReadAsync(target, capability, cancellation.Token);
                    break;
                case "curve":
                    await writer.ReadCurveAsync(target, cancellation.Token);
                    break;
            }
        });
        Assert.Equal(0, opened);
    }

    private sealed class EnvironmentInfo : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
