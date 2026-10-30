using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.GpuPlacement.External;
using ResourceManager.App.Infrastructure.ProcessIdentity;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Settings;
using ResourceManager.App.StateItems;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.ReadWrite.Windows.Services;
using ResourceManager.Shared.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

// 内存与 CPU 取舍（docs/memory_cpu_tiers_2026-10-30.md）：档位表、单独设置、读端点、GC 运行时配置写入，
// 以及各所有者在下一次使用时读生效值。
public sealed class MemoryCpuTierTests
{
    private static readonly string[] OptionNames =
    [
        "gcGen0MaxBudget", "gcConserveMemory", "ledgerMemoryModeFloor", "bufferReturn", "nativeWorkareaGrowth",
        "singleRequestStopBufferMs", "serviceIdentityCacheMs", "browserRuntimeSnapshotSeconds",
        "platformSensorSnapshotSeconds", "storageSnapshotSeconds", "rendererAbsenceEntries",
        "fileQuerySessionsMax", "fileQuerySessionsPrebuilt", "fileQueryIdleSessions",
    ];

    [Fact]
    public void CatalogUsesTheAgreedOptionNamesInOrder()
        => Assert.Equal(OptionNames, MemoryCpuTierResolver.Options.Select(static option => option.Name));

    [Fact]
    public void TierThreeKeepsTodaysBehaviourExceptTheOnDemandFileQueryPool()
    {
        var values = MemoryCpuTierTable.ProductDefault.Values;
        Assert.Equal(3, MemoryCpuTierTable.ProductDefault.Tier);
        Assert.Equal(4L * 1024 * 1024, values.GcGen0MaxBudget);
        Assert.Equal(5, values.GcConserveMemory);
        Assert.Equal(new BufferReturnPolicy(false, 8), values.BufferReturn);
        Assert.Equal(TimeSpan.FromSeconds(10), values.SingleRequestStopBuffer);
        Assert.Equal(TimeSpan.FromSeconds(5), values.ServiceIdentityCacheLifetime);
        Assert.Equal(TimeSpan.FromSeconds(5), values.BrowserRuntimeSnapshot);
        Assert.Equal(TimeSpan.FromSeconds(5), values.PlatformSensorSnapshot);
        Assert.Equal(TimeSpan.FromSeconds(15), values.StorageSnapshot);
        Assert.Equal(4096, values.RendererAbsenceEntries);
        Assert.Equal(8, values.FileQuerySessionsMax);
        Assert.Equal(0, values.FileQuerySessionsPrebuilt);
        Assert.False(values.FileQueryCloseIdleSessions);
        Assert.Empty(MemoryCpuTierTable.ProductDefault.Ignored);
        Assert.All(MemoryCpuTierTable.ProductDefault.Options, static option => Assert.Equal(MemoryCpuTierSources.Tier, option.Source));
    }

    [Theory]
    [InlineData(1, 0, "releaseAfterUse", 1048576L)]
    [InlineData(2, 5, "shrinkAfter2LowReads", 1048576L * 2)]
    [InlineData(3, 10, "shrinkAfter8LowReads", 1048576L * 4)]
    [InlineData(4, 30, "shrinkAfter32LowReads", 1048576L * 8)]
    [InlineData(5, 60, "keepPeak", null)]
    public void EachTierResolvesItsColumnAndCategory(int tier, int retentionSeconds, string bufferReturn, long? gen0)
    {
        var resolution = MemoryCpuTierResolver.Resolve(MemoryCpuTierTable.Product, new MemoryCpuTierSettings(tier));
        Assert.Equal(tier, resolution.Tier);
        Assert.Equal(TimeSpan.FromSeconds(retentionSeconds), resolution.Values.SingleRequestStopBuffer);
        Assert.Equal(bufferReturn, Option(resolution, "bufferReturn").Value.Choice);
        Assert.Equal(gen0, resolution.Values.GcGen0MaxBudget);
        foreach (var option in resolution.Options)
        {
            Assert.Equal(5, option.Tiers.Length);
            Assert.Equal(option.Tiers[tier - 1].Value, option.Value);
            Assert.Equal(option.Tiers[tier - 1].Category, option.Category);
            Assert.True(option.Range.Accepts(option.Value));
        }
        var expectedCategory = tier is 1 or 5 ? MemoryCpuTierCategories.Aggressive : MemoryCpuTierCategories.Proportional;
        Assert.Equal(expectedCategory, Option(resolution, "singleRequestStopBufferMs").Category);
    }

