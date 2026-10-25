using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class JsonOptimizationProtectionStoreTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        $"rm-optimization-protection-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoadsLegacyTargetStateAndSoftwareKindAsCanonicalValues()
    {
        var target = CreateTarget() with
        {
            TargetType = "PhysicalDisk",
            SoftwareKind = "HighPerformance",
            State = "Stale"
        };
        await WriteTargetsAsync([target]);

        var store = CreateStore();
        var restored = Assert.Single(await store.LoadAsync(CancellationToken.None));

        Assert.Equal(OptimizationReportTargetTypes.PhysicalDisk, restored.TargetType);
        Assert.Equal("high_performance", restored.SoftwareKind);
        Assert.Equal(OptimizationProtectionStates.Stale, restored.State);

        await store.SaveAsync([restored], CancellationToken.None);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(StoragePath()));
        var savedTarget = Assert.Single(saved.RootElement.GetProperty("targets").EnumerateArray());
        Assert.Equal(OptimizationReportTargetTypes.PhysicalDisk,
            savedTarget.GetProperty("targetType").GetString());
        Assert.Equal("high_performance", savedTarget.GetProperty("softwareKind").GetString());
        Assert.Equal(OptimizationProtectionStates.Stale,
            savedTarget.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("targetType", "unknown_target")]
    [InlineData("softwareKind", "unknown_software_kind")]
    [InlineData("state", "unknown_state")]
    public async Task UnconvertibleOldValuesAreBackedUpAndStartEmpty(string field, string value)
    {
        var target = CreateTarget() with
        {
            TargetType = field == "targetType" ? value : CreateTarget().TargetType,
            SoftwareKind = field == "softwareKind" ? value : CreateTarget().SoftwareKind,
            State = field == "state" ? value : CreateTarget().State
        };
        await WriteTargetsAsync([target]);

        var original = await File.ReadAllBytesAsync(StoragePath());
        Assert.Empty(await CreateStore().LoadAsync(CancellationToken.None));
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StoragePath())!, "optimization-protection.json.*.backup"));
        Assert.Equal(original, await File.ReadAllBytesAsync(backup));
    }

    [Fact]
    public async Task LegacyDisplayTextIsBackedUpExactlyAndMigratedOnce()
    {
        const string original = """
            [{"id":"protect:app","targetType":"software","targetKey":"software:app","displayName":"App display",
              "softwareId":"app","softwareName":"App","softwareKind":"other","protectedAt":"2026-10-24T00:00:00Z",
              "protectedReason":"用户从性能优化报告中设为保护级。","createdFromReportId":"report:app","lastVerifiedAt":null,
              "state":"active","allowsPlacementAvoidance":true,"protectionLevel":2}]
            """;
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath())!);
        await File.WriteAllTextAsync(StoragePath(), original);
        var originalBytes = await File.ReadAllBytesAsync(StoragePath());
        var target = Assert.Single(await CreateStore().LoadAsync(default));
        Assert.Equal("App", target.SoftwareName);
        Assert.Equal("report", target.ProtectionSource);
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StoragePath())!, "optimization-protection.json.*.backup"));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(backup));
        var current = await File.ReadAllBytesAsync(StoragePath());
        using var json = JsonDocument.Parse(current);
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        var item = Assert.Single(json.RootElement.GetProperty("targets").EnumerateArray());
        Assert.False(item.TryGetProperty("displayName", out _));
        Assert.False(item.TryGetProperty("protectedReason", out _));
        Assert.Equal("report", item.GetProperty("protectionSource").GetString());
        Assert.Single(await CreateStore().LoadAsync(default));
        Assert.Equal(current, await File.ReadAllBytesAsync(StoragePath()));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StoragePath())!, "optimization-protection.json.*.backup"));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("")]
    public async Task UnconvertibleOldFileStartsEmptyWithExactBackup(string original)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath())!);
        await File.WriteAllTextAsync(StoragePath(), original);
        var originalBytes = await File.ReadAllBytesAsync(StoragePath());
        Assert.Empty(await CreateStore().LoadAsync(default));
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StoragePath())!, "optimization-protection.json.*.backup"));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(backup));
        Assert.Empty(await CreateStore().LoadAsync(default));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StoragePath())!, "optimization-protection.json.*.backup"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(testRoot, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    private JsonOptimizationProtectionStore CreateStore()
        => new(new TestHostEnvironment(
            Directory.CreateDirectory(Path.Combine(testRoot, "Resource Manager-APP")).FullName));

    private async Task WriteTargetsAsync(IReadOnlyList<ProtectedOptimizationTarget> targets)
    {
        var path = StoragePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(targets, JsonOptions));
    }

    private string StoragePath()
        => Path.Combine(testRoot, "Config", "optimization-protection.json");

    private static ProtectedOptimizationTarget CreateTarget()
        => new(
            "protect:app",
            OptimizationReportTargetTypes.Software,
            "software:app",
            "app",
            "App",
            "other",
            DateTimeOffset.Parse("2026-10-24T00:00:00Z"),
            "user",
            "report:app",
            null,
            OptimizationProtectionStates.Active,
            AllowsPlacementAvoidance: true);

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
