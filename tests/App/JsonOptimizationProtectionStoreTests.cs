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
        var savedTarget = Assert.Single(saved.RootElement.EnumerateArray());
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
    public async Task UnknownStoredValuesFailWithInvalidDataException(string field, string value)
    {
        var target = CreateTarget() with
        {
            TargetType = field == "targetType" ? value : CreateTarget().TargetType,
            SoftwareKind = field == "softwareKind" ? value : CreateTarget().SoftwareKind,
            State = field == "state" ? value : CreateTarget().State
        };
        await WriteTargetsAsync([target]);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateStore().LoadAsync(CancellationToken.None));
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
            "App",
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