    [Fact]
    public void BufferReturnChoicesMapToTheInventoryPolicy()
    {
        BufferReturnPolicy Policy(int tier)
            => MemoryCpuTierResolver.Resolve(MemoryCpuTierTable.Product, new MemoryCpuTierSettings(tier)).Values.BufferReturn;
        Assert.Equal(new BufferReturnPolicy(true, 0), Policy(1));
        Assert.Equal(new BufferReturnPolicy(false, 2), Policy(2));
        Assert.Equal(new BufferReturnPolicy(false, 8), Policy(3));
        Assert.Equal(new BufferReturnPolicy(false, 32), Policy(4));
        Assert.True(Policy(5).KeepPeak);
    }

    [Fact]
    public void OverrideBeatsTheTierAndSurvivesATierChange()
    {
        var overrides = Overrides("""{"singleRequestStopBufferMs":7000,"gcGen0MaxBudget":null,"bufferReturn":"keepPeak"}""");
        foreach (var tier in new[] { 1, 3, 5 })
        {
            var resolution = MemoryCpuTierResolver.Resolve(MemoryCpuTierTable.Product, new MemoryCpuTierSettings(tier, overrides));
            Assert.Equal(TimeSpan.FromSeconds(7), resolution.Values.SingleRequestStopBuffer);
            Assert.Null(resolution.Values.GcGen0MaxBudget);
            Assert.True(resolution.Values.BufferReturn.KeepPeak);
            Assert.Equal(MemoryCpuTierSources.User, Option(resolution, "singleRequestStopBufferMs").Source);
            Assert.Equal(MemoryCpuTierSources.User, Option(resolution, "gcGen0MaxBudget").Source);
            Assert.Equal(MemoryCpuTierSources.Tier, Option(resolution, "serviceIdentityCacheMs").Source);
            // The category stays the tier cell's category; source says the value is the user's.
            Assert.Equal(Option(resolution, "singleRequestStopBufferMs").Tiers[tier - 1].Category,
                Option(resolution, "singleRequestStopBufferMs").Category);
        }
    }

    [Theory]
    [InlineData(0, "{}", MemoryCpuTierResolver.TierOutOfRange, "$.memoryCpuTier.tier")]
    [InlineData(6, "{}", MemoryCpuTierResolver.TierOutOfRange, "$.memoryCpuTier.tier")]
    [InlineData(3, """{"noSuchOption":1}""", MemoryCpuTierResolver.UnknownOption, "$.memoryCpuTier.overrides.noSuchOption")]
    [InlineData(3, """{"singleRequestStopBufferMs":null}""", MemoryCpuTierResolver.ValueOutOfRange, "$.memoryCpuTier.overrides.singleRequestStopBufferMs")]
    [InlineData(3, """{"singleRequestStopBufferMs":3600001}""", MemoryCpuTierResolver.ValueOutOfRange, "$.memoryCpuTier.overrides.singleRequestStopBufferMs")]
    [InlineData(3, """{"singleRequestStopBufferMs":"10"}""", MemoryCpuTierResolver.ValueOutOfRange, "$.memoryCpuTier.overrides.singleRequestStopBufferMs")]
    [InlineData(3, """{"singleRequestStopBufferMs":1.5}""", MemoryCpuTierResolver.ValueOutOfRange, "$.memoryCpuTier.overrides.singleRequestStopBufferMs")]
    [InlineData(3, """{"bufferReturn":"sometimes"}""", MemoryCpuTierResolver.ValueOutOfRange, "$.memoryCpuTier.overrides.bufferReturn")]
    [InlineData(3, """{"bufferReturn":null}""", MemoryCpuTierResolver.ValueOutOfRange, "$.memoryCpuTier.overrides.bufferReturn")]
    [InlineData(3, """{"gcConserveMemory":null}""", MemoryCpuTierResolver.ValueOutOfRange, "$.memoryCpuTier.overrides.gcConserveMemory")]
    [InlineData(3, """{"gcConserveMemory":10}""", MemoryCpuTierResolver.ValueOutOfRange, "$.memoryCpuTier.overrides.gcConserveMemory")]
    [InlineData(3, """{"fileQuerySessionsMax":9}""", MemoryCpuTierResolver.ValueOutOfRange, "$.memoryCpuTier.overrides.fileQuerySessionsMax")]
    [InlineData(3, """{"rendererAbsenceEntries":0}""", MemoryCpuTierResolver.ValueOutOfRange, "$.memoryCpuTier.overrides.rendererAbsenceEntries")]
    public void ValidateRejectsTierOptionAndValueOutsideTheCatalog(int tier, string overrides, string code, string path)
    {
        var rejection = MemoryCpuTierResolver.Validate(new MemoryCpuTierSettings(tier, Overrides(overrides)));
        Assert.NotNull(rejection);
        Assert.Equal(code, rejection.Code);
        Assert.Equal(path, rejection.Path);
    }

