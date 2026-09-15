using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerDisplayCoordinatorTests
{
    [Fact]
    public void DefaultProfilePublishesExactDisplayCoordinatorPlan()
    {
        var hostPlan = HostManagerTestPlanFactory.CreatePlan();
        var plan = hostPlan.DisplayCoordinator;

        Assert.True(plan.IsPublished);
        Assert.Equal(NativeDisplayCoordinatorAbi.Version, plan.Build.AbiVersion);
        Assert.Equal("display_coordinator", plan.Build.NativeModule);
        Assert.Equal(59UL << 32, plan.Recreate.ConfigurationGeneration);
        Assert.Equal(5U, plan.Recreate.Capacity.MaximumSourceCount);
        Assert.Equal(256U, plan.Recreate.Capacity.MaximumObservationCount);
        Assert.Equal(512U, plan.Recreate.Capacity.MaximumNodeCount);
        Assert.Equal(8192U, plan.Recreate.Capacity.MaximumDiffEntryCount);
        Assert.Equal(67_108_864UL, plan.Recreate.Capacity.ResidentByteBudget);
        Assert.Equal(
            "display-coordinator/display-coordinator-v3.bin",
            plan.Recreate.PersistenceRelativePath);
        Assert.Equal(1UL, plan.Recreate.RequiredSourceMask);
        Assert.Equal(0b1_1110UL, plan.Recreate.OptionalSourceMask);
        Assert.True(hostPlan.DeploymentDigests.DisplayCoordinator.IsPublished);
    }

    [Fact]
    public void ProfileRejectsMissingPersistencePathAndRetiredAbi()
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["host_recreate"]!["display_coordinator"]!
                .AsObject()
                .Remove("persistence_relative_path");
        }));
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["build_specialize"]!["display_coordinator_abi_version"] = 0x0002_0000U;
        }));
    }

    [Fact]
    public void PayloadCatalogUsesStableTypedHandlesAndRoundTripsExactPayload()
    {
        var path = CreatePath();
        var catalog = new NativeDisplayCoordinatorPayloadCatalog();

        var first = catalog.AddDisplayPath(path);
        var second = catalog.AddDisplayPath(path);
        var evidence = catalog.AddEvidence([1, 2, 3, 4]);

        Assert.Equal(first, second);
        Assert.NotEqual(first, evidence);
        Assert.False(first.IsZero);
        var exported = catalog.Export();
        var imported = new NativeDisplayCoordinatorPayloadCatalog();
        imported.Import(exported);
        Assert.Equal(path, imported.ResolveDisplayPath(first));
        Assert.Equal(
            exported.Select(static entry => entry.Handle),
            imported.Export().Select(static entry => entry.Handle));
    }

    [Fact]
    public void PayloadCatalogCloneIsIsolatedWithoutSerializationRoundTrip()
    {
        var catalog = new NativeDisplayCoordinatorPayloadCatalog();
        var pathHandle = catalog.AddDisplayPath(CreatePath());

        var clone = catalog.Clone();
        var evidenceHandle = clone.AddEvidence([1, 2, 3, 4]);

        Assert.Equal(CreatePath(), clone.ResolveDisplayPath(pathHandle));
        Assert.Single(catalog.Export());
        Assert.Equal(2, clone.Export().Count);
        Assert.DoesNotContain(
            catalog.Export(),
            entry => entry.Handle.Equals(evidenceHandle));
    }

    [Fact]
    public void ProjectedFactsUseZeroForAbsentTextBindings()
    {
        var batches = NativeDisplayCoordinatorObservationProjector.Create(
            [CreatePath() with { MonitorFriendlyName = null }],
            [],
            new NativeDisplayCoordinatorPayloadCatalog(),
            DateTimeOffset.UtcNow);
        var displayConfig = Assert.Single(
            batches,
            batch => batch.Source == NativeDisplaySource.DisplayConfig);
        var fact = Assert.Single(displayConfig.Facts);

        Assert.Equal(0U, fact.FriendlyNameTextIndex);
        Assert.Equal(0U, fact.EdidSerialTextIndex);
        Assert.Equal(0U, fact.EvidenceTextIndex);
        Assert.Equal(
            0UL,
            fact.ValidMask
                & (ulong)(
                    NativeDisplayFactValidity.FriendlyName
                    | NativeDisplayFactValidity.EdidSerial
                    | NativeDisplayFactValidity.Evidence));
        Assert.NotEqual(0U, fact.MatchTextIndex);
        Assert.NotEqual(
            0UL,
            fact.ValidMask
                & (ulong)NativeDisplayFactValidity.AdapterDevicePath);
    }

    [Fact]
    public void EdidProductNameDoesNotLeaveAnUnreferencedPathFriendlyName()
    {
        var path = CreatePath() with
        {
            Edid = new DeviceTopologyEdidCapabilities(
                Version: "1.4",
                ProductName: "EDID display",
                SerialNumber: "1234",
                BitsPerColorChannel: 8,
                DigitalInterface: DeviceEdidDigitalInterfaces.DisplayPort,
                HdrFormats: null,
                WidthMillimeters: 600,
                HeightMillimeters: 340,
                DisplayTechnology: BackendMessage.Create(
                    BackendMessageDomains.DeviceTopology,
                    BackendMessageCodes.DeviceTopology.DisplayTechnologyDigital,
                    DeviceEdidDigitalInterfaces.DisplayPort),
                PanelTechnology: null)
        };
        var batch = Assert.Single(
            NativeDisplayCoordinatorObservationProjector.Create(
                [path],
                [],
                new NativeDisplayCoordinatorPayloadCatalog(),
                DateTimeOffset.UtcNow),
            item => item.Source == NativeDisplaySource.Edid);
        var fact = Assert.Single(batch.Facts);

        Assert.Equal(4, batch.Texts.Length);
        Assert.All(
            batch.Texts.Select(static (_, index) => checked((uint)index)),
            index => Assert.True(
                fact.MonitorPathTextIndex == index
                || fact.SourceDeviceTextIndex == index
                || fact.FriendlyNameTextIndex == index
                || fact.EdidSerialTextIndex == index));
    }

    [Theory]
    [InlineData(true, false, (uint)NativeDisplaySource.Dxgi)]
    [InlineData(false, true, (uint)NativeDisplaySource.Edid)]
    public void PartialCapabilityReadFailureRetainsTheWholeSourceLastGood(
        bool edidComplete,
        bool dxgiComplete,
        uint sourceValue)
    {
        var path = CreatePath() with
        {
            EdidObservationComplete = edidComplete,
            DxgiObservationComplete = dxgiComplete
        };
        var batch = Assert.Single(
            NativeDisplayCoordinatorObservationProjector.Create(
                [path],
                [],
                new NativeDisplayCoordinatorPayloadCatalog(),
                DateTimeOffset.UtcNow),
            item => item.Source == (NativeDisplaySource)sourceValue);

        Assert.Equal(NativeDisplaySourceStatus.Unavailable, batch.Status);
        Assert.Empty(batch.Facts);
        Assert.Empty(batch.Texts);
        Assert.Empty(batch.TextBytes);
    }

    [Fact]
    public void PersistenceEnvelopeRoundTripsAndRejectsTampering()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"rm-display-coordinator-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "display.bin");
        try
        {
            var catalog = new NativeDisplayCoordinatorPayloadCatalog();
            _ = catalog.AddDisplayPath(CreatePath());
            var generation = 36UL << 32;
            var header = new NativeDisplayPersistenceHeader
            {
                AbiVersion = NativeDisplayCoordinatorAbi.Version,
                StructSize = checked((uint)Unsafe.SizeOf<NativeDisplayPersistenceHeader>()),
                ConfigurationGeneration = generation,
                OperationEpoch = 9,
                ContentGeneration = 7,
                StateRevision = 11,
                LastRefreshEpoch = 5,
                Phase = (uint)NativeDisplayCoordinatorPhase.Ready
            };
            var store = new NativeDisplayCoordinatorPersistenceStore(path);
            store.Save(
                generation,
                new NativeDisplayCoordinatorPersistenceImage(
                    header,
                    [],
                    [],
                    [],
                    catalog.Export()));

            var loaded = Assert.IsType<NativeDisplayCoordinatorPersistenceImage>(
                store.Load(generation));
            Assert.Equal(9UL, loaded.Header.OperationEpoch);
            Assert.Equal(7UL, loaded.Header.ContentGeneration);
            Assert.Equal(11UL, loaded.Header.StateRevision);
            Assert.Single(loaded.Payloads);
            Assert.Null(store.Load(generation + (1UL << 32)));

            var bytes = File.ReadAllBytes(path);
            bytes[32] ^= 0x5A;
            File.WriteAllBytes(path, bytes);
            Assert.Throws<InvalidDataException>(() => store.Load(generation));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void MonotonicRefreshClockContinuesAcrossRestartAndSameTick()
    {
        const ulong persistedBeforeRestart = 81_967_009;

        var afterRestart = NativeDisplayCoordinatorWorkspace.AdvanceMonotonicMilliseconds(
            persistedBeforeRestart,
            observed: 1_470_100);
        var sameTick = NativeDisplayCoordinatorWorkspace.AdvanceMonotonicMilliseconds(
            afterRestart,
            observed: afterRestart);
        var caughtUp = NativeDisplayCoordinatorWorkspace.AdvanceMonotonicMilliseconds(
            sameTick,
            observed: sameTick + 100);

        Assert.Equal(persistedBeforeRestart + 1, afterRestart);
        Assert.Equal(afterRestart + 1, sameTick);
        Assert.Equal(sameTick + 100, caughtUp);
        Assert.Throws<InvalidOperationException>(() =>
            NativeDisplayCoordinatorWorkspace.AdvanceMonotonicMilliseconds(
                ulong.MaxValue,
                observed: 1));
    }

    [Fact]
    public void ProductionHasOneDisplayAuthorityAndTopologyRefreshesItDirectly()
    {
        var appRoot = FindAppRoot();
        var displayRoot = Path.Combine(
            appRoot,
            "Infrastructure",
            "DeviceTopology",
            "Display");
        foreach (var retired in new[]
                 {
                     "DeviceTopologyDisplayConnectorResolver.cs",
                     Path.Combine(
                         "..",
                         "..",
                         "Monitoring",
                         "DisplayOutput",
                         "DisplayOutputIdentity.cs"),
                     Path.Combine(
                         "..",
                         "..",
                         "Monitoring",
                         "DisplayOutput",
                         "DisplayOutputCapabilitySemanticComparer.cs"),
                     Path.Combine(
                         "..",
                         "..",
                         "Monitoring",
                         "DisplayOutput",
                         "WindowsDisplayOutputCapabilityReader.cs")
                 })
        {
            Assert.False(File.Exists(Path.GetFullPath(Path.Combine(displayRoot, retired))));
        }

        var dxgi = File.ReadAllText(Path.Combine(
            displayRoot,
            "WindowsDxgiDisplayCapabilityReader.cs"));
        var edid = File.ReadAllText(Path.Combine(
            displayRoot,
            "WindowsMonitorEdidReader.cs"));
        Assert.DoesNotContain("CacheLifetime", dxgi, StringComparison.Ordinal);
        Assert.DoesNotContain("topologyFactory", dxgi, StringComparison.Ordinal);
        Assert.DoesNotContain("CacheLifetime", edid, StringComparison.Ordinal);
        Assert.DoesNotContain("CacheEntry", edid, StringComparison.Ordinal);

        var monitoringRegistration = File.ReadAllText(Path.Combine(
            appRoot,
            "Hosting",
            "MonitoringServiceRegistration.cs"));
        Assert.DoesNotContain(
            "AddHostedService(static provider =>\n            provider.GetRequiredService<DisplayOutputCapabilityProvider>())",
            monitoringRegistration.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);

        var runtimeRegistration = File.ReadAllText(Path.Combine(
            appRoot,
            "Hosting",
            "RuntimeSpecializationServiceRegistration.cs"));
        var ownerIndex = runtimeRegistration.IndexOf(
            "AddHostedServiceAlias<HostManagerDisplayCoordinatorOwner>()",
            StringComparison.Ordinal);
        Assert.True(ownerIndex >= 0);
        Assert.DoesNotContain(
            "DisplayOutputCapabilityProvider",
            runtimeRegistration,
            StringComparison.Ordinal);

        var topologyReader = File.ReadAllText(Path.Combine(
            appRoot,
            "Infrastructure",
            "DeviceTopology",
            "WindowsDeviceTopologyReader.cs"));
        Assert.Contains("displayCoordinator.Refresh()", topologyReader, StringComparison.Ordinal);
        Assert.DoesNotContain("displayCoordinator.Read()", topologyReader, StringComparison.Ordinal);
    }

    private static DeviceTopologyDisplayPath CreatePath()
        => new(
            AdapterHighPart: 1,
            AdapterLowPart: 2,
            SourceId: 3,
            TargetId: 4,
            OutputTechnology: 10,
            ConnectorInstance: 5,
            MonitorFriendlyName: "Test display",
            MonitorDevicePath: @"DISPLAY\TEST\1",
            Width: 2560,
            Height: 1440,
            RefreshRateNumerator: 165_000,
            RefreshRateDenominator: 1000,
            Active: true,
            TargetAvailable: true,
            AdapterDevicePath: @"PCI\VEN_10DE",
            SourceDeviceName: @"\\.\DISPLAY1",
            PositionX: 0,
            PositionY: 0);

    private static string FindAppRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath)!;
        var appRoot = Path.GetFullPath(Path.Combine(
            sourceDirectory,
            "..",
            "Resource Manager-APP"));
        return File.Exists(Path.Combine(appRoot, "ResourceManager.App.csproj"))
            ? appRoot
            : throw new DirectoryNotFoundException(
                "Could not locate Resource Manager-APP from the test source path.");
    }
}
