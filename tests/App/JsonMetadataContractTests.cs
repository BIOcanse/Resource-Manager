using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.KernelLeak;
using ResourceManager.App.Domain.Settings;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed partial class JsonMetadataContractTests
{
    [Fact]
    public void SchedulingDebugPayloadSerializesPopulatedProcessAndDatasetFacts()
    {
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var ticks = DateTimeOffset.UtcNow.UtcTicks;
        var mask = SchedulingProcessMetricMask.CpuUsage;
        var facts = new SchedulingProcessFactSnapshot(SamplingObservationStatus.Current, 1, ticks, 1, 1, 0, 0, mask, mask,
            [new(42, 10, "cs2", @"D:\Games\Steam\cs2.exe", "catalog:game-counter-strike-2", "CS2", "game", "game", 95, mask, 17, 0, 1, [])])
        {
            DatasetObservations = new Dictionary<SchedulingProcessMetricMask, SchedulingProcessDatasetObservation>
            { [mask] = SchedulingProcessDatasetObservation.CreateCurrent(mask, 1, ticks, 1, ticks) }
        };
        var type = typeof(ResourceManagerEndpointRouteBuilderExtensions).GetNestedType("DebugSchedulingPayload", BindingFlags.NonPublic)!;
        var payload = Activator.CreateInstance(type, [null, facts, null])!;
        var wire = JsonSerializer.SerializeToElement(payload, type, options);
        Assert.Equal(42, wire.GetProperty("processFacts").GetProperty("processes")[0].GetProperty("processId").GetInt32());
        Assert.Equal(17, wire.GetProperty("processFacts").GetProperty("processes")[0].GetProperty("cpuUsagePercent").GetDouble());
    }

    [Fact]
    public void KernelLeakRunFailureFixturePinsRequiredStartOnHttpAndSubscriptionWire()
    {
        const string fixture = """
            {"status":"Observing","capturedAt":"1970-01-01T00:00:02+00:00","items":[],"failure":"None",
            "observation":{"startedAt":"1970-01-01T00:00:02+00:00","elapsedSeconds":0,"sampleCount":1,
            "requiredSeconds":600,"requiredSampleCount":8,"previousRunFailure":{"startedAt":"1970-01-01T00:00:00+00:00",
            "failedAt":"1970-01-01T00:00:01+00:00","failure":"SourceUnavailable"},"restartPolicy":"NextInterval"},"unavailableHolderCount":0}
            """;
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var start = DateTimeOffset.UnixEpoch;
        var previous = new KernelLeakRunFailure(start, start.AddSeconds(1), KernelLeakFailure.SourceUnavailable);
        var report = new KernelLeakReport(KernelLeakReportStatus.Observing, start.AddSeconds(2), [], KernelLeakFailure.None,
            new(start.AddSeconds(2), 0, 1, 600, 8, previous));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(fixture), JsonSerializer.SerializeToNode(report, options)));
        var frame = new FrontendSubscriptionChannelFrame<KernelLeakReport>("leaks", report);
        var expectedFrame = new JsonObject { ["subscriptionId"] = "leaks", ["value"] = JsonNode.Parse(fixture) };
        Assert.True(JsonNode.DeepEquals(expectedFrame, JsonSerializer.SerializeToNode(frame, options)));
        Assert.Equal(previous, JsonSerializer.Deserialize<KernelLeakRunFailure>(JsonSerializer.Serialize(previous, options), options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<KernelLeakRunFailure>(
            """{"startedAt":null,"failedAt":"1970-01-01T00:00:01+00:00","failure":"SourceUnavailable"}""", options));
    }

    [Fact]
    public void KernelLeakUnitWireContainsOnlyCount()
    {
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        Assert.Collection(Enum.GetValues<KernelLeakUnit>(),
            unit => Assert.Equal("\"count\"", JsonSerializer.Serialize(unit, options)));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<KernelLeakUnit>("\"bytes\"", options));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("cycle-failed")]
    [InlineData("native-plan-failed")]
    public void SmartStatusFixturesPinHttpAndSubscriptionWire(string name)
    {
        var expected = SmartStatusFixture(name);
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var status = JsonSerializer.Deserialize<HostManagerSmartCoordinatorStatus>(expected, options)!;
        Assert.True(AppOptimizationModes.IsSupported(status.Mode), $"Unsupported fixture mode: {status.Mode}");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonSerializer.SerializeToNode(status, options)));
        var frame = new FrontendSubscriptionChannelFrame<HostManagerSmartCoordinatorStatus>("status", status);
        var expectedFrame = new JsonObject { ["subscriptionId"] = "status", ["value"] = JsonNode.Parse(expected) };
        var wire = JsonSerializer.SerializeToNode(frame, options);
        Assert.True(JsonNode.DeepEquals(expectedFrame, wire), wire!.ToJsonString());
        var roundTrip = JsonSerializer.Deserialize<FrontendSubscriptionChannelFrame<HostManagerSmartCoordinatorStatus>>(wire, options);
        Assert.True(JsonNode.DeepEquals(wire, JsonSerializer.SerializeToNode(roundTrip, options)));
    }

    private static string SmartStatusFixture(string name)
    {
        using var stream = typeof(JsonMetadataContractTests).Assembly.GetManifestResourceStream("smart-status")!;
        using var fixtures = JsonDocument.Parse(stream);
        return fixtures.RootElement.GetProperty(name).GetRawText();
    }

    [Fact]
    public void EverySmartStatusFixtureUsesASupportedProductMode()
    {
        using var stream = typeof(JsonMetadataContractTests).Assembly.GetManifestResourceStream("smart-status")!;
        using var fixtures = JsonDocument.Parse(stream);
        Assert.All(fixtures.RootElement.EnumerateObject(), fixture => Assert.True(
            AppOptimizationModes.IsSupported(fixture.Value.GetProperty("mode").GetString()), fixture.Name));
    }

    [Fact]
    public void EverySmartExclusionCodeHasAnExactWireFixture()
    {
        using var stream = typeof(JsonMetadataContractTests).Assembly.GetManifestResourceStream("smart-exclusions")!;
        using var fixture = JsonDocument.Parse(stream);
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var details = JsonSerializer.Deserialize<IReadOnlyList<HostManagerFactExclusionDiagnostics>>(fixture.RootElement, options)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(fixture.RootElement.GetRawText()), JsonSerializer.SerializeToNode(details, options)));
        Assert.Equal(Enum.GetValues<SchedulingFactCode>().Order(), details.Select(e => e.Fact).Distinct().Order());
        Assert.Equal(Enum.GetValues<SchedulingFactExclusionReason>().Order(), details.Select(e => e.Reason).Distinct().Order());
        foreach (var row in details)
        {
            Assert.True(Enum.IsDefined(row.Fact));
            Assert.True(Enum.IsDefined(row.Reason));
            var count = new HostManagerFactExclusionCount(row.Fact, row.Reason, 1);
            var countWire = JsonSerializer.SerializeToElement(count, options);
            var detailWire = JsonSerializer.SerializeToElement(row, options);
            Assert.Equal(detailWire.GetProperty("fact").GetString(), countWire.GetProperty("fact").GetString());
            Assert.Equal(detailWire.GetProperty("reason").GetString(), countWire.GetProperty("reason").GetString());
            Assert.Matches("^[a-z]+(-[a-z]+)*$", detailWire.GetProperty("fact").GetString()!);
            Assert.Matches("^[a-z]+(-[a-z]+)*$", detailWire.GetProperty("reason").GetString()!);
        }
    }

    [Theory]
    [InlineData("fact", "\"unknown-fact\"")]
    [InlineData("reason", "\"unknown-reason\"")]
    [InlineData("fact", "\"gpu-usage:11\"")]
    [InlineData("fact", "999")]
    [InlineData("reason", "999")]
    [InlineData("fact", "\"999\"")]
    [InlineData("reason", "\"999\"")]
    public void SmartExclusionWireRejectsUnknownAndNumericCodes(string field, string value)
    {
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var detail = JsonSerializer.SerializeToNode(new HostManagerFactExclusionDiagnostics(
            null, null, null, SchedulingFactCode.NativePlan, SchedulingFactExclusionReason.CapacityExceeded, null), options)!;
        detail[field] = JsonNode.Parse(value);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<HostManagerFactExclusionDiagnostics>(detail, options));
        var count = new JsonObject { ["fact"] = detail["fact"]!.DeepClone(), ["reason"] = detail["reason"]!.DeepClone(), ["count"] = 1 };
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<HostManagerFactExclusionCount>(count, options));
    }

    [Fact]
    public void SmartExclusionProductionTypesCannotEmitUndefinedCodes()
    {
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        foreach (var (fact, reason) in new[]
        {
            ((SchedulingFactCode)0, SchedulingFactExclusionReason.DatasetNotCurrent),
            ((SchedulingFactCode)int.MaxValue, SchedulingFactExclusionReason.DatasetNotCurrent),
            (SchedulingFactCode.Inventory, (SchedulingFactExclusionReason)0),
            (SchedulingFactCode.Inventory, (SchedulingFactExclusionReason)int.MaxValue)
        })
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Serialize(new HostManagerFactExclusionDiagnostics(null, null, null, fact, reason, null), options));
            Assert.Throws<JsonException>(() => JsonSerializer.Serialize(new HostManagerFactExclusionCount(fact, reason, 1), options));
        }
    }

    [Theory]
    [InlineData(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    [InlineData(ResourceManager.ReadWrite.ReadVolatilityKind.Sampled)]
    [InlineData(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven)]
    public void MetricCatalogVolatilityHasAValidWireShape(ResourceManager.ReadWrite.ReadVolatilityKind kind)
    {
        var volatility = new ResourceManager.ReadWrite.ReadVolatility(kind,
            kind == ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven ? "producer transition" : null);
        var definition = new MetricDefinition("fixture", "", volatility);
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var wire = JsonSerializer.SerializeToElement(definition, options);
        Assert.Equal(kind == ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven,
            wire.GetProperty("volatility").TryGetProperty("event", out _));
        var roundTrip = JsonSerializer.Deserialize<MetricDefinition>(wire, options)!;
        Assert.Equal(definition.Id, roundTrip.Id);
        Assert.Equal(volatility, roundTrip.Volatility);
    }

    [Theory]
    [InlineData("process", "process-growth-at-least-minimum", "800", "null")]
    [InlineData("processTree", "largest-direct-children-memory-growth", "null", "400")]
    public void MemoryProtectionRecordFixturesRoundTripWithProductionMetadata(string kind, string rule, string growth, string children)
    {
        var treeRoot = kind == "processTree" ? "{\"processId\":20,\"processStartKey\":\"100\",\"name\":\"root\"}" : "null";
        var fixture = $$$$"""
            {"type":"record","record":{"at":"2026-10-27T12:00:00+00:00","processId":20,"processStartKey":"200","name":"example",
            "baselineBytes":0,"currentBytes":800,"growthBytes":800,"dumpPaths":[],"bytesWritten":0,
            "outcome":"terminated-without-dump","reason":"reserve-error","actionId":"test-action","targetKind":"{{{{kind}}}}","treeRoot":{{{{treeRoot}}}},
            "detail":"volume release failed","selectionBasis":{"rule":"{{{{rule}}}}","processGrowthBytes":{{{{growth}}}},
            "directChildrenMemoryGrowthBytes":{{{{children}}}},"minimumProcessGrowthBytes":800}}}
            """;
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var value = JsonSerializer.Deserialize<ResourceManager.App.Application.MemoryProtection.HostMemoryProtectionRecordFrame>(fixture, options)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(fixture), JsonSerializer.SerializeToNode(value, options)));
        var replay = new ResourceManager.App.Application.MemoryProtection.HostMemoryProtectionRecordsFrame([value.Record], 64);
        var replayWire = JsonSerializer.SerializeToElement(replay, options);
        Assert.Equal("records", replayWire.GetProperty("type").GetString());
        Assert.Equal(64, replayWire.GetProperty("limit").GetInt32());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(fixture)!["record"], JsonNode.Parse(replayWire.GetProperty("records")[0].GetRawText())));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"at\":\"2026-10-27T12:00:00+00:00\",\"commit\":{\"currentBytes\":200,\"limitBytes\":1000,\"percent\":20},\"processes\":[],\"processCount\":0}")]
    public void MemoryProtectionPointFixturesRoundTripWithProductionMetadata(string point)
    {
        var fixture = "{\"type\":\"normalRunningPoint\",\"normalRunningPoint\":" + point + "}";
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var value = JsonSerializer.Deserialize<ResourceManager.App.Application.MemoryProtection.HostMemoryProtectionPointFrame>(fixture, options)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(fixture), JsonSerializer.SerializeToNode(value, options)));
    }

    [Fact]
    public void OrdinaryOperationAndProcessWireExcludeInternalDiagnosticText()
    {
        const string diagnostic = "internal-operation-exception-details";
        const string fault = "internal-coordinator-fault-details";
        var time = DateTimeOffset.UnixEpoch;
        var operation = new ResourceManager.App.Domain.Operations.HostManagerOperationSnapshot(
            "00000000000000000000000000000001", "component.install", "component:example",
            new ResourceManager.App.Domain.Operations.HostManagerOperationSubject(ResourceManager.App.Domain.Operations.HostManagerOperationTargetKind.Component, TargetId: "example"),
            "failed", 1, 1, 1, 1, time, time, time, false, null, null,
            ResourceManager.App.Infrastructure.Operations.HostManagerOperationFeedback.Failure(new IOException(diagnostic)));
        var state = new ResourceManager.App.Domain.Operations.HostManagerOperationsPublishedState(time, 1, 1,
            new(false, true, "progress-publication", fault, 1, 1), [operation]);
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var wire = JsonSerializer.SerializeToElement(HostManagerOperationWireProjection.Project(state), options);
        Assert.False(wire.TryGetProperty("faultMessage", out _));
        Assert.True(wire.GetProperty("persistenceFaulted").GetBoolean());
        Assert.Equal("progress-publication", wire.GetProperty("faultStage").GetString());
        var item = Assert.Single(wire.GetProperty("operations").EnumerateArray());
        foreach (var field in new[] { "title", "result", "error", "diagnostic" })
            Assert.False(item.TryGetProperty(field, out _));
        Assert.Equal(5, item.GetProperty("errorMessage").GetProperty("code").GetInt32());
        Assert.DoesNotContain(diagnostic, wire.GetRawText());
        Assert.DoesNotContain(fault, wire.GetRawText());

        var process = new ResourceManager.App.Domain.LocalSystem.SystemProcessOperationResult(
            ResourceManager.App.Domain.LocalSystem.SystemProcessAction.Terminate,
            [new(42, "example", "Failed", ResourceManager.App.Domain.LocalSystem.SystemProcessOutcome.NativeFailure,
                ProcessStartKey: "123", NativeErrorCode: 87)]);
        var processWire = JsonSerializer.SerializeToElement(process, options);
        Assert.False(processWire.TryGetProperty("message", out _));
        var processItem = Assert.Single(processWire.GetProperty("items").EnumerateArray());
        Assert.False(processItem.TryGetProperty("message", out _));
        Assert.False(processItem.TryGetProperty("diagnostic", out _));
        Assert.Equal(87, processItem.GetProperty("nativeErrorCode").GetInt32());
    }

    private static readonly Assembly App = typeof(HostManagerPlanCompiler).Assembly;
    private static readonly Assembly ReadWrite = typeof(WindowsMetricConfiguration).Assembly;

    [Fact]
    public void ProcessFieldCatalogSerializesWithTheProductionResolver()
    {
        // Candidate .22 answered /api/resource-breakdown/fields/catalog with 500: Results.Ok serializes the runtime
        // type, and the registry's collection expression has no generated metadata. Test hosts resolve by reflection.
        var options = new JsonSerializerOptions();
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        Assert.Throws<NotSupportedException>(() =>
            JsonSerializer.Serialize((object)ResourceManager.ReadWrite.ProcessFields.ProcessMetricFieldRegistry.Definitions, options));
        object wire = ResourceManagerEndpointRouteBuilderExtensions.ProcessFieldCatalogWire;
        using var catalog = JsonDocument.Parse(JsonSerializer.Serialize(wire, wire.GetType(), options));
        Assert.Equal(ResourceManager.ReadWrite.ProcessFields.ProcessMetricFieldRegistry.Definitions.Count,
            catalog.RootElement.GetArrayLength());
    }

    [Fact]
    public void GenericSessionConsumersUseTheProductionGeneratedWire()
    {
        var launch = new ResourceManager.Shared.Runtime.TrustedAccess.SessionLaunchRequest(
            Path.Combine(AppContext.BaseDirectory, "consumer.exe"), "--example");
        var wire = ResourceManager.Shared.Runtime.TrustedAccess.SessionLaunchContract.Encode(launch);
        using var json = JsonDocument.Parse(wire);
        Assert.Equal(launch.ExecutablePath, json.RootElement.GetProperty("executablePath").GetString());
        Assert.Equal(launch, ResourceManager.Shared.Runtime.TrustedAccess.SessionLaunchContract.Decode(wire));
        Assert.Equal(123u, ResourceManager.Shared.Runtime.TrustedAccess.SessionLaunchContract.DecodeResult(
            ResourceManager.Shared.Runtime.TrustedAccess.SessionLaunchContract.EncodeResult(123)).ProcessId);

        var shell = new ResourceManager.Shared.Runtime.UserShellExecuteRequest(
            "https://example.invalid/", "", WaitForExit: true);
        Assert.Equal(shell, ResourceManager.Shared.Runtime.UserShellExecuteRequest.Decode(shell.Encode()));
    }

    [Fact]
    public void TrustedAccessDecisionFixturesMatchProductionWireSerialization()
    {
        using var stream = typeof(JsonMetadataContractTests).Assembly.GetManifestResourceStream("trusted-access-decisions")!;
        using var fixtures = JsonDocument.Parse(stream);
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        foreach (var fixture in fixtures.RootElement.EnumerateObject())
        {
            var value = JsonSerializer.Deserialize<TrustedAccessRequestDecisionWireResponse>(fixture.Value, options)!;
            Assert.Equal(fixture.Name, value.Status);
            var serialized = JsonSerializer.SerializeToNode(value, options);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(fixture.Value.GetRawText()), serialized), fixture.Name);
            Assert.True(fixture.Value.TryGetProperty("key", out _));
            Assert.True(fixture.Value.TryGetProperty("denialReason", out _));
        }
    }

    public static IEnumerable<object[]> Owners()
    {
        foreach (var (assembly, name, field) in new[]
        {
            (App, "ResourceManager.App.Infrastructure.Settings.JsonAppSettingsStore", "JsonOptions"),
            (App, "ResourceManager.App.Infrastructure.Software.JsonManualSoftwareRegistry", "JsonOptions"),
            (App, "ResourceManager.App.Infrastructure.Control.JsonControlInstanceRegistry", "JsonOptions"),
            (App, "ResourceManager.App.Infrastructure.Optimization.JsonOptimizationProtectionStore", "JsonOptions"),
            (App, "ResourceManager.App.Infrastructure.GpuPlacement.JsonGpuPlacementProcessHistoryStore", "JsonOptions"),
            (App, "ResourceManager.App.Infrastructure.Optimization.HostManagerMemoryCleanupAttemptJournal", "JsonOptions"),
            (App, "ResourceManager.App.Infrastructure.Optimization.HostManagerProcessEffectValidationScopeAuthority", "JsonOptions"),
            (App, "ResourceManager.App.Infrastructure.Optimization.HostManagerRollbackStateEnvelopeCodec", "JsonOptions"),
            (App, "ResourceManager.App.Infrastructure.Adaptation.ResourceManagerSelfSchedulingControl", "SchedulingPayloadJson"),
            (App, "ResourceManager.App.Infrastructure.Monitoring.GpuTelemetry.GpuTelemetryWorkerProtocol", "SerializerOptions"),
            (App, "ResourceManager.App.Infrastructure.DeviceTopology.Snapshots.JsonDeviceTopologySnapshotStore", "JsonOptions"),
            (App, "ResourceManager.App.Infrastructure.DeviceTopology.Snapshots.DeviceTopologySemanticComparer", "JsonOptions"),
            (App, "ResourceManager.App.Application.Settings.AppSettingsMigrator", "WebJsonOptions"),
            (App, "ResourceManager.App.Application.Settings.AppSettingsPatchApplier", "JsonOptions"),
            (ReadWrite, "ResourceManager.App.Infrastructure.DeviceTopology.NativeDisplayCoordinatorPayloadCatalog", "JsonOptions"),
        }) yield return [name, (JsonSerializerOptions)assembly.GetType(name, true)!.GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!];
        yield return ["host-profile", StrictHostManagerProfileLoader.CreateSerializerOptions()];
        yield return ["plan-identity", HostManagerPlanIdentity.CreateSerializerOptions()];
        yield return ["metric-profile", WindowsMetricConfiguration.CreateSerializerOptions()];
        yield return ["freedom-point", FreedomPointRegistry.CreateSerializerOptions()];
        yield return ["freedom-declarations", FreedomPointRegistry.CreateDeclarationSerializerOptions()];
        var software = App.GetType("ResourceManager.App.Infrastructure.Software.JsonSoftwarePolicyProfileProvider", true)!;
        yield return ["software-profile", (JsonSerializerOptions)software.GetMethod("CreateSerializerOptions", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!];
        yield return ["information", ResourceManager.ReadWrite.Information.WindowsInformationFieldProjection.InformationJsonOptions];
        foreach (var assembly in new[] { App, ReadWrite, typeof(ResourceManager.Shared.Runtime.BackendRuntimeIdentityContract).Assembly,
            typeof(ResourceManager.NativeUi.BackendServiceSession).Assembly })
        foreach (var name in new[] { "ManagementInventoryJsonContext", "SoftwareContentJsonContext", "TrustedAccessInvocationJsonContext",
            "SessionLaunchJsonContext", "UserShellExecuteJsonContext", "LoginStartupJsonContext",
            "PackageRuntimeCatalogJson", "StartupModuleStorageJson", "OverlayStartupModulesJson",
            "ElevatedBackendRequestJsonContext", "ElevatedBackendBridgeJsonContext",
            "NativeBackendProbeJsonContext", "DeviceFingerprintJsonContext", "MetricCatalogJsonContext", "ExternalGpuPlacementJsonContext" })
        {
            var type = assembly.GetTypes().FirstOrDefault(t => t.Name == name);
            if (type is null) continue;
            var context = (JsonSerializerContext)type.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            yield return [name, name == "MetricCatalogJsonContext"
                ? ((JsonSerializerContext)Activator.CreateInstance(type, new JsonSerializerOptions
                    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!).Options
                : context.Options];
        }
        foreach (var name in new[] { "ResourceManager.App.Infrastructure.SoftwareIdentity.BundledSoftwareIdentityCatalog", "ResourceManager.App.Infrastructure.SoftwareIssues.BundledSoftwareIssueCatalog", "ResourceManager.App.Infrastructure.SoftwareMetadata.BundledSoftwareMetadataCatalog", "ResourceManager.App.Infrastructure.NetworkTelemetry.KnownNetworkProxyCatalog" })
            yield return [name, (JsonSerializerOptions)App.GetType(name, true)!.GetMethod("CreateSerializerOptions", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!];
        var http = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(http);
        yield return ["http-subscription", http];
    }

    [Theory]
    [MemberData(nameof(Owners))]
    public void OwnerKeepsLegacyBytesAndRoundTrips(string owner, JsonSerializerOptions options)
    {
        IEnumerable<Type> roots = options.TypeInfoResolver is JsonSerializerContext context
            ? RootTypes(context.GetType())
            : ColdRootTypes(owner);
        var legacy = LegacyOptions(owner);
        Assert.Equal(legacy.PropertyNamingPolicy, options.PropertyNamingPolicy);
        Assert.Equal(legacy.PropertyNameCaseInsensitive, options.PropertyNameCaseInsensitive);
        Assert.Equal(legacy.NumberHandling, options.NumberHandling);
        Assert.Equal(legacy.WriteIndented, options.WriteIndented);
        Assert.Equal(legacy.MaxDepth, options.MaxDepth);
        Assert.Equal(legacy.ReadCommentHandling, options.ReadCommentHandling);
        Assert.Equal(legacy.AllowTrailingCommas, options.AllowTrailingCommas);
        Assert.Equal(legacy.UnmappedMemberHandling, options.UnmappedMemberHandling);
        Assert.Equal(legacy.RespectRequiredConstructorParameters, options.RespectRequiredConstructorParameters);
        foreach (var root in roots)
        {
            var fixture = Fixture(root, legacy);
            object? value;
            try { value = JsonSerializer.Deserialize(fixture, root, legacy); }
            catch (NotSupportedException)
            {
                // Response-only converters deliberately reject reads in both contracts.
                Assert.Throws<NotSupportedException>(() => { _ = JsonSerializer.Deserialize(fixture, root, options); });
                continue;
            }
            var expected = JsonSerializer.SerializeToUtf8Bytes(value, root, legacy);
            var actual = JsonSerializer.SerializeToUtf8Bytes(value, root, options);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"{owner}: {root} changed bytes.\nOld: {Encoding.UTF8.GetString(expected)}\nNew: {Encoding.UTF8.GetString(actual)}");
            try
            {
                var readback = JsonSerializer.Deserialize(expected, root, options);
                Assert.Equal(expected, JsonSerializer.SerializeToUtf8Bytes(readback, root, options));
            }
            catch (NotSupportedException)
            {
                Assert.Throws<NotSupportedException>(() => { _ = JsonSerializer.Deserialize(expected, root, legacy); });
            }
        }
    }

    // These are the pre-change owner settings, independent of the generated options under test.
    private static JsonSerializerOptions LegacyOptions(string owner)
    {
        var plain = owner is "host-profile" or "metric-profile" or "freedom-point" or "freedom-declarations" or "software-profile"
            or "plan-identity" or "MetricCatalogJsonContext" or "ManagementInventoryJsonContext"
            or "SoftwareContentJsonContext" or "TrustedAccessInvocationJsonContext" or "NativeBackendProbeJsonContext"
            or "SessionLaunchJsonContext" or "UserShellExecuteJsonContext" or "LoginStartupJsonContext"
            or "PackageRuntimeCatalogJson" or "StartupModuleStorageJson" or "OverlayStartupModulesJson"
            or "ElevatedBackendRequestJsonContext" or "ElevatedBackendBridgeJsonContext"
            or "DeviceFingerprintJsonContext" or "ExternalGpuPlacementJsonContext"
            || owner.EndsWith("ResourceManagerSelfSchedulingControl", StringComparison.Ordinal);
        var options = new JsonSerializerOptions(plain ? JsonSerializerDefaults.General : JsonSerializerDefaults.Web)
        { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        if (owner is "host-profile" or "metric-profile" or "freedom-point" or "freedom-declarations")
            options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        if (owner is "plan-identity" or "MetricCatalogJsonContext" or "LoginStartupJsonContext"
            or "PackageRuntimeCatalogJson" or "StartupModuleStorageJson" or "OverlayStartupModulesJson"
            or "ElevatedBackendBridgeJsonContext") options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        if (owner == "StartupModuleStorageJson") options.WriteIndented = true;
        if (owner == "software-profile") options.PropertyNameCaseInsensitive = true;
        if (owner.EndsWith("JsonManualSoftwareRegistry", StringComparison.Ordinal))
        {
            options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            options.RespectRequiredConstructorParameters = true;
        }
        if (owner.EndsWith("ResourceManagerSelfSchedulingControl", StringComparison.Ordinal))
        {
            options.MaxDepth = 8;
            options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            options.RespectRequiredConstructorParameters = true;
        }
        if (owner.EndsWith("HostManagerRollbackStateEnvelopeCodec", StringComparison.Ordinal)) options.MaxDepth = 32;
        if (owner.EndsWith("JsonAppSettingsStore", StringComparison.Ordinal)
            || owner.EndsWith("JsonOptimizationProtectionStore", StringComparison.Ordinal)
            || owner.EndsWith("JsonGpuPlacementProcessHistoryStore", StringComparison.Ordinal)
            || owner.EndsWith("JsonManualSoftwareRegistry", StringComparison.Ordinal)
            || owner.EndsWith("JsonControlInstanceRegistry", StringComparison.Ordinal)
            || owner.EndsWith("HostManagerMemoryCleanupAttemptJournal", StringComparison.Ordinal)
            || owner.EndsWith("HostManagerProcessEffectValidationScopeAuthority", StringComparison.Ordinal)) options.WriteIndented = true;
        if (owner == "http-subscription")
            options = new(new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions)
            { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        return options;
    }

    [Fact]
    public void WriteOnlySnapshotConvertersKeepLegacyBytes()
    {
        var capturedAt = DateTimeOffset.Parse("2026-10-24T12:34:56Z");
        var process = new ResourceProcessSegment(4242, "中文<&>.exe", @"C:\Apps\中文.exe", 12, 12, 100)
            { ProcessStartKey = long.MaxValue, SharedValue = 4 };
        var software = new ResourceSoftwareSegment("software:test", "中文<&>", "other", "other", 12, 12, 1, [process])
            { SharedValue = 4 };
        var source = new ResourceBreakdownSnapshot(capturedAt,
            [new ResourceBreakdownBar(ResourceBreakdownMetricIds.CpuUsage, "%", ResourceBreakdownScaleModes.Capacity,
                12, 100, 12, [software], SamplingObservationStatus.Current, SamplingObservationStatus.Current)])
        {
            Sampling = ResourceBreakdownSamplingState.Ready(capturedAt),
            Datasets = [new ResourceBreakdownDatasetSamplingState("process.cpu.usage", ResourceBreakdownSamplingStatuses.Ready,
                ulong.MaxValue, 9_007_199_254_740_991, capturedAt, capturedAt, capturedAt, capturedAt.AddSeconds(5), null, null)]
        };
        var requested = new HashSet<string>(["software:test"], StringComparer.OrdinalIgnoreCase);
        object[] snapshots =
        [
            ResourceBreakdownWireSnapshot.Create(source, [ResourceBreakdownMetricIds.CpuUsage], requested),
            ResourceProcessDetailsWireSnapshot.Create(source, [ResourceBreakdownMetricIds.CpuUsage], requested)
        ];
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        foreach (var snapshot in snapshots)
        {
            var expected = JsonSerializer.SerializeToUtf8Bytes(snapshot, snapshot.GetType(), LegacyOptions("http-subscription"));
            Assert.Equal(expected, JsonSerializer.SerializeToUtf8Bytes(snapshot, snapshot.GetType(), options));
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize(expected, snapshot.GetType(), options));
        }
    }

    public static IEnumerable<object[]> SteadyOwners() => Owners()
        .Where(o => ((JsonSerializerOptions)o[1]).TypeInfoResolver is JsonSerializerContext);

    [Theory]
    [MemberData(nameof(SteadyOwners))]
    public void OwnedMetadataUsesNoReflectionEmitAccessors(string owner, JsonSerializerOptions options)
    {
        var context = Assert.IsAssignableFrom<JsonSerializerContext>(options.TypeInfoResolver);
        var pending = new Queue<Type>(RootTypes(context.GetType()));
        var visited = new HashSet<Type>();
        while (pending.TryDequeue(out var type))
        {
            if (!visited.Add(type)) continue;
            Assert.True(visited.Count <= 10000, "Metadata inspection exceeded its type bound.");
            var metadata = options.GetTypeInfo(type);
            Check(metadata.CreateObject);
            foreach (var property in metadata.Properties)
            {
                Check(property.Get); Check(property.Set);
                if (property.Get is not null || property.Set is not null) pending.Enqueue(property.PropertyType);
            }
            if (metadata.Kind is JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary)
            {
                if (type.IsArray) pending.Enqueue(type.GetElementType()!);
                else if (type.IsGenericType) foreach (var argument in type.GenericTypeArguments) pending.Enqueue(argument);
            }
        }
        void Check(Delegate? accessor)
        {
            if (accessor is null) return;
            Assert.False(accessor.Method is DynamicMethod || accessor.Method.DeclaringType is null,
                $"{owner}: emitted accessor {accessor.Method}");
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("number-string")]
    [InlineData("case")]
    [InlineData("comment")]
    [InlineData("trailing-comma")]
    [InlineData("duplicate")]
    public void StrictProfileErrorsMatchLegacy(string mutation)
    {
        var root = JsonNode.Parse(StrictHostManagerProfileLoader.ReadExpandedDefaultBytes())!.AsObject();
        if (mutation == "unknown") root["unexpected"] = true;
        if (mutation == "number-string") root["profile_revision"] = "68";
        if (mutation == "case") { root["Profile_revision"] = root["profile_revision"]!.DeepClone(); root.Remove("profile_revision"); }
        var json = root.ToJsonString();
        if (mutation == "comment") json = "/*comment*/" + json;
        if (mutation == "trailing-comma") json = json[..^1] + ",}";
        if (mutation == "duplicate") json = "{\"profile_revision\":68," + json[1..];
        var bytes = Encoding.UTF8.GetBytes(json);
        var options = new JsonSerializerOptions(StrictHostManagerProfileLoader.CreateSerializerOptions()) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var old = Assert.ThrowsAny<JsonException>(() =>
        {
            StrictHostManagerProfileLoader.ValidateNoDuplicateProperties(bytes);
            JsonSerializer.Deserialize<HostManagerConfigurationProfile>(bytes, options);
        });
        var actual = Assert.Throws<InvalidDataException>(() => StrictHostManagerProfileLoader.LoadBytes(bytes, "strict.json"));
        Assert.Equal(old.Message, actual.InnerException!.Message);
    }

    [Fact]
    public void OneShotContextsAreNotRootedAfterUse()
    {
        var references = CreateColdMetadata();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.All(references, reference => Assert.False(reference.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CreateColdMetadata()
    {
        var cold = Owners().Where(owner => (string)owner[0] is "host-profile" or "metric-profile" or "plan-identity"
            or "freedom-point" or "freedom-declarations" or "software-profile"
            || ((string)owner[0]).Contains("BundledSoftware", StringComparison.Ordinal)
            || ((string)owner[0]).EndsWith("KnownNetworkProxyCatalog", StringComparison.Ordinal)).ToArray();
        foreach (var owner in cold)
        {
            var item = (JsonSerializerOptions)owner[1];
            item.MakeReadOnly();
            foreach (var root in item.TypeInfoResolver is JsonSerializerContext context ? RootTypes(context.GetType()) : ColdRootTypes((string)owner[0]))
                _ = item.GetTypeInfo(root);
        }
        return cold.Select(owner => new WeakReference(owner[1])).ToArray();
    }

    private static IEnumerable<Type> ColdRootTypes(string owner)
    {
        var name = owner switch
        {
            "host-profile" => "HostManagerConfigurationProfile",
            "metric-profile" => "MetricSnapshotConfigurationProfile",
            "freedom-declarations" => "FreedomPointRegistryDeclaration",
            "ResourceManager.App.Infrastructure.SoftwareIdentity.BundledSoftwareIdentityCatalog" => "SoftwareIdentityCatalogDocument",
            "ResourceManager.App.Infrastructure.SoftwareIssues.BundledSoftwareIssueCatalog" => "SoftwareIssueCatalogDocument",
            "ResourceManager.App.Infrastructure.SoftwareMetadata.BundledSoftwareMetadataCatalog" => "SoftwareMetadataCatalogDocument",
            "ResourceManager.App.Infrastructure.NetworkTelemetry.KnownNetworkProxyCatalog" => "ProxySignatureDocument",
            _ => throw new InvalidOperationException(owner)
        };
        return App.GetTypes()
            .Concat(typeof(ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints.CompiledFreedomPointTree).Assembly.GetTypes())
            .Concat(ReadWrite.GetTypes()).Where(t => t.Name == name).Take(1);
    }
    private static IEnumerable<Type> RootTypes(Type context) => context.GetCustomAttributesData()
        .Where(a => a.AttributeType == typeof(JsonSerializableAttribute)).Select(a => (Type)a.ConstructorArguments[0].Value!).Distinct();

    // Bounded breadth-first fixture generation exercises every declared field using the old serializer.
    private static string Fixture(Type rootType, JsonSerializerOptions legacy)
    {
        JsonNode? root = null;
        var pending = new Queue<(Type Type, int Depth, Action<JsonNode?> Set)>();
        pending.Enqueue((rootType, 0, node => root = node));
        var count = 0;
        while (pending.TryDequeue(out var work))
        {
            Assert.True(++count <= 20000, "Fixture exceeded its node bound.");
            var type = Nullable.GetUnderlyingType(work.Type) ?? work.Type;
            if (type == typeof(HostManagerSmartCoordinatorStatus))
            {
                work.Set(JsonNode.Parse(SmartStatusFixture("native-plan-failed")));
                continue;
            }
            if (type == typeof(NativePlanFailureStatus))
            {
                work.Set(JsonSerializer.SerializeToNode(
                    NativePlanFailureStatus.FromReason(SchedulingFactExclusionReason.CapacityExceeded), legacy));
                continue;
            }
            if (type == typeof(MetricInteger)) { work.Set(JsonValue.Create("9007199254740993")); continue; }
            // This declaration validates Kind/Event together; independent primitive fixtures are invalid.
            if (type == typeof(ResourceManager.ReadWrite.ReadVolatility))
            {
                work.Set(JsonSerializer.SerializeToNode(ResourceManager.ReadWrite.ReadVolatility.EventDriven("producer transition"), legacy));
                continue;
            }
            if (type == typeof(string) || type == typeof(object)) { work.Set(JsonValue.Create("中文<&>\\\"\nBIOcanse")); continue; }
            if (type.FullName == "FreedomPoint.PointPath") { work.Set(JsonSerializer.SerializeToNode(new FreedomPoint.PointPath(FreedomPoint.PointPath.MainProgramPackage, 1, 0, FreedomPoint.PointFamily.Build, FreedomPoint.PointKind.Simple, 0), legacy)); continue; }
            if (type == typeof(JsonElement) || typeof(JsonNode).IsAssignableFrom(type)) { work.Set(JsonNode.Parse("{\"kind\":\"中文<&>\",\"n\":1}")); continue; }
            if (type == typeof(bool)) { work.Set(JsonValue.Create(true)); continue; }
            if (type.IsEnum) { work.Set(JsonSerializer.SerializeToNode(Enum.GetValues(type).GetValue(0), type, legacy)); continue; }
            if (type == typeof(DateTimeOffset)) { work.Set(JsonValue.Create("2026-10-24T12:34:56+00:00")); continue; }
            if (type == typeof(DateTime)) { work.Set(JsonValue.Create("2026-10-24T12:34:56Z")); continue; }
            if (type == typeof(TimeSpan)) { work.Set(JsonValue.Create("00:00:01")); continue; }
            if (type == typeof(Guid)) { work.Set(JsonValue.Create("11111111-2222-3333-4444-555555555555")); continue; }
            if (type == typeof(byte[])) { work.Set(JsonValue.Create("AQID")); continue; }
            if (type == typeof(char)) { work.Set(JsonValue.Create("字")); continue; }
            if (type.IsPrimitive || type == typeof(decimal)) { work.Set(JsonValue.Create(1)); continue; }
            var metadata = legacy.GetTypeInfo(type);
            if (metadata.Kind == JsonTypeInfoKind.Dictionary) { work.Set(new JsonObject()); continue; }
            if (metadata.Kind == JsonTypeInfoKind.Enumerable)
            {
                var array = new JsonArray(); work.Set(array);
                var element = type.IsArray ? type.GetElementType() : type.GetInterfaces().Append(type).FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
                if (element is not null && work.Depth < 8) { array.Add((JsonNode?)null); pending.Enqueue((element, work.Depth + 1, node => array[0] = node)); }
                continue;
            }
            if (metadata.Kind == JsonTypeInfoKind.None) { work.Set(JsonValue.Create(1)); continue; }
            var obj = new JsonObject(); work.Set(obj);
            foreach (var property in metadata.Properties)
            {
                if (property.IsExtensionData || property.Get is null && property.Set is null) continue;
                if (work.Depth >= 8 && !property.PropertyType.IsValueType) { obj[property.Name] = null; continue; }
                pending.Enqueue((property.PropertyType, work.Depth + 1, node => obj[property.Name] = node));
            }
        }
        return root?.ToJsonString() ?? "null";
    }
}