    [Fact]
    public void NullIsAcceptedOnlyWhereTheRangeAllowsUnset()
    {
        foreach (var option in MemoryCpuTierResolver.Options)
        {
            var rejection = MemoryCpuTierResolver.Validate(new MemoryCpuTierSettings(3, Overrides($$"""{"{{option.Name}}":null}""")));
            if (option.Range.Unset) Assert.Null(rejection);
            else Assert.Equal(MemoryCpuTierResolver.ValueOutOfRange, rejection?.Code);
        }
        Assert.True(MemoryCpuTierResolver.Find("gcGen0MaxBudget")!.Range.Unset);
        Assert.Null(MemoryCpuTierResolver.Validate(null));
        Assert.Null(MemoryCpuTierResolver.Validate(new MemoryCpuTierSettings()));
    }

    [Fact]
    public void InvalidStoredEntriesAreIgnoredAndListedNotApplied()
    {
        var resolution = MemoryCpuTierResolver.Resolve(MemoryCpuTierTable.Product, new MemoryCpuTierSettings(9,
            Overrides("""{"noSuchOption":1,"singleRequestStopBufferMs":-1,"serviceIdentityCacheMs":60000}""")));
        Assert.Equal(3, resolution.Tier);
        Assert.Equal(TimeSpan.FromSeconds(10), resolution.Values.SingleRequestStopBuffer);
        Assert.Equal(TimeSpan.FromSeconds(60), resolution.Values.ServiceIdentityCacheLifetime);
        Assert.Equal(
            new[]
            {
                new MemoryCpuTierRejection(MemoryCpuTierResolver.TierOutOfRange, "$.memoryCpuTier.tier"),
                new MemoryCpuTierRejection(MemoryCpuTierResolver.UnknownOption, "$.memoryCpuTier.overrides.noSuchOption"),
                new MemoryCpuTierRejection(MemoryCpuTierResolver.ValueOutOfRange, "$.memoryCpuTier.overrides.singleRequestStopBufferMs"),
            },
            resolution.Ignored);
    }

    [Fact]
    public void PatchReplacesTheWholeSectionAndKeepsNullOverrides()
    {
        var current = AppSettingsDefaults.Create() with
        {
            MemoryCpuTier = new MemoryCpuTierSettings(4, Overrides("""{"singleRequestStopBufferMs":7000,"serviceIdentityCacheMs":2000}""")),
        };
        using var changes = JsonDocument.Parse("""{"memoryCpuTier":{"tier":2,"overrides":{"gcGen0MaxBudget":null}}}""");
        var patched = AppSettingsPatchApplier.Apply(current, changes.RootElement).MemoryCpuTier!;
        Assert.Equal(2, patched.Tier);
        var entry = Assert.Single(patched.Overrides!);
        Assert.Equal("gcGen0MaxBudget", entry.Key);
        Assert.Equal(JsonValueKind.Null, entry.Value.ValueKind);
        Assert.Null(MemoryCpuTierResolver.Validate(patched));

        using var cleared = JsonDocument.Parse("""{"memoryCpuTier":{"tier":2,"overrides":{}}}""");
        Assert.Empty(AppSettingsPatchApplier.Apply(current, cleared.RootElement).MemoryCpuTier!.Overrides ?? new Dictionary<string, JsonElement>());
    }

    [Theory]
    [InlineData("""{"memoryCpuTier":null}""", "$.memoryCpuTier")]
    [InlineData("""{"memoryCpuTier":3}""", "$.memoryCpuTier")]
    [InlineData("""{"memoryCpuTier":{"overrides":{}}}""", "$.memoryCpuTier.tier")]
    [InlineData("""{"memoryCpuTier":{"tier":3}}""", "$.memoryCpuTier.overrides")]
    [InlineData("""{"memoryCpuTier":{"tier":3,"overrides":[]}}""", "$.memoryCpuTier.overrides")]
    [InlineData("""{"memoryCpuTier":{"tier":3,"overrides":{},"extra":1}}""", "$.memoryCpuTier.extra")]
    public void PatchRejectsASectionThatIsNotTierAndOverrides(string changes, string path)
    {
        using var document = JsonDocument.Parse(changes);
        var exception = Assert.Throws<AppSettingsPatchException>(
            () => AppSettingsPatchApplier.Apply(AppSettingsDefaults.Create(), document.RootElement));
        Assert.Equal("invalid-settings-value-type", exception.Message);
        Assert.Equal(path, exception.FieldPath);
    }

