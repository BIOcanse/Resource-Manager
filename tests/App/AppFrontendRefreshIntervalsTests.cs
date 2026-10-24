using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Settings;

namespace Resource_Manager_APP.Tests;

public sealed class AppFrontendRefreshIntervalsTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        "resource-manager-frontend-refresh-interval-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("monitor", "responsive", 1_000)]
    [InlineData("monitor", "balanced", 2_000)]
    [InlineData("monitor", "lowPower", 3_000)]
    [InlineData("monitor", "quiet", 5_000)]
    [InlineData("resourceTable", "responsive", 1_000)]
    [InlineData("resourceTable", "balanced", 2_000)]
    [InlineData("resourceTable", "lowPower", 3_000)]
    [InlineData("resourceTable", "quiet", 5_000)]
    [InlineData("localSystem", "responsive", 30_000)]
    [InlineData("localSystem", "balanced", 60_000)]
    [InlineData("localSystem", "lowPower", 120_000)]
    [InlineData("localSystem", "quiet", 300_000)]
    public void ExplicitPreset_UsesTheSameIntervalInBothProfiles(
        string key,
        string preset,
        int expectedMilliseconds)
    {
        var defaults = AppSettingsDefaults.Create();
        var settings = defaults with
        {
            Performance = SetInterval(
                defaults.Performance,
                key,
                new AppPresetNumericSetting(
                    AppPresetNumericSettingModes.Preset,
                    preset,
                    CustomValue: 17_000))
        };

        var actual = AppFrontendRefreshIntervalPolicy.Expand(settings.Performance);

        Assert.Equal(expectedMilliseconds, GetInterval(actual.Foreground, key));
        Assert.Equal(expectedMilliseconds, GetInterval(actual.BackgroundOptimized, key));
    }

    [Theory]
    [InlineData("monitor", 12_345)]
    [InlineData("resourceTable", 23_456)]
    [InlineData("localSystem", 34_567)]
    public void CustomInterval_IsPreservedInBothProfiles(string key, int customValue)
    {
        var defaults = AppSettingsDefaults.Create();
        var settings = defaults with
        {
            Performance = SetInterval(
                defaults.Performance,
                key,
                new AppPresetNumericSetting(
                    AppPresetNumericSettingModes.Custom,
                    AppLogicRefreshIntervalPresets.Quiet,
                    customValue))
        };

        var actual = AppFrontendRefreshIntervalPolicy.Expand(settings.Performance);

        Assert.Equal(customValue, GetInterval(actual.Foreground, key));
        Assert.Equal(customValue, GetInterval(actual.BackgroundOptimized, key));
    }

    [Theory]
    [InlineData("monitor", 1_000, 3_000)]
    [InlineData("resourceTable", 1_000, 3_000)]
    [InlineData("localSystem", 30_000, 120_000)]
    public void AutomaticInterval_UsesResponsiveAndOptimizedLowPowerProfiles(
        string key,
        int expectedForeground,
        int expectedBackgroundOptimized)
    {
        var defaults = AppSettingsDefaults.Create();

        var actual = AppFrontendRefreshIntervalPolicy.Expand(defaults.Performance);

        Assert.Equal(expectedForeground, GetInterval(actual.Foreground, key));
        Assert.Equal(expectedBackgroundOptimized, GetInterval(actual.BackgroundOptimized, key));
    }

    [Fact]
    public void PublicationContainsThePresetCatalogueAndResetDefaults()
    {
        var actual = AppFrontendRefreshIntervalPolicy.Expand(
            AppSettingsDefaults.Create().Performance);

        Assert.Equal(
            new AppRefreshIntervalPresetCatalog(
                new AppRefreshIntervalPresetValues(1_000, 2_000, 3_000, 5_000),
                new AppRefreshIntervalPresetValues(1_000, 2_000, 3_000, 5_000),
                new AppRefreshIntervalPresetValues(30_000, 60_000, 120_000, 300_000)),
            actual.Presets);
        Assert.Equal(
            new AppRefreshIntervalDefaults(
                new AppPresetNumericSetting(AppPresetNumericSettingModes.Aotu, "responsive", 1_000),
                new AppPresetNumericSetting(AppPresetNumericSettingModes.Aotu, "responsive", 1_000),
                new AppPresetNumericSetting(AppPresetNumericSettingModes.Aotu, "balanced", 60_000)),
            actual.Defaults);
    }

    [Fact]
    public void UpdateResultSerializesExpandedIntervalsBesideTheirSettingsRevision()
    {
        var defaults = AppSettingsDefaults.Create();
        var settings = defaults with
        {
            Performance = defaults.Performance with
            {
                MonitorRefreshIntervalMs = new AppPresetNumericSetting(
                    AppPresetNumericSettingModes.Custom,
                    AppLogicRefreshIntervalPresets.Responsive,
                    12_345)
            }
        };
        var result = CreateResult(settings);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, JsonSerializerOptions.Web));
        var root = json.RootElement;
        var intervals = root.GetProperty("frontendRefreshIntervals");

        Assert.Equal(result.Revision, root.GetProperty("revision").GetString());
        Assert.Equal(
            result.Settings.Performance.MonitorRefreshIntervalMs.CustomValue,
            root.GetProperty("settings")
                .GetProperty("performance")
                .GetProperty("monitorRefreshIntervalMs")
                .GetProperty("customValue")
                .GetInt32());
        Assert.Equal(
            12_345,
            intervals.GetProperty("foreground").GetProperty("monitor").GetInt32());
        Assert.Equal(
            12_345,
            intervals.GetProperty("backgroundOptimized").GetProperty("monitor").GetInt32());
        Assert.Equal(
            3_000,
            intervals.GetProperty("presets").GetProperty("monitor").GetProperty("lowPower").GetInt32());
        Assert.Equal(
            "aotu",
            intervals.GetProperty("defaults").GetProperty("monitor").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task SettingsStoreKeepsIntervalsBoundToTheCommittedRevision()
    {
        var appRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "Resource Manager-APP")).FullName;
        var environment = new TestHostEnvironment(appRoot);
        var store = new JsonAppSettingsStore(environment);
        var initial = await store.LoadAsync(CancellationToken.None);
        var saved = await store.SaveAsync(initial.Settings with
        {
            Performance = initial.Settings.Performance with
            {
                ResourceTableRefreshIntervalMs = new AppPresetNumericSetting(
                    AppPresetNumericSettingModes.Custom,
                    AppLogicRefreshIntervalPresets.Balanced,
                    24_000)
            }
        }, CancellationToken.None);
        var loaded = await new JsonAppSettingsStore(environment)
            .LoadReadOnlyAsync(CancellationToken.None);

        Assert.Equal(saved.Revision, loaded.Revision);
        Assert.Equal(saved.FrontendRefreshIntervals, loaded.FrontendRefreshIntervals);
        Assert.Equal(24_000, loaded.FrontendRefreshIntervals.Foreground.ResourceTable);
        Assert.Equal(24_000, loaded.FrontendRefreshIntervals.BackgroundOptimized.ResourceTable);
    }

    [Fact]
    public async Task EmitsBackendIntervalFixtureWhenPathIsSet()
    {
        var defaults = AppSettingsDefaults.Create();
        var result = CreateResult(defaults);
        var serialized = JsonSerializer.Serialize(
            new { frontendRefreshIntervals = result.FrontendRefreshIntervals },
            JsonSerializerOptions.Web);

        using var json = JsonDocument.Parse(serialized);
        Assert.Equal(
            1_000,
            json.RootElement.GetProperty("frontendRefreshIntervals")
                .GetProperty("foreground").GetProperty("monitor").GetInt32());
        Assert.Equal(
            120_000,
            json.RootElement.GetProperty("frontendRefreshIntervals")
                .GetProperty("backgroundOptimized").GetProperty("localSystem").GetInt32());

        var fixturePath = Environment.GetEnvironmentVariable("RM_FRONTEND_INTERVAL_FIXTURE_PATH");
        if (string.IsNullOrWhiteSpace(fixturePath))
        {
            return;
        }

        var fixtureDirectory = Path.GetDirectoryName(fixturePath);
        if (!string.IsNullOrWhiteSpace(fixtureDirectory))
        {
            Directory.CreateDirectory(fixtureDirectory);
        }
        await File.WriteAllTextAsync(fixturePath, serialized);
    }

    public void Dispose()
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static AppSettingsUpdateResult CreateResult(AppSettings settings)
    {
        var digest = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(settings, JsonSerializerOptions.Web)));
        return new AppSettingsUpdateResult(
            settings,
            DateTimeOffset.UnixEpoch,
            "test/settings.json",
            new AppSettingsSourceMetadata(
                AppSettingsSourceKind.TestFixture,
                settings.Version,
                digest,
                digest,
                RewritePerformed: false));
    }

    private static AppPerformanceSettings SetInterval(
        AppPerformanceSettings performance,
        string key,
        AppPresetNumericSetting setting)
        => key switch
        {
            "monitor" => performance with { MonitorRefreshIntervalMs = setting },
            "resourceTable" => performance with { ResourceTableRefreshIntervalMs = setting },
            "localSystem" => performance with { LocalSystemRefreshIntervalMs = setting },
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };

    private static int GetInterval(AppRefreshIntervalValues values, string key)
        => key switch
        {
            "monitor" => values.Monitor,
            "resourceTable" => values.ResourceTable,
            "localSystem" => values.LocalSystem,
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
