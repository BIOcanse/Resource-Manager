using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace Resource_Manager_APP.Tests;

// Policy files written before 2.0.0 are folded into the three scheduling exclusions once, inside
// the store gate, and written back atomically; the application never sees the old mode fields.
public sealed class GpuPlacementPolicyMigrationTests
{
    [Theory]
    // enabledMode, schedulingMode, runtimeSchedulingMode, runtimeHotSwitchEnabled -> excluded, hot switch
    [InlineData("Disabled", "Precise", "Precise", true, true, true)]
    [InlineData("Preview", "Precise", "Precise", true, true, true)]
    [InlineData(null, "Precise", "Precise", true, true, true)]
    [InlineData("unknown", "Precise", "Precise", true, true, true)]
    [InlineData("Auto", "Precise", "Precise", true, false, true)]
    [InlineData("Manual", "Precise", "Precise", true, false, true)]
    [InlineData("Inherit", "Precise", "Precise", true, false, true)]
    [InlineData("Auto", null, null, true, false, true)]
    [InlineData("Auto", "Ordinary", "Ordinary", true, true, true)]
    [InlineData("Auto", "Precise", "Ordinary", true, false, false)]
    [InlineData("Auto", "Precise", "Ordinary", null, false, false)]
    [InlineData("Auto", "Precise", "Precise", null, false, null)]
    public async Task LegacySoftwareModesFoldIntoGpuExclusionAndRuntimeHotSwitch(
        string? enabledMode,
        string? schedulingMode,
        string? runtimeSchedulingMode,
        bool? runtimeHotSwitchEnabled,
        bool expectedGpuExcluded,
        bool? expectedRuntimeHotSwitch)
    {
        using var root = new OwnedRoot();
        var software = LegacySoftware("software", enabledMode, schedulingMode, runtimeSchedulingMode, runtimeHotSwitchEnabled);
        root.WritePolicyFile(LegacyDocument("1.3.0", [software], []));
        var logger = new RecordingLogger();

        var policy = Assert.Single((await root.CreateStore(logger).GetAsync(CancellationToken.None)).SoftwarePolicies);

        Assert.Equal(expectedGpuExcluded, policy.GpuSchedulingExcluded);
        Assert.False(policy.CpuSchedulingExcluded);
        Assert.False(policy.MemorySchedulingExcluded);
        Assert.Equal(expectedRuntimeHotSwitch, policy.RuntimeHotSwitchEnabled);
        Assert.Equal(GpuPlacementTargets.HighPerformanceGpu, policy.StartupTargetGpu);
        Assert.Equal(CpuMaximumOccupancyModes.SingleCcd, policy.CpuMaximumOccupancyMode);
        Assert.Equal(["core:1"], policy.CpuManualLockedPositionIds);
        Assert.Equal(55, policy.BaseScoreOverride);
        Assert.Empty(logger.Entries.Where(static entry => entry.Level >= LogLevel.Warning));
        AssertMigratedFile(root.ReadPolicyFile(), expectedGpuExcluded);
    }

    [Fact]
    public async Task MissingVersionIsMigratedAndDeadFieldsAreDropped()
    {
        using var root = new OwnedRoot();
        root.WritePolicyFile(LegacyDocument(null, [LegacySoftware("software", "Auto", "Precise", "Precise", true)], []));

        var policy = Assert.Single((await root.CreateStore(new RecordingLogger()).GetAsync(CancellationToken.None)).SoftwarePolicies);

        Assert.False(policy.GpuSchedulingExcluded);
        AssertMigratedFile(root.ReadPolicyFile(), expectedGpuExcluded: false);
    }

