using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Fact]
    public async Task ProcessorSocketWorkerPublishesFailureRecoveryAndEmptyTableWhileGetAndCancelDoNotRead()
    {
        var good = SmbiosFirmwareInformationCollectorTests.SocketTable(SmbiosFirmwareInformationCollectorTests.Processor(0x1234));
        var empty = SmbiosFirmwareInformationCollectorTests.SocketTable();
        var phase = 0;
        var rawReads = 0;
        var reader = new Reader(() => {
            var capture = SmbiosFirmwareInformationReader.Read(() => {
                Interlocked.Increment(ref rawReads);
                return Volatile.Read(ref phase) switch { 1 => null, 3 => empty, _ => good };
            });
            return DeviceInformationTestData.Snapshot() with { FirmwareDeclarations = capture?.Declarations };
        });
        using var fixture = new Fixture(reader);
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        using var initial = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal(0, Volatile.Read(ref rawReads));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream") {
            Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                new { id = "processor-sockets", path = "/api/device-topology/fields/subscribe?intervalMs=1000" }
            } })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var success = await NextSocketFrame(stream, value => HasSocketValue(value, "CPU0"), timeout.Token);
        var field = Field(success.RootElement.GetProperty("value"), "smbios-cpu-socket|1234", "cpu_all_socket_designation");
        Assert.Equal("smbios.type4", field.GetProperty("definition").GetProperty("sourceId").GetString());
        Volatile.Write(ref phase, 1);
        using var failure = await NextSocketFrame(stream, value => HasSocketValue(value, null), timeout.Token);
        Assert.NotNull(fixture.Provider.ReadState().InformationFields.Devices.Single(device => device.DeviceIdentity == "smbios-cpu-socket|1234"));
        Volatile.Write(ref phase, 2);
        using var recovery = await NextSocketFrame(stream, value => HasSocketValue(value, "CPU0"), timeout.Token);
        Volatile.Write(ref phase, 3);
        using var removed = await NextSocketFrame(stream, value => !value.GetProperty("devices").EnumerateArray()
            .Any(device => device.GetProperty("objectType").GetString() == "cpu_socket"), timeout.Token);
        Volatile.Write(ref phase, 4);
        using var final = await NextSocketFrame(stream, value => HasSocketValue(value, "CPU0"), timeout.Token);
        await timeout.CancelAsync();
        response.Dispose();
        await Task.Delay(300);
        var reads = Volatile.Read(ref rawReads);
        var current = fixture.Provider.ReadState().InformationFields;
        using var cached = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.True(HasSocketValue(cached.RootElement, "CPU0"));
        Assert.Equal(reads, Volatile.Read(ref rawReads));
        await Task.Delay(1300);
        Assert.Equal(reads, Volatile.Read(ref rawReads));
        Assert.Same(current, fixture.Provider.ReadState().InformationFields);
        await app.StopAsync();
    }

    private static bool HasSocketValue(JsonElement value, string? expected)
    {
        var sockets = value.GetProperty("devices").EnumerateArray().Where(device =>
            device.GetProperty("deviceIdentity").GetString() == "smbios-cpu-socket|1234").ToArray();
        if (sockets.Length != 1) return false;
        var field = sockets[0].GetProperty("fields")[0].GetProperty("value");
        return expected is null ? field.ValueKind == JsonValueKind.Null : field.GetString() == expected;
    }

    private static async Task<JsonDocument> NextSocketFrame(StreamReader stream, Func<JsonElement, bool> select, CancellationToken token)
    {
        while (true)
        {
            var frame = JsonDocument.Parse((await stream.ReadLineAsync(token))!);
            if (select(frame.RootElement.GetProperty("value"))) return frame;
            frame.Dispose();
        }
    }
}