    [Fact]
    public void RewriteChangesOnlyTheTwoGcKeys()
    {
        const string original = """
            {
              // written by the SDK
              "runtimeOptions": {
                "tfm": "net10.0",
                "configProperties": {
                  "System.GC.Concurrent": false,
                  "System.GC.Gen0MaxBudget": 4194304,
                  "System.GC.ConserveMemory": 5,
                  "Other.Switch": "0x10",
                },
              },
            }
            """;
        var rewritten = JsonNode.Parse(RuntimeConfigWriter.Rewrite(original, new RuntimeGcSettings(1048576, 9)))!;
        var properties = rewritten["runtimeOptions"]!["configProperties"]!.AsObject();
        Assert.Equal(1048576, properties[RuntimeConfigWriter.Gen0MaxBudgetKey]!.GetValue<long>());
        Assert.Equal(9, properties[RuntimeConfigWriter.ConserveMemoryKey]!.GetValue<int>());
        Assert.False(properties["System.GC.Concurrent"]!.GetValue<bool>());
        Assert.Equal("0x10", properties["Other.Switch"]!.GetValue<string>());
        Assert.Equal("net10.0", rewritten["runtimeOptions"]!["tfm"]!.GetValue<string>());
        Assert.Equal(4, properties.Count);

        var unset = JsonNode.Parse(RuntimeConfigWriter.Rewrite(original, new RuntimeGcSettings(null, 0)))!;
        var unsetProperties = unset["runtimeOptions"]!["configProperties"]!.AsObject();
        Assert.False(unsetProperties.ContainsKey(RuntimeConfigWriter.Gen0MaxBudgetKey));
        Assert.Equal(0, unsetProperties[RuntimeConfigWriter.ConserveMemoryKey]!.GetValue<int>());
        Assert.Equal(3, unsetProperties.Count);

        var created = JsonNode.Parse(RuntimeConfigWriter.Rewrite("""{"runtimeOptions":{"tfm":"net10.0"}}""", new RuntimeGcSettings(2097152, 7)))!;
        Assert.Equal(2, created["runtimeOptions"]!["configProperties"]!.AsObject().Count);
        Assert.Throws<InvalidDataException>(() => RuntimeConfigWriter.Rewrite("""{"other":{}}""", new RuntimeGcSettings(null, 0)));
        Assert.Throws<InvalidDataException>(() => RuntimeConfigWriter.Rewrite("""{"runtimeOptions":{"configProperties":[]}}""", new RuntimeGcSettings(null, 0)));
    }