    [Fact]
    public async Task SecondLoadOfTheMigratedFileIsAPlainReadWithoutRewriting()
    {
        using var root = new OwnedRoot();
        root.WritePolicyFile(LegacyDocument("1.3.0",
            [LegacySoftware("software", "Disabled", "Precise", "Precise", true)],
            [LegacyProcess("software", "renderer", inherit: false, "Auto")]));
        var first = await root.CreateStore(new RecordingLogger()).GetAsync(CancellationToken.None);
        var migrated = root.ReadPolicyFile();
        var writeTime = File.GetLastWriteTimeUtc(root.PolicyPath);

        var logger = new RecordingLogger();
        var second = await root.CreateStore(logger).GetAsync(CancellationToken.None);

        Assert.Equal(migrated, root.ReadPolicyFile());
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(root.PolicyPath));
        Assert.Empty(logger.Entries);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(first),
            System.Text.Json.JsonSerializer.Serialize(second));
    }

    [Theory]
    [InlineData("3.0.0")]
    [InlineData("2.1.0")]
    public async Task NewerVersionIsRefusedWithoutTouchingTheFile(string version)
    {
        using var root = new OwnedRoot();
        root.WritePolicyFile(LegacyDocument(version, [LegacySoftware("software", "Auto", "Precise", "Precise", true)], []));
        var original = root.ReadPolicyFile();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            root.CreateStore(new RecordingLogger()).GetAsync(CancellationToken.None));

        Assert.Equal(original, root.ReadPolicyFile());
    }

    [Fact]
    public async Task UnreadableLegacyFileFailsWithoutPartialWrite()
    {
        using var root = new OwnedRoot();
        root.WritePolicyFile(new JsonObject
        {
            ["version"] = "1.3.0",
            ["softwarePolicies"] = "not-a-list"
        });
        var original = root.ReadPolicyFile();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            root.CreateStore(new RecordingLogger()).GetAsync(CancellationToken.None));

        Assert.Equal(original, root.ReadPolicyFile());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(root.PolicyPath)!));
    }

    [Theory]
    // software mode, process inherit, process mode, override allowed -> warning expected
    [InlineData("Auto", false, "Disabled", true, true)]
    [InlineData("Auto", false, "Preview", true, true)]
    [InlineData("Disabled", false, "Auto", true, true)]
    [InlineData("Disabled", false, "Manual", true, true)]
    [InlineData("Auto", false, "Auto", true, false)]
    [InlineData("Auto", false, "Inherit", true, false)]
    [InlineData("Auto", false, "unknown", true, false)]
    [InlineData("Auto", true, "Disabled", true, false)]
    [InlineData("Auto", false, "Disabled", false, false)]
    public async Task ProcessGpuModeOverrideIsDroppedAndOnlyARealDifferenceIsWarned(
        string softwareMode,
        bool processInherit,
        string processMode,
        bool processOverrideAllowed,
        bool expectWarning)
    {
        using var root = new OwnedRoot();
        var software = LegacySoftware("software", softwareMode, "Precise", "Precise", true);
        software["processOverrideAllowed"] = processOverrideAllowed;
        root.WritePolicyFile(LegacyDocument("1.3.0", [software], [LegacyProcess("software", "renderer-key", processInherit, processMode)]));
        var logger = new RecordingLogger();

        var document = await root.CreateStore(logger).GetAsync(CancellationToken.None);

        var process = Assert.Single(document.ProcessPolicies);
        Assert.Equal(processInherit, process.Inherit);
        Assert.Equal(processInherit ? null : 12d, process.BaseScoreOverride);
        Assert.True(process.StartupInterceptionEnabled);
        var warnings = logger.Entries.Where(static entry => entry.Level == LogLevel.Warning).ToArray();
        if (expectWarning)
        {
            Assert.Contains("renderer-key", Assert.Single(warnings).Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(warnings);
        }

        var file = root.ReadPolicyNode();
        var processNode = Assert.Single(file["processPolicies"]!.AsArray())!.AsObject();
        Assert.False(processNode.ContainsKey("enabledMode"));
        Assert.False(processNode.ContainsKey("explicitSelectionMode"));
    }

    [Fact]
    public async Task CurrentVersionKeepsEveryExclusionAndAlwaysWritesAllThree()
    {
        using var root = new OwnedRoot();
        var store = root.CreateStore(new RecordingLogger());
        await store.SaveSoftwarePolicyAsync(
            GpuPlacementPolicyDefaults.CreateSoftwarePolicy("software", "Software") with
            {
                CpuSchedulingExcluded = true,
                MemorySchedulingExcluded = false,
                GpuSchedulingExcluded = true
            },
            CancellationToken.None);
        await store.SaveSoftwarePolicyAsync(
            GpuPlacementPolicyDefaults.CreateSoftwarePolicy("other", "Other"),
            CancellationToken.None);

        var reloaded = (await root.CreateStore(new RecordingLogger()).GetAsync(CancellationToken.None)).SoftwarePolicies
            .ToDictionary(static policy => policy.SoftwareId);

        Assert.True(reloaded["software"].CpuSchedulingExcluded);
        Assert.False(reloaded["software"].MemorySchedulingExcluded);
        Assert.True(reloaded["software"].GpuSchedulingExcluded);
        Assert.False(reloaded["other"].CpuSchedulingExcluded || reloaded["other"].MemorySchedulingExcluded || reloaded["other"].GpuSchedulingExcluded);
        var file = root.ReadPolicyNode();
        Assert.Equal(GpuPlacementPolicyDocumentVersions.Current, file["version"]!.GetValue<string>());
        foreach (var node in file["softwarePolicies"]!.AsArray())
        {
            var policy = node!.AsObject();
            Assert.True(policy.ContainsKey("cpuSchedulingExcluded"));
            Assert.True(policy.ContainsKey("memorySchedulingExcluded"));
            Assert.True(policy.ContainsKey("gpuSchedulingExcluded"));
        }
    }

    private static void AssertMigratedFile(string text, bool expectedGpuExcluded)
    {
        var file = JsonNode.Parse(text)!.AsObject();
        Assert.Equal("2.0.0", file["version"]!.GetValue<string>());
        var policy = Assert.Single(file["softwarePolicies"]!.AsArray())!.AsObject();
        Assert.Equal(expectedGpuExcluded, policy["gpuSchedulingExcluded"]!.GetValue<bool>());
        Assert.False(policy["cpuSchedulingExcluded"]!.GetValue<bool>());
        Assert.False(policy["memorySchedulingExcluded"]!.GetValue<bool>());
        foreach (var removed in new[]
        {
            "enabledMode", "schedulingMode", "runtimeSchedulingMode", "explicitSelectionMode",
            "keepCpuProcessesOnMainCcd", "gpuExclusive", "absolutePerformanceModeEnabled", "cpuExclusiveLocksAffinity"
        })
        {
            Assert.False(policy.ContainsKey(removed), removed);
        }
    }

    private static JsonObject LegacyDocument(string? version, JsonObject[] software, JsonObject[] processes)
    {
        var document = new JsonObject
        {
            ["softwarePolicies"] = new JsonArray(software.Cast<JsonNode>().ToArray()),
            ["processPolicies"] = new JsonArray(processes.Cast<JsonNode>().ToArray()),
            ["updatedAt"] = "2026-10-01T00:00:00+00:00"
        };
        if (version is not null) document["version"] = version;
        return document;
    }

    private static JsonObject LegacySoftware(
        string softwareId,
        string? enabledMode,
        string? schedulingMode,
        string? runtimeSchedulingMode,
        bool? runtimeHotSwitchEnabled)
    {
        var policy = new JsonObject
        {
            ["softwareId"] = softwareId,
            ["softwareName"] = "Software",
            ["allowedProviders"] = new JsonArray("d3d-device-create-shim"),
            ["startupTargetGpu"] = GpuPlacementTargets.HighPerformanceGpu,
            ["explicitSelectionMode"] = "AllowHigh",
            ["runtimeHotSwitchEnabled"] = runtimeHotSwitchEnabled,
            ["preferredRuntimeSwitchMethod"] = GpuPlacementRuntimeSwitchMethods.FutureFrameTakeover,
            ["baseScoreOverride"] = 55,
            ["processOverrideAllowed"] = true,
            ["updatedAt"] = "2026-10-01T00:00:00+00:00",
            ["keepCpuProcessesOnMainCcd"] = true,
            ["gpuExclusive"] = true,
            ["absolutePerformanceModeEnabled"] = true,
            ["cpuMaximumOccupancyMode"] = CpuMaximumOccupancyModes.SingleCcd,
            ["cpuExclusiveLocksAffinity"] = true,
            ["cpuManualExclusivePositionIds"] = new JsonArray(),
            ["cpuManualLockedPositionIds"] = new JsonArray("core:1")
        };
        if (enabledMode is not null) policy["enabledMode"] = enabledMode;
        if (schedulingMode is not null) policy["schedulingMode"] = schedulingMode;
        if (runtimeSchedulingMode is not null) policy["runtimeSchedulingMode"] = runtimeSchedulingMode;
        return policy;
    }

    private static JsonObject LegacyProcess(string softwareId, string processKey, bool inherit, string enabledMode)
        => new()
        {
            ["softwareId"] = softwareId,
            ["processKey"] = processKey,
            ["processName"] = "renderer.exe",
            ["executablePath"] = @"C:\Apps\renderer.exe",
            ["inherit"] = inherit,
            ["enabledMode"] = enabledMode,
            ["allowedProviders"] = new JsonArray("d3d-device-create-shim"),
            ["explicitSelectionMode"] = "AllowLow",
            ["baseScoreOverride"] = 12,
            ["updatedAt"] = "2026-10-01T00:00:00+00:00",
            ["startupInterceptionEnabled"] = true
        };

    private sealed class OwnedRoot : IDisposable
    {
        private readonly string root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "rm-gpu-policy-migration-" + Guid.NewGuid().ToString("N"))).FullName;

        public string PolicyPath => Path.Combine(root, "UserData", "SoftwareProfiles", "gpu-placement-policies.local.json");

        public JsonGpuPlacementPolicyStore CreateStore(RecordingLogger logger) => new(new Environment(root), logger);

        public void WritePolicyFile(JsonObject document)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PolicyPath)!);
            File.WriteAllText(PolicyPath, document.ToJsonString());
        }

        public string ReadPolicyFile() => File.ReadAllText(PolicyPath);

        public JsonObject ReadPolicyNode() => JsonNode.Parse(ReadPolicyFile())!.AsObject();

        public void Dispose() => Directory.Delete(root, true);
    }

    private sealed class Environment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    internal sealed class RecordingLogger : ILogger<JsonGpuPlacementPolicyStore>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
