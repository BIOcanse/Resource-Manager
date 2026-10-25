using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Control;

namespace Resource_Manager_APP.Tests;

public sealed class ControlInstanceMigrationTests
{
    [Fact]
    public async Task SanitizedInstalledC15FileMigratesAllRegistrationsAndPreservesExactLegacyBackupOnce()
    {
        using var fixture = new Fixture();
        var original = InstalledFixture();
        Assert.Equal("DCD77115F9E99A851279CE19D42145A055D9A2F32224EE192F3B71D96B13411A", Convert.ToHexString(SHA256.HashData(original)));
        await File.WriteAllBytesAsync(fixture.RegistryPath, original);
        var first = await fixture.Open().ReadAsync(default);
        Assert.Equal(6, first.Instances.Count);
        Assert.All(first.Instances, item => Assert.False(item.IsPresent));
        var migrated = await File.ReadAllBytesAsync(fixture.RegistryPath);
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.LegacyBackup));
        using var oldDocument = JsonDocument.Parse(original);
        using var newDocument = JsonDocument.Parse(migrated);
        Assert.Equal(2, newDocument.RootElement.GetProperty("formatVersion").GetInt32());
        var oldItems = oldDocument.RootElement.GetProperty("instances").EnumerateArray().ToArray();
        Assert.Equal(new[] { "gpu:model:0123456789abcdef", "gpu:uid:gpu-11111111-2222-4333-8444-555555555555",
            "cpu:model:0123456789abcdef", "fan:oem0", "fan:gpu1", "fan:oem1" },
            oldItems.Select(item => item.GetProperty("id").GetString()));
        var newItems = newDocument.RootElement.GetProperty("instances").EnumerateArray().ToArray();
        foreach (var old in oldItems)
        {
            var id = old.GetProperty("id").GetString();
            var current = Assert.Single(newItems, item => item.GetProperty("id").GetString() == id);
            Assert.False(current.TryGetProperty("displayName", out _));
            foreach (var property in new[] { "id", "kind", "platform", "gpuAttachment", "identityIsUnique", "defaultSettings" })
                Assert.Equal(JsonSerializer.Serialize(old.GetProperty(property)), JsonSerializer.Serialize(current.GetProperty(property)));
            foreach (var property in new[] { "firstSeenAt", "lastSeenAt" })
                Assert.Equal(old.GetProperty(property).GetDateTimeOffset(), current.GetProperty(property).GetDateTimeOffset());
            if (old.GetProperty("kind").GetString() != "fan")
            {
                Assert.Equal(old.GetProperty("displayName").GetString(), current.GetProperty("reportedName").GetString());
                Assert.Equal(JsonValueKind.Null, current.GetProperty("adapterIndex").ValueKind);
            }
            else Assert.Equal(JsonValueKind.Null, current.GetProperty("reportedName").ValueKind);
        }
        Assert.Equal(0, newItems.Single(item => item.GetProperty("id").GetString() == "fan:oem0").GetProperty("ordinal").GetInt32());
        Assert.Equal(1, newItems.Single(item => item.GetProperty("id").GetString() == "fan:oem1").GetProperty("ordinal").GetInt32());
        var gpuFan = newItems.Single(item => item.GetProperty("id").GetString() == "fan:gpu1");
        Assert.Equal(1, gpuFan.GetProperty("adapterIndex").GetInt32());
        Assert.Equal(JsonValueKind.Null, gpuFan.GetProperty("ordinal").ValueKind);
        Assert.Contains(fixture.Log.Entries, item => item.Level == LogLevel.Information && item.Text.Contains(fixture.LegacyBackup));

        var backupTime = File.GetLastWriteTimeUtc(fixture.LegacyBackup);
        var restarted = await fixture.Open().ReadAsync(default);
        Assert.Equal(6, restarted.Instances.Count);
        Assert.Equal(migrated, await File.ReadAllBytesAsync(fixture.RegistryPath));
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.LegacyBackup));
        Assert.Equal(backupTime, File.GetLastWriteTimeUtc(fixture.LegacyBackup));
        Assert.Single(fixture.Log.Entries, item => item.Level == LogLevel.Information && item.Text.Contains(fixture.LegacyBackup));
    }

    [Fact]
    public async Task LegacyNamesAreNeverParsedIntoAnIdentityOrAnIndex()
    {
        using var fixture = new Fixture();
        var legacy = Legacy("fan:opaque", "风扇99") with { Ordinal = null, AdapterIndex = null };
        await File.WriteAllTextAsync(fixture.RegistryPath, JsonSerializer.Serialize(new { instances = new[] { legacy } }, JsonSerializerOptions.Web));
        var instance = Assert.Single((await fixture.Open().ReadAsync(default)).Instances).Instance;
        Assert.Equal("fan:opaque", instance.Id);
        Assert.Null(instance.ReportedName);
        Assert.Null(instance.Ordinal);
        Assert.Null(instance.AdapterIndex);
    }

    [Fact]
    public async Task ExistingIndexFactsRemoveOnlyRecognizedFabricatedLabelsAndKeepReportedNames()
    {
        using var fixture = new Fixture();
        var legacy = new[] { Legacy("fan:opaque", "显卡风扇控制器（GPU7）") with { AdapterIndex = 7 },
            Legacy("fan:oem2", "Vendor cooling pump") };
        await File.WriteAllTextAsync(fixture.RegistryPath, JsonSerializer.Serialize(new { instances = legacy }, JsonSerializerOptions.Web));
        var result = (await fixture.Open().ReadAsync(default)).Instances;
        var fabricated = result.Single(item => item.Instance.Id == "fan:opaque").Instance;
        Assert.Null(fabricated.ReportedName);
        Assert.Equal(7, fabricated.AdapterIndex);
        Assert.Null(fabricated.Ordinal);
        Assert.Equal("Vendor cooling pump", result.Single(item => item.Instance.Id == "fan:oem2").Instance.ReportedName);
    }

    [Fact]
    public async Task CurrentReportedNameIsPreservedEvenWhenItResemblesAnOldGeneratedLabel()
    {
        using var fixture = new Fixture();
        fixture.Catalog.Objects = [new("fan:opaque", "fan", "风扇99", new("windows", "oem"), [])];
        await File.WriteAllTextAsync(fixture.RegistryPath, JsonSerializer.Serialize(
            new { instances = new[] { Legacy("fan:opaque", "风扇1") } }, JsonSerializerOptions.Web));
        var instance = Assert.Single((await fixture.Open().ReadAsync(default)).Instances).Instance;
        Assert.Equal("fan:opaque", instance.Id);
        Assert.Equal("风扇99", instance.ReportedName);
        Assert.Null(instance.Ordinal);
    }

    [Fact]
    public async Task MissingCurrentNameDoesNotEraseAHistoricalReportedNameDuringMigration()
    {
        using var fixture = new Fixture();
        fixture.Catalog.Objects = [new("fan:oem2", "fan", null, new("windows", "oem"), [], Ordinal: 2)];
        await File.WriteAllTextAsync(fixture.RegistryPath, JsonSerializer.Serialize(
            new { instances = new[] { Legacy("fan:oem2", "Vendor cooling pump") } }, JsonSerializerOptions.Web));
        Assert.Equal("Vendor cooling pump", Assert.Single((await fixture.Open().ReadAsync(default)).Instances).Instance.ReportedName);
    }

    [Fact]
    public async Task AbsentOptionalLegacyNameDoesNotDiscardTheRegistration()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.RegistryPath, """
            {"instances":[{"id":"fan:opaque","kind":"fan","platform":{"operatingSystem":"windows","vendor":"oem"},
              "identityIsUnique":false,"firstSeenAt":"2026-10-23T00:00:00Z","lastSeenAt":"2026-10-23T00:00:00Z","defaultSettings":[]}]}
            """);
        var item = Assert.Single((await fixture.Open().ReadAsync(default)).Instances).Instance;
        Assert.Equal("fan:opaque", item.Id);
        Assert.Null(item.ReportedName);
        Assert.Null(item.Ordinal);
    }

    [Fact]
    public async Task RestoredLegacyFileGetsADifferentBackupWithoutReplacingTheExistingBackup()
    {
        using var fixture = new Fixture();
        var original = InstalledFixture();
        await File.WriteAllBytesAsync(fixture.RegistryPath, original);
        const string earlierBackup = "previous backup is retained";
        await File.WriteAllTextAsync(fixture.LegacyBackup, earlierBackup);
        Assert.Equal(6, (await fixture.Open().ReadAsync(default)).Instances.Count);
        Assert.Equal(earlierBackup, await File.ReadAllTextAsync(fixture.LegacyBackup));
        var backup = Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(fixture.RegistryPath)!, "instances.json.legacy-v1.*.bak"));
        Assert.Equal(original, await File.ReadAllBytesAsync(backup));
        var migrated = await File.ReadAllBytesAsync(fixture.RegistryPath);
        Assert.Equal(6, (await fixture.Open().ReadAsync(default)).Instances.Count);
        Assert.Equal(migrated, await File.ReadAllBytesAsync(fixture.RegistryPath));
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(fixture.RegistryPath)!, "instances.json.legacy-v1.*.bak"));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"instances\":[{\"id\":\"fan:oem0\",\"displayName\":\"风扇1\"}]}")]
    [InlineData("{\"formatVersion\":1,\"instances\":[]}")]
    [InlineData("{\"formatVersion\":\"2\",\"instances\":[]}")]
    [InlineData("{\"instances\":[{\"id\":\"fan:oem0\",\"kind\":\"fan\",\"platform\":null,\"identityIsUnique\":false,\"firstSeenAt\":\"2026-10-23T00:00:00Z\",\"lastSeenAt\":\"2026-10-23T00:00:00Z\",\"defaultSettings\":[]}]}")]
    [InlineData("{\"formatVersion\":2,\"instances\":[{\"id\":\"fan:oem0\",\"kind\":\"fan\",\"platform\":null,\"defaultSettings\":[]}]}")]
    public async Task UnconvertibleFilesStayUntouchedAndReadsLogEmptyEvenWithNewlyDiscoveredObjects(string original)
    {
        using var fixture = new Fixture();
        fixture.Catalog.Objects = [new("fan:oem0", "fan", null, new("windows", "oem"), [], Ordinal: 0)];
        await File.WriteAllTextAsync(fixture.RegistryPath, original);
        var registry = fixture.Open();
        Assert.Empty((await registry.ReadAsync(default)).Instances);
        Assert.Empty((await registry.ReadAsync(default)).Instances);
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.RegistryPath));
        Assert.False(File.Exists(fixture.LegacyBackup));
        Assert.False(File.Exists(fixture.UnconvertedBackup));
        Assert.Single(fixture.Log.Entries, item => item.Level == LogLevel.Warning && item.Error is not null);
        Assert.Empty((await fixture.Open().ReadAsync(default)).Instances);
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.RegistryPath));
    }

    [Fact]
    public async Task ExplicitRefreshAfterBadReadPreservesOriginalBeforeSavingCurrentFacts()
    {
        using var fixture = new Fixture();
        var original = Encoding.UTF8.GetBytes("{invalid original bytes}");
        await File.WriteAllBytesAsync(fixture.RegistryPath, original);
        fixture.Catalog.Objects = [new("fan:oem0", "fan", null, new("windows", "oem"), [], Ordinal: 0)];
        var registry = fixture.Open();
        Assert.Empty((await registry.ReadAsync(default)).Instances);
        Assert.Single((await registry.RefreshAsync(default)).Instances);
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.UnconvertedBackup));
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.RegistryPath));
        Assert.Equal(2, saved.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.False(Assert.Single(saved.RootElement.GetProperty("instances").EnumerateArray()).TryGetProperty("displayName", out _));
        Assert.Single((await fixture.Open().ReadAsync(default)).Instances);
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.UnconvertedBackup));
    }

    [Fact]
    public async Task ExplicitForgetAfterBadReadCanRecoverWithoutRevokingBeforeOriginalIsBackedUp()
    {
        using var fixture = new Fixture();
        const string original = "unconvertible";
        await File.WriteAllTextAsync(fixture.RegistryPath, original);
        var registry = fixture.Open();
        Assert.Empty((await registry.ReadAsync(default)).Instances);
        Assert.Empty((await registry.ForgetAsync("disconnected", default)).Instances);
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.UnconvertedBackup));
        Assert.Equal(new[] { "disconnected" }, fixture.Plane.Forgotten);
        Assert.Empty((await fixture.Open().ReadAsync(default)).Instances);
    }

    [Fact]
    public async Task ReadOnlyLegacyConversionDoesNotRewriteOrCreateBackup()
    {
        using var fixture = new Fixture();
        var original = InstalledFixture();
        await File.WriteAllBytesAsync(fixture.RegistryPath, original);
        Assert.Equal(6, (await fixture.Open(StartupCapabilitySet.NormalReadOnly).ReadAsync(default)).Instances.Count);
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.RegistryPath));
        Assert.False(File.Exists(fixture.LegacyBackup));
    }

    private static LegacyControlInstance Legacy(string id, string name)
        => new(id, "fan", name, new("windows", "oem"), null, false,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1), []);

    private static byte[] InstalledFixture()
    {
        using var stream = typeof(ControlInstanceMigrationTests).Assembly.GetManifestResourceStream(
            "Resource_Manager_APP.Tests.Fixtures.Control.control-instances.installed-c15.json")
            ?? throw new InvalidOperationException("Installed C15 migration fixture is missing.");
        using var result = new MemoryStream();
        stream.CopyTo(result);
        return result.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "rm-control-instance-migration-" + Guid.NewGuid().ToString("N"));
        public string RegistryPath => Path.Combine(root, "UserData", "Control", "instances.json");
        public string LegacyBackup => RegistryPath + ".legacy-v1.bak";
        public string UnconvertedBackup => RegistryPath + ".unconverted.bak";
        public Catalog Catalog { get; } = new();
        public Plane Plane { get; } = new();
        public CaptureLog Log { get; } = new();
        public Fixture() => Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);
        public JsonControlInstanceRegistry Open(StartupCapabilitySet? capabilities = null)
            => new(new Environment(root), Catalog, Plane, Log, capabilities);
        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(RegistryPath)!)) File.Delete(file);
            foreach (var directory in new[] { Path.GetDirectoryName(RegistryPath)!, Path.Combine(root, "UserData"), root })
                Directory.Delete(directory);
        }
    }

    private sealed class Catalog : IControlObjectCatalog
    {
        public IReadOnlyList<ControlObject> Objects { get; set; } = [];
        public ControlObjectCatalog ReadObjects() => new(Objects, DateTimeOffset.UtcNow);
    }

    private sealed class Plane : IControlPlane
    {
        public List<string> Forgotten { get; } = [];
        public Task ForgetInstanceAsync(string id, CancellationToken token) { Forgotten.Add(id); return Task.CompletedTask; }
        public Task<ControlStateView> ReadStateAsync(CancellationToken token) => Task.FromResult(new ControlStateView(ControlDesiredState.Empty, ControlApplyReport.Empty));
        public Task<ControlStateView> RetryObjectAsync(string id, CancellationToken token) => ReadStateAsync(token);
        public Task<ControlStateView> SetObjectSettingsAsync(string id, IReadOnlyList<ControlSetting> settings, CancellationToken token) => ReadStateAsync(token);
        public Task<ControlStateView> ApplyDesiredStateAsync(ControlDesiredState desired, CancellationToken token) => ReadStateAsync(token);
        public Task<ControlApplyReport> ReassertAsync(CancellationToken token) => Task.FromResult(ControlApplyReport.Empty);
    }

    private sealed class CaptureLog : ILogger<JsonControlInstanceRegistry>
    {
        public List<(LogLevel Level, Exception? Error, string Text)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, error, formatter(state, error)));
    }

    private sealed class Environment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