    [Fact]
    public void ApplyWritesBothFilesOnlyWhenTheEffectiveValueDiffersAndNeverOtherwise()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rm-runtimeconfig-{Guid.NewGuid():N}");
        try
        {
            var service = Path.Combine(root, "Bin", "ResourceManager");
            var nativeUi = Path.Combine(root, "Bin", "ResourceManagerNativeUi");
            Directory.CreateDirectory(service);
            Directory.CreateDirectory(nativeUi);
            var servicePath = Path.Combine(service, "ResourceManager.runtimeconfig.json");
            var nativeUiPath = Path.Combine(nativeUi, "ResourceManager.NativeUi.runtimeconfig.json");
            File.WriteAllText(servicePath, """{"runtimeOptions":{"configProperties":{"System.GC.Gen0MaxBudget":4194304,"System.GC.ConserveMemory":5,"Keep":1}}}""");
            File.WriteAllText(nativeUiPath, """{"runtimeOptions":{"configProperties":{"System.GC.Gen0MaxBudget":2097152}}}""");

            Assert.Null(RuntimeConfigWriter.InstalledFiles(service)); // No Internal\Management: not an installed layout.
            Directory.CreateDirectory(InstalledManagerDirectory(root));
            var files = RuntimeConfigWriter.InstalledFiles(service + Path.DirectorySeparatorChar);
            Assert.NotNull(files);
            Assert.Equal(new[] { RuntimeConfigWriter.ServiceProcess, RuntimeConfigWriter.NativeUiProcess }, files.Select(static file => file.Process));
            Assert.Null(RuntimeConfigWriter.InstalledFiles(nativeUi));

            var running = new RuntimeGcSettings(4194304, 5);
            var writer = new RuntimeConfigWriter(files, running);
            var serviceBefore = File.ReadAllText(servicePath);
            writer.Apply(running); // Tier 3 on a tier-3 service: nothing to write.
            Assert.Equal(serviceBefore, File.ReadAllText(servicePath));
            Assert.Equal(running, writer.Read().NextStart);

            writer.Apply(new RuntimeGcSettings(null, 0)); // Tier 5.
            foreach (var path in new[] { servicePath, nativeUiPath })
            {
                var properties = JsonNode.Parse(File.ReadAllText(path))!["runtimeOptions"]!["configProperties"]!.AsObject();
                Assert.False(properties.ContainsKey(RuntimeConfigWriter.Gen0MaxBudgetKey));
                Assert.Equal(0, properties[RuntimeConfigWriter.ConserveMemoryKey]!.GetValue<int>());
            }
            Assert.Equal(1, JsonNode.Parse(File.ReadAllText(servicePath))!["runtimeOptions"]!["configProperties"]!["Keep"]!.GetValue<int>());
            var state = writer.Read();
            Assert.True(state.Writable);
            Assert.Null(state.Reason);
            Assert.Equal(running, state.Running);
            Assert.Equal(new RuntimeGcSettings(null, 0), state.NextStart);
            Assert.Null(state.LastFailure);
            Assert.Empty(Directory.GetFiles(service, "*.tmp"));

            var stamp = File.GetLastWriteTimeUtc(servicePath);
            File.SetLastWriteTimeUtc(servicePath, stamp.AddMinutes(-5));
            writer.Apply(new RuntimeGcSettings(null, 0)); // Same effective value again: untouched.
            Assert.Equal(stamp.AddMinutes(-5), File.GetLastWriteTimeUtc(servicePath));

            File.WriteAllText(nativeUiPath, "[]");
            writer.Apply(new RuntimeGcSettings(1048576, 9));
            Assert.NotNull(writer.Read().LastFailure);
            Assert.Equal("[]", File.ReadAllText(nativeUiPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DevRunWritesNothingAndSaysWhy()
    {
        var writer = new RuntimeConfigWriter(null, new RuntimeGcSettings(null, 0));
        writer.Apply(new RuntimeGcSettings(1048576, 9));
        var state = writer.Read();
        Assert.False(state.Writable);
        Assert.Equal(RuntimeConfigWriter.NotInstalledReason, state.Reason);
        Assert.Empty(state.Files);
        Assert.Equal(state.Running, state.NextStart);
        Assert.Null(RuntimeConfigWriter.InstalledFiles(AppContext.BaseDirectory));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(4194304L, 4194304L)]
    [InlineData(5, 5L)]
    [InlineData("4194304", 4194304L)]
    [InlineData("0x400000", 4194304L)]
    [InlineData("0X10", 16L)]
    [InlineData("big", null)]
    [InlineData(true, null)]
    public void RuntimeValuesParseFromAppContextText(object? data, long? expected)
        => Assert.Equal(expected, RuntimeConfigWriter.ParseInteger(data));

    [Fact]
    public async Task OwnersReadTheNewlyPublishedValueOnTheirNextUse()
    {
        var authority = new BackendStateItems().Authority;
        var cell = authority.ClaimCell<MemoryCpuTierResolution>(StateItemUids.MemoryCpuTierEffective);
        Assert.Equal(MemoryCpuTierTable.ProductDefault.Values, MemoryCpuTierState.Read(authority));
        Assert.Equal(new WindowsProcessInventoryBufferReturn(false, 8), HostedResourcePublicationState.InventoryBufferReturn(authority));

        var reads = 0;
        var services = new WindowsServiceIdentityResolver(
            () => { reads++; return [new WindowsServiceRecord("SvcA", "A", 0x20, 4, 57001, null)]; },
            _ => 100, TimeProvider.System, authority);
        services.CaptureInputVersion();
        services.CaptureInputVersion();
        Assert.Equal(1, reads); // Tier 3: five-second snapshot.

        await Publish(cell, 1, "{}");
        Assert.Equal(TimeSpan.Zero, MemoryCpuTierState.Read(authority).SingleRequestStopBuffer);
        Assert.Equal(new WindowsProcessInventoryBufferReturn(true, 0), HostedResourcePublicationState.InventoryBufferReturn(authority));
        services.CaptureInputVersion();
        services.CaptureInputVersion();
        Assert.Equal(3, reads); // Tier 1: every resolution re-reads.

        await Publish(cell, 1, """{"serviceIdentityCacheMs":60000,"bufferReturn":"keepPeak"}""");
        services.CaptureInputVersion();
        services.CaptureInputVersion();
        Assert.Equal(3, reads); // 60 s now: the snapshot read at tier 1 is still fresh.
        Assert.True(HostedResourcePublicationState.InventoryBufferReturn(authority).KeepPeak);

        await Publish(cell, 5, "{}");
        Assert.Equal(new WindowsProcessInventoryBufferReturn(false, 0), HostedResourcePublicationState.InventoryBufferReturn(authority));
        Assert.Null(MemoryCpuTierState.Read(authority).GcGen0MaxBudget);
    }

    [Fact]
    public async Task RendererAbsenceCacheOverflowsAtThePublishedEntryCount()
    {
        var authority = new BackendStateItems().Authority;
        var cell = authority.ClaimCell<MemoryCpuTierResolution>(StateItemUids.MemoryCpuTierEffective);
        await Publish(cell, 3, """{"rendererAbsenceEntries":4}""");
        using var broker = new NativeRendererTestBroker();
        using var cache = new RendererAbsenceCache(broker, authority);
        using var observationRun = cache.Acquire();
        var first = RendererAbsenceCacheTests.Process(20_000, 20_000, @"C:\Apps\renderer.exe");
        for (var index = 0; index < 4; index++)
        {
            var identity = first with { ProcessId = first.ProcessId + index, ProcessStartKey = (ulong)(first.ProcessStartKey + (uint)index) };
            cache.PublishAbsent(identity, cache.Read(identity));
        }
        Assert.True(cache.Read(first).IsAbsent);
        var overflow = first with { ProcessId = first.ProcessId + 4, ProcessStartKey = (ulong)(first.ProcessStartKey + 4) };
        cache.PublishAbsent(overflow, cache.Read(overflow));
        Assert.False(cache.Read(first).IsAbsent);

        await Publish(cell, 3, """{"rendererAbsenceEntries":8}""");
        for (var index = 0; index < 5; index++)
        {
            var identity = first with { ProcessId = first.ProcessId + index, ProcessStartKey = (ulong)(first.ProcessStartKey + (uint)index) };
            cache.PublishAbsent(identity, cache.Read(identity));
        }
        Assert.True(cache.Read(first).IsAbsent); // Five entries now fit.
    }

    [Fact]
    public async Task EndpointListsEveryOptionWithItsTierCellsAndRestartFieldsForEveryTier()
    {
        await using var fixture = await TierFixture.StartAsync();
        var initial = (await fixture.Client.GetFromJsonAsync<JsonObject>("/api/settings/memory-cpu-tier"))!;
        AssertDecoderInvariants(initial, 3);
        var runtime = initial["runtimeConfig"]!.AsObject();
        Assert.False(runtime["writable"]!.GetValue<bool>());
        Assert.Equal(RuntimeConfigWriter.NotInstalledReason, runtime["reason"]!.GetValue<string>());
        Assert.Empty(runtime["files"]!.AsArray());
        Assert.Equal(RuntimeConfigWriter.NativeUiRunningUnavailable, runtime["nativeUiRunningValue"]!.GetValue<string>());
        Assert.Empty(initial["ignored"]!.AsArray());

        int[] retention = [0, 5000, 10000, 30000, 60000];
        for (var tier = 1; tier <= 5; tier++)
        {
            using var patched = await fixture.PatchAsync($$$"""{"memoryCpuTier":{"tier":{{{tier}}},"overrides":{}}}""");
            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
            var listed = (await fixture.Client.GetFromJsonAsync<JsonObject>("/api/settings/memory-cpu-tier"))!;
            AssertDecoderInvariants(listed, tier);
            Assert.Equal(retention[tier - 1], OptionOf(listed, "singleRequestStopBufferMs")["value"]!.GetValue<int>());
            // A dev run writes nothing, so nothing waits for a restart.
            Assert.False(OptionOf(listed, "gcConserveMemory")["pendingRestart"]!.GetValue<bool>());
        }
    }

    [Fact]
    public async Task HttpPatchReplacesTheSectionAcceptsUnsetWhereAllowedAndRejectsOtherNulls()
    {
        await using var fixture = await TierFixture.StartAsync();
        using (var accepted = await fixture.PatchAsync(
            """{"memoryCpuTier":{"tier":2,"overrides":{"gcGen0MaxBudget":null,"singleRequestStopBufferMs":7000}}}"""))
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var listed = (await fixture.Client.GetFromJsonAsync<JsonObject>("/api/settings/memory-cpu-tier"))!;
        AssertDecoderInvariants(listed, 2);
        var gen0 = OptionOf(listed, "gcGen0MaxBudget");
        Assert.Equal("user", gen0["source"]!.GetValue<string>());
        Assert.True(gen0.ContainsKey("value"));
        Assert.Null(gen0["value"]);
        Assert.Equal(7000, OptionOf(listed, "singleRequestStopBufferMs")["value"]!.GetValue<int>());

        // The section is replaced, not merged: leaving gcGen0MaxBudget out returns it to the tier.
        using (var replaced = await fixture.PatchAsync("""{"memoryCpuTier":{"tier":2,"overrides":{"singleRequestStopBufferMs":7000}}}"""))
            Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        listed = (await fixture.Client.GetFromJsonAsync<JsonObject>("/api/settings/memory-cpu-tier"))!;
        AssertDecoderInvariants(listed, 2);
        Assert.Equal("tier", OptionOf(listed, "gcGen0MaxBudget")["source"]!.GetValue<string>());
        Assert.Equal(2097152, OptionOf(listed, "gcGen0MaxBudget")["value"]!.GetValue<long>());
        var saved = (await fixture.Client.GetFromJsonAsync<JsonObject>("/api/settings/app"))!;
        var savedOverrides = saved["settings"]!["memoryCpuTier"]!["overrides"]!.AsObject();
        Assert.Equal(new[] { "singleRequestStopBufferMs" }, savedOverrides.Select(static entry => entry.Key));

        foreach (var (changes, code, path) in new[]
        {
            ("""{"memoryCpuTier":{"tier":2,"overrides":{"singleRequestStopBufferMs":null}}}""", MemoryCpuTierResolver.ValueOutOfRange, "$.memoryCpuTier.overrides.singleRequestStopBufferMs"),
            ("""{"memoryCpuTier":{"tier":2,"overrides":{"noSuchOption":1}}}""", MemoryCpuTierResolver.UnknownOption, "$.memoryCpuTier.overrides.noSuchOption"),
            ("""{"memoryCpuTier":{"tier":6,"overrides":{}}}""", MemoryCpuTierResolver.TierOutOfRange, "$.memoryCpuTier.tier"),
        })
        {
            using var rejected = await fixture.PatchAsync(changes);
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            var result = (await rejected.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Equal("rejectedBeforeCommit", result["runtimeApplicationDisposition"]!.GetValue<string>());
            Assert.Equal(code, result["runtimeFailureCode"]!.GetValue<string>());
            Assert.Equal(path, result["runtimeFailurePath"]!.GetValue<string>());
        }
        foreach (var malformed in new[]
        {
            """{"memoryCpuTier":{"tier":2}}""",
            """{"memoryCpuTier":{"tier":2,"overrides":{},"extra":true}}""",
            """{"memoryCpuTier":null}""",
        })
        {
            using var rejected = await fixture.PatchAsync(malformed);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        listed = (await fixture.Client.GetFromJsonAsync<JsonObject>("/api/settings/memory-cpu-tier"))!;
        Assert.Equal(2, listed["tier"]!.GetValue<int>());
        Assert.Equal(7000, OptionOf(listed, "singleRequestStopBufferMs")["value"]!.GetValue<int>());
    }

    // The frontend decoder's invariants (design §5): five cells, the current tier's cell decides the value
    // when source is tier and always decides the category, restart options carry runningValue and
    // pendingRestart and immediate options carry neither, and the range has exactly one of its two shapes.
    private static void AssertDecoderInvariants(JsonObject response, int tier)
    {
        Assert.Equal(new[] { "tier", "options", "runtimeConfig", "ignored" }, response.Select(static entry => entry.Key));
        Assert.Equal(tier, response["tier"]!.GetValue<int>());
        var options = response["options"]!.AsArray().Select(static option => option!.AsObject()).ToArray();
        Assert.Equal(OptionNames, options.Select(static option => option["name"]!.GetValue<string>()));
        foreach (var option in options)
        {
            var name = option["name"]!.GetValue<string>();
            var tiers = option["tiers"]!.AsArray();
            Assert.Equal(5, tiers.Count);
            var cell = tiers[tier - 1]!.AsObject();
            var source = option["source"]!.GetValue<string>();
            Assert.Contains(source, new[] { "tier", "user" });
            if (source == "tier") Assert.Equal(Text(cell["value"]), Text(option["value"]));
            Assert.Equal(cell["category"]!.GetValue<string>(), option["category"]!.GetValue<string>());
            Assert.All(tiers, static entry => Assert.Contains(entry!["category"]!.GetValue<string>(), new[] { "proportional", "aggressive" }));

            var range = option["range"]!.AsObject();
            var unit = option["unit"]?.GetValue<string>();
            if (range["kind"]!.GetValue<string>() == "integer")
            {
                Assert.Equal(new[] { "kind", "min", "max", "unset" }, range.Select(static entry => entry.Key));
                Assert.Contains(unit, new[] { "bytes", "milliseconds", "seconds", "count", "level" });
                Assert.True(InRange(range, option["value"]), name);
                Assert.All(tiers, entry => Assert.True(InRange(range, entry!["value"]), name));
            }
            else
            {
                Assert.Equal("choice", range["kind"]!.GetValue<string>());
                Assert.Equal(new[] { "kind", "choices" }, range.Select(static entry => entry.Key));
                Assert.Null(unit);
                var choices = range["choices"]!.AsArray().Select(static choice => choice!.GetValue<string>()).ToArray();
                Assert.Contains(option["value"]!.GetValue<string>(), choices);
                Assert.All(tiers, entry => Assert.Contains(entry!["value"]!.GetValue<string>(), choices));
            }

            var apply = option["apply"]!.GetValue<string>();
            if (apply == "restart")
            {
                Assert.True(option.ContainsKey("runningValue"), name);
                Assert.True(InRange(range, option["runningValue"]), name);
                _ = option["pendingRestart"]!.GetValue<bool>();
            }
            else
            {
                Assert.Equal("immediate", apply);
                Assert.False(option.ContainsKey("runningValue"), name);
                Assert.False(option.ContainsKey("pendingRestart"), name);
            }
            Assert.Equal(name is "gcGen0MaxBudget" or "gcConserveMemory" ? "restart" : "immediate", apply);
        }
    }

    private static bool InRange(JsonObject range, JsonNode? value)
    {
        if (value is null) return range["unset"]!.GetValue<bool>();
        var integer = value.GetValue<long>();
        return integer >= range["min"]!.GetValue<long>() && integer <= range["max"]!.GetValue<long>();
    }

    private static string Text(JsonNode? node) => node?.ToJsonString() ?? "null";

    private static JsonObject OptionOf(JsonObject response, string name)
        => response["options"]!.AsArray().Single(option => option!["name"]!.GetValue<string>() == name)!.AsObject();

    private static MemoryCpuTierOptionResolution Option(MemoryCpuTierResolution resolution, string name)
        => resolution.Options.Single(option => option.Name == name);

    private static IReadOnlyDictionary<string, JsonElement> Overrides(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(static property => property.Name, static property => property.Value.Clone());
    }

    private static Task Publish(StateCell<MemoryCpuTierResolution> cell, int tier, string overrides)
        => cell.PublishAsync(MemoryCpuTierResolver.Resolve(MemoryCpuTierTable.Product, new MemoryCpuTierSettings(tier, Overrides(overrides))),
            new BusinessBlockReason(BusinessBlockRequestKind.Configuration, nameof(MemoryCpuTierTests), "memory-cpu-tier-test", 0));

    private static string InstalledManagerDirectory(string root) => ResourceManager.Shared.Packages.UpdateManagerPaths.InstalledDirectory(root);

    private sealed class TierFixture(WebApplication app, string root) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses)),
            Timeout = TimeSpan.FromSeconds(20)
        };

        public async Task<HttpResponseMessage> PatchAsync(string changes)
        {
            var current = (await Client.GetFromJsonAsync<JsonObject>("/api/settings/app"))!;
            var body = new JsonObject
            {
                ["expectedRevision"] = current["revision"]!.GetValue<string>(),
                ["changes"] = JsonNode.Parse(changes)
            };
            return await Client.PatchAsync("/api/settings/app", JsonContent.Create(body));
        }

        public static async Task<TierFixture> StartAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"rm-memory-cpu-tier-api-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var capabilities = new StartupCapabilitySet("memory-cpu-tier-test",
                StartupCapability.RuntimeEffectOwners | StartupCapability.MutablePersistence);
            ResourceManager.App.Infrastructure.Security.LoopbackApiPipelineConfigurator
                .Compile(false).ConfigureServices(builder.Services);
            builder.Services.AddResourceManagerApp(["--no-native-ui"], capabilities);
            NativeBackendBlockTestSupport.AddFullTreeHostFixture(builder.Services);
            builder.Services.RemoveAll<IHostedService>();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            try
            {
                typeof(ResourceManagerEndpointRouteBuilderExtensions)
                    .GetMethod("MapSettingsEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [app, capabilities]);
                await app.StartAsync();
                await app.Services.GetRequiredService<IRuntimeSpecializationCoordinator>()
                    .RebuildAsync("test-startup", CancellationToken.None);
                return new TierFixture(app, root);
            }
            catch
            {
                await app.DisposeAsync();
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await app.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }
}
