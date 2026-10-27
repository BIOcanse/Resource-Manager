using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Fact]
    public async Task SecurityHttpUsesSingleFetchIsolatesFailuresAndRecoversWithoutHistoricalValues()
    {
        var api = new WindowsSecurityInformationCollectorTests.Api();
        var collector = new WindowsSecurityInformationReader(api);
        var reader = new Reader(() => DeviceInformationTestData.Snapshot() with { SecurityInformation = collector.Read() });
        using var fixture = new Fixture(reader);
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        _ = ReadPublishedFields(fixture.Provider, app);
        Assert.Empty(api.Providers);
        Assert.Equal(0, api.Opens);

        for (var phase = 0; phase < 4; phase++)
        {
            api.Health[4] = phase == 1 ? (1, 2U) : (0, 0U);
            api.PolicyFactory = () => new() { PrivateStatus = phase == 1 ? unchecked((int)0x80004005) : 0 };
            api.OpenError = phase == 2 ? new COMException("unavailable") : null;
            var sampledHttpFields1 = await client.GetStringAsync("/api/device-topology/fields");
            var current = fixture.Provider.ReadState().InformationFields;
            var reads = api.Providers.Count;
            var opens = api.Opens;
            using var result = JsonDocument.Parse(sampledHttpFields1);
            var antivirus = Field(result.RootElement, "machine", "security_all_antivirus_state_reported");
            Assert.Equal("windows.security-center", antivirus.GetProperty("definition").GetProperty("sourceId").GetString());
            Assert.Equal("enum32", antivirus.GetProperty("definition").GetProperty("unit").GetString());
            if (phase == 1) Assert.Equal(JsonValueKind.Null, antivirus.GetProperty("value").ValueKind);
            else Assert.Equal("0", antivirus.GetProperty("value").GetString());
            Assert.Equal("3", Field(result.RootElement, "machine", "security_all_firewall_state_reported").GetProperty("value").GetString());
            var active = Field(result.RootElement, "machine", "security_all_windows_firewall_profiles_active_mask");
            if (phase == 2) Assert.Equal(JsonValueKind.Null, active.GetProperty("value").ValueKind);
            else Assert.Equal("6", active.GetProperty("value").GetString());
            foreach (var (name, code) in new[] { ("domain", "1"), ("private", "2"), ("public", "4") })
            {
                var identity = "windows-firewall-profile|" + name;
                Assert.Equal(code, Field(result.RootElement, identity, "security_all_windows_firewall_profile_type").GetProperty("value").GetString());
                var enabled = Field(result.RootElement, identity, "security_all_windows_firewall_enabled_configured");
                Assert.Equal("windows.firewall-policy", enabled.GetProperty("definition").GetProperty("sourceId").GetString());
                Assert.Equal("boolean", enabled.GetProperty("definition").GetProperty("valueKind").GetString());
                if (phase == 2 || phase == 1 && name == "private") Assert.Equal(JsonValueKind.Null, enabled.GetProperty("value").ValueKind);
                else Assert.Equal(name == "private", enabled.GetProperty("value").GetBoolean());
            }
            Assert.Equal("Machine Model", Field(result.RootElement, "machine", "machine_all_model_declared").GetProperty("value").GetString());
            Assert.Same(current, fixture.Provider.ReadState().InformationFields);
            Assert.Equal(reads, api.Providers.Count);
            Assert.Equal(opens, api.Opens);
        }
        Assert.All(api.Policies, static policy => Assert.Equal(1, policy.Disposals));
        await app.StopAsync();
    }

    [Fact]
    public async Task SecurityRestorationAndWholeSnapshotFailureClearAllValuesUntilNextCapture()
    {
        var raw = DeviceInformationTestData.Snapshot() with
        { SecurityInformation = new WindowsSecurityInformationReader(new WindowsSecurityInformationCollectorTests.Api()).Read() };
        var persisted = new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion, "test", 7,
            new DeviceTopologySemanticComparer().ComputeHash(raw), raw);
        var reader = new Reader(raw, new InvalidOperationException("whole source unavailable"), raw);
        using var fixture = new Fixture(reader, persisted);
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        using var restored = JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        AssertSecurityValuesCleared(restored.RootElement);
        Assert.Equal(0, reader.ReadCount);
        var sampledHttpFields2 = await client.GetStringAsync("/api/device-topology/fields");
        using var first = JsonDocument.Parse(sampledHttpFields2);
        Assert.Equal("0", Field(first.RootElement, "machine", "security_all_antivirus_state_reported").GetProperty("value").GetString());
        Assert.True(Field(first.RootElement, "windows-firewall-profile|private", "security_all_windows_firewall_enabled_configured").GetProperty("value").GetBoolean());
        var sampledHttpFields3 = await client.GetStringAsync("/api/device-topology/fields");
        using var failed = JsonDocument.Parse(sampledHttpFields3);
        AssertSecurityValuesCleared(failed.RootElement);
        var sampledHttpFields4 = await client.GetStringAsync("/api/device-topology/fields");
        using var recovered = JsonDocument.Parse(sampledHttpFields4);
        Assert.Equal("6", Field(recovered.RootElement, "machine", "security_all_windows_firewall_profiles_active_mask").GetProperty("value").GetString());
        Assert.Equal(3, reader.ReadCount);
        await app.StopAsync();
    }

    [Fact]
    public async Task OriginalWorkerAndUnifiedSecurityStreamReadOnlyWithDemandAndKeepDisplayAfterCancellation()
    {
        var api = new WindowsSecurityInformationCollectorTests.Api();
        var collector = new WindowsSecurityInformationReader(api);
        var reader = new Reader(() => DeviceInformationTestData.Snapshot() with { SecurityInformation = collector.Read() });
        using var fixture = new Fixture(reader);
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        _ = ReadPublishedFields(fixture.Provider, app);
        Assert.Empty(api.Providers);
        Assert.Equal(0, api.Opens);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                new { id = "security-worker", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var message = JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        var snapshot = message.RootElement.GetProperty("value");
        Assert.Equal("0", Field(snapshot, "machine", "security_all_antivirus_state_reported").GetProperty("value").GetString());
        Assert.False(Field(snapshot, "windows-firewall-profile|domain", "security_all_windows_firewall_enabled_configured").GetProperty("value").GetBoolean());
        Assert.True(api.Opens >= 1);
        Assert.Equal(api.Opens * 2, api.Providers.Count);
        Assert.All(api.Policies, static policy => Assert.Equal(1, policy.Disposals));
        await timeout.CancelAsync();
        response.Dispose();
        await Task.Delay(250);
        var reads = reader.ReadCount;
        var current = fixture.Provider.ReadState().InformationFields;
        using var cached = JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        Assert.Equal("0", Field(cached.RootElement, "machine", "security_all_antivirus_state_reported").GetProperty("value").GetString());
        await Task.Delay(1300);
        Assert.Equal(reads, reader.ReadCount);
        Assert.Equal(reads, api.Opens);
        Assert.Same(current, fixture.Provider.ReadState().InformationFields);
        await app.StopAsync();
    }

    private static void AssertSecurityValuesCleared(JsonElement snapshot)
    {
        foreach (var name in new[] { "security_all_antivirus_state_reported", "security_all_firewall_state_reported", "security_all_windows_firewall_profiles_active_mask" })
            Assert.Equal(JsonValueKind.Null, Field(snapshot, "machine", name).GetProperty("value").ValueKind);
        foreach (var name in new[] { "domain", "private", "public" })
            foreach (var field in new[] { "security_all_windows_firewall_profile_type", "security_all_windows_firewall_enabled_configured" })
                Assert.Equal(JsonValueKind.Null, Field(snapshot, "windows-firewall-profile|" + name, field).GetProperty("value").ValueKind);
    }
}
