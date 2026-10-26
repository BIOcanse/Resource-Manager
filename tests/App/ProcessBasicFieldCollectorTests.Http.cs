using System.Net;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using NetworkTests = Resource_Manager_APP.Tests.ProcessNetworkFieldCollectorTests;

namespace Resource_Manager_APP.Tests;

public sealed partial class ProcessBasicFieldCollectorTests
{
    [Fact]
    public async Task RealHttpUnifiedWorkerIsolatesSourcesRecoversAndStopsReadingAfterCancellation()
    {
        var phase = 0; var api = new Api(); var captured = DateTimeOffset.UtcNow.AddSeconds(-5);
        var network = new NetworkTests.NetworkReader(_ => Volatile.Read(ref phase) == 1 ? throw new InvalidOperationException("network failed") : Network(captured));
        using var fixture = Fixture(api, network, () => Volatile.Read(ref phase) == 3
            ? throw new InvalidOperationException("inventory failed") : [Process.GetCurrentProcess()]);
        await using var app = await NetworkTests.StartAsync(fixture); using var client = NetworkTests.Client(app);
        using var catalog = JsonDocument.Parse(await client.GetStringAsync("/api/resource-breakdown/fields/catalog"));
        Assert.Equal(34, catalog.RootElement.GetArrayLength());
        Assert.Equal(19, catalog.RootElement.EnumerateArray().Count(f => f.GetProperty("sourceId").GetString() == "windows.process.query"));
        var quantities = catalog.RootElement.EnumerateArray()
            .Where(field => field.TryGetProperty("reading", out var reading) && reading.ValueKind == JsonValueKind.Object)
            .Select(field => field.GetProperty("reading").GetProperty("quantity").GetString()).ToArray();
        Assert.Equal(4, quantities.Length);
        Assert.Contains("working_set_including_shared", quantities);
        Assert.Contains("private_working_set", quantities);
        Assert.Contains("private_commit", quantities);
        Assert.Contains("peak_private_commit", quantities);
        var factFields = catalog.RootElement.EnumerateArray()
            .Where(field => field.GetProperty("supportsResourceFacts").GetBoolean())
            .Select(field => field.GetProperty("name").GetString()).Order().ToArray();
        Assert.Equal(new[] { "process_all_cpu_usage_actual", "process_all_network_receive_bytes_attributed", "process_all_network_receive_rate_attributed", "process_all_network_send_bytes_attributed", "process_all_network_send_rate_attributed", "process_all_physical_disk_read_bytes_attributed", "process_all_physical_disk_write_bytes_attributed", "process_all_private_commit_actual", "process_all_private_working_set_actual", "process_all_working_set_actual" }, factFields);
        Assert.False(Assert.Single(catalog.RootElement.EnumerateArray(), field => field.GetProperty("name").GetString()
            == "process_all_peak_private_commit_actual").GetProperty("supportsResourceFacts").GetBoolean());
        foreach (var field in catalog.RootElement.EnumerateArray())
        {
            var name = field.GetProperty("name").GetString()!;
            var expectedTotal = name is "process_all_working_set_actual" or "process_all_private_working_set_actual"
                ? "memory.usage" : name == "process_all_private_commit_actual" ? "virtualMemory.usage" : name == "process_all_cpu_usage_actual" ? "cpu.usage" : name.StartsWith("process_all_network_") ? "network.traffic" : name.StartsWith("process_all_physical_disk_") ? "disk.io" : null;
            Assert.Equal(expectedTotal, field.GetProperty("resourceFactTotalMetricId").GetString());
        }
        string[] names = [Names[1], Names[5], NetworkNames[0]];
        var snapshotUrl = "/api/resource-breakdown/snapshot?ids=" + string.Join(',', names);
        var subscribeUrl = "/api/resource-monitor/subscribe?scope=bars&intervalMs=1000&ids=" + string.Join(',', names);
        using var initial = JsonDocument.Parse(await client.GetStringAsync(snapshotUrl));
        Assert.Equal(3, initial.RootElement.GetProperty("processFields").GetProperty("fields").GetArrayLength()); Assert.Equal(0, api.Opens); Assert.Equal(0, network.Reads);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "process-basic-mixed", path = subscribeUrl } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var first = await BasicFrame(stream, timeout.Token, true, true);
        var fields = first.RootElement.GetProperty("value").GetProperty("breakdown").GetProperty("processFields");
        Assert.Equal(2, fields.GetProperty("sources").GetArrayLength()); Assert.Equal(JsonValueKind.Null, fields.GetProperty("windowMilliseconds").ValueKind);
        Assert.Equal(2000, Assert.Single(fields.GetProperty("sources").EnumerateArray(), s => s.GetProperty("sourceId").GetString() == "windows.etw.network").GetProperty("windowMilliseconds").GetInt32());
        Volatile.Write(ref phase, 1); using var failedNetwork = await BasicFrame(stream, timeout.Token, true, false);
        Volatile.Write(ref api.Fault, "memory"); Volatile.Write(ref phase, 2);
        using var failedMemory = await BasicFrame(stream, timeout.Token, false, true);
        Assert.Equal("9007199254740993", BasicWireValue(failedMemory.RootElement.GetProperty("value").GetProperty("breakdown"), Names[5]).GetProperty("integerValue").GetString());
        Volatile.Write(ref api.Fault, ""); Volatile.Write(ref phase, 3);
        using var failedInventory = await BasicFrame(stream, timeout.Token, false, false);
        Assert.Equal(JsonValueKind.Null, BasicWireValue(failedInventory.RootElement.GetProperty("value").GetProperty("breakdown"), Names[5]).GetProperty("integerValue").ValueKind);
        Volatile.Write(ref phase, 2); using var recovered = await BasicFrame(stream, timeout.Token, true, true);
        Assert.Equal(0, api.HandleCalls); Assert.All(api.PrivateRequests, Assert.False);
        await timeout.CancelAsync(); response.Dispose(); await Task.Delay(300);
        var opens = Volatile.Read(ref api.Opens); var reads = network.Reads;
        using var cached = JsonDocument.Parse(await client.GetStringAsync(snapshotUrl));
        Assert.Equal("9007199254740993", BasicWireValue(cached.RootElement, Names[1]).GetProperty("integerValue").GetString());
        using var monitor = JsonDocument.Parse(await client.GetStringAsync("/api/resource-monitor/snapshot?scope=bars&ids=" + Names[5]));
        Assert.Equal("9007199254740993", BasicWireValue(monitor.RootElement.GetProperty("breakdown"), Names[5]).GetProperty("integerValue").GetString());
        Assert.Equal(opens, Volatile.Read(ref api.Opens)); Assert.Equal(reads, network.Reads);
        await Task.Delay(1300); Assert.Equal(opens, Volatile.Read(ref api.Opens)); Assert.Equal(reads, network.Reads); await app.StopAsync();
    }

    private static JsonElement BasicWireValue(JsonElement snapshot, string field)
        => Assert.Single(snapshot.GetProperty("processFields").GetProperty("processes")[0].GetProperty("values").EnumerateArray(), v => v.GetProperty("field").GetString() == field);

    private static async Task<JsonDocument> BasicFrame(StreamReader stream, CancellationToken token, bool memory, bool network)
    {
        while (!token.IsCancellationRequested)
        {
            var line = await stream.ReadLineAsync(token); Assert.NotNull(line); var frame = JsonDocument.Parse(line!);
            var wire = frame.RootElement.GetProperty("value").GetProperty("breakdown");
            if (wire.GetProperty("processFields").GetProperty("processes").GetArrayLength() > 0
                && (BasicWireValue(wire, Names[1]).GetProperty("integerValue").ValueKind != JsonValueKind.Null) == memory
                && (BasicWireValue(wire, NetworkNames[0]).GetProperty("integerValue").ValueKind != JsonValueKind.Null) == network)
                return frame;
            frame.Dispose();
        }
        throw new OperationCanceledException(token);
    }
}

