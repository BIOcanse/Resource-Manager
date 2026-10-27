using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackendUids = ResourceManager.App.StateItems.BlockUids;
using UiUids = ResourceManager.NativeUi.StateItems.BlockUids;

namespace Resource_Manager_APP.Tests;

public sealed class StateRegistryTests
{
    // Delete these string-to-UID dictionaries in step 2, when production IDs become UIDs.
    internal static readonly IReadOnlyDictionary<string, int> BackendBlocks = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["$program"] = BackendUids.Program,
        ["NativeEtwLifetimeService"] = BackendUids.NativeEtwLifetimeService,
        ["TrustedAccessRequestOwner"] = BackendUids.TrustedAccessRequestOwner,
        ["TrustedAccessInvocationHostedService"] = BackendUids.TrustedAccessInvocationHostedService,
        ["RuntimeSpecializationCoordinator"] = BackendUids.RuntimeSpecializationCoordinator,
        ["AutomaticProductUpdateService"] = BackendUids.AutomaticProductUpdateService,
        ["HostManagerSamplingSubscriptionOwner"] = BackendUids.HostManagerSamplingSubscriptionOwner,
        ["HostManagerMetricSnapshotOwner"] = BackendUids.HostManagerMetricSnapshotOwner,
        ["HostManagerSoftwareIdentityOwner"] = BackendUids.HostManagerSoftwareIdentityOwner,
        ["HostManagerFileQueryOwner"] = BackendUids.HostManagerFileQueryOwner,
        ["HostManagerDisplayCoordinatorOwner"] = BackendUids.HostManagerDisplayCoordinatorOwner,
        ["HostManagerPublicServiceCoordinatorOwner"] = BackendUids.HostManagerPublicServiceCoordinatorOwner,
        ["HostManagerOperationCoordinatorOwner"] = BackendUids.HostManagerOperationCoordinatorOwner,
        ["SqlitePortableSoftwareRegistry"] = BackendUids.SqlitePortableSoftwareRegistry,
        ["ServiceAutoStartSettings"] = BackendUids.ServiceAutoStartSettings,
        ["ServiceRecoverySettings"] = BackendUids.ServiceRecoverySettings,
        ["ControlDesiredStateReassertion"] = BackendUids.ControlDesiredStateReassertion,
        ["FanCurveTemperatureFeed"] = BackendUids.FanCurveTemperatureFeed,
        ["ControlActualStateReader"] = BackendUids.ControlActualStateReader,
        ["FanControlCoreLifetimeService"] = BackendUids.FanControlCoreLifetimeService,
        ["KernelEtwSessionBroker"] = BackendUids.KernelEtwSessionBroker,
        ["PortableSoftwareDiscoveryService"] = BackendUids.PortableSoftwareDiscoveryService,
        ["EtwPhysicalDiskIoAttributionReader"] = BackendUids.EtwPhysicalDiskIoAttributionReader,
        ["EtwNetworkTcpIpAttributionReader"] = BackendUids.EtwNetworkTcpIpAttributionReader,
        ["GpuPlacementProcessHistoryObserver"] = BackendUids.GpuPlacementProcessHistoryObserver,
        ["SharedResourceSubscriptionMaintenanceService"] = BackendUids.SharedResourceSubscriptionMaintenanceService,
        ["JsonDebugDiagnosticLogWriter"] = BackendUids.JsonDebugDiagnosticLogWriter,
        ["HostManagerReportCoordinatorOwner"] = BackendUids.HostManagerReportCoordinatorOwner,
        ["HostManagerSmartCoordinator"] = BackendUids.HostManagerSmartCoordinator,
        ["present-etw"] = BackendUids.PresentFrameEtwSource,
        ["gpu-allocation-etw"] = BackendUids.GpuAllocationCollector,
        ["DxgkrnlVidMmEtwTelemetryZone"] = BackendUids.DxgkrnlVidMmEtwTelemetryZone,
        ["DeviceTopologySnapshotProvider"] = BackendUids.DeviceTopologySnapshotProvider,
        ["GpuTelemetryWorkerSupervisor"] = BackendUids.GpuTelemetryWorkerSupervisor,
        ["WindowsHardwareMetricSampler"] = BackendUids.WindowsHardwareMetricSampler,
        ["CpuTopologySnapshotProvider"] = BackendUids.CpuTopologySnapshotProvider,
        ["EtwCpuCoreResidencyReader"] = BackendUids.EtwCpuCoreResidencyReader,
        ["EtwSystemInterruptSnapshotSource"] = BackendUids.EtwSystemInterruptSnapshotSource,
        ["WindowsResourceBreakdownSampler"] = BackendUids.WindowsResourceBreakdownSampler
    };
    internal static readonly IReadOnlyDictionary<string, int> UiBlocks = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["$program"] = UiUids.Program,
        ["native-ui:main"] = UiUids.MainRoot,
        ["native-ui:tray"] = UiUids.TrayRoot,
        ["native-ui:single-instance"] = UiUids.SingleInstance,
        ["native-ui:backend-connection"] = UiUids.BackendConnection,
        ["native-ui:main-window"] = UiUids.MainWindow,
        ["native-ui:desktop-observation"] = UiUids.DesktopObservation,
        ["native-ui:overlay"] = UiUids.Overlay,
        ["native-ui:loopback-proxy"] = UiUids.LoopbackProxy,
        ["native-ui:webview-host"] = UiUids.WebViewHost,
        ["native-ui:tray-notifications"] = UiUids.TrayNotifications
    };

    [Theory]
    [InlineData("backend")]
    [InlineData("ui")]
    public void GeneratedVersionAndConstantsMatchRepositoryBytes(string process)
    {
        var bytes = File.ReadAllBytes(RegistryPath());
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.UTF8.GetBytes($"blob {bytes.Length}\0"));
        hash.AppendData(bytes);
        var version = Convert.ToHexStringLower(hash.GetHashAndReset());
        Assert.Equal(version, process == "backend" ? ResourceManager.App.StateItems.StateRegistry.Version
            : ResourceManager.NativeUi.StateItems.StateRegistry.Version);
        using var document = JsonDocument.Parse(bytes);
        var blockType = process == "backend" ? typeof(BackendUids) : typeof(UiUids);
        var itemType = process == "backend" ? typeof(ResourceManager.App.StateItems.StateItemUids)
            : typeof(ResourceManager.NativeUi.StateItems.StateItemUids);
        foreach (var kind in new[] { "block", "item" })
        {
            var registered = document.RootElement.GetProperty("entries").EnumerateArray()
                .Where(entry => entry.GetProperty("process").GetString() == process && entry.GetProperty("kind").GetString() == kind)
                .ToDictionary(entry => entry.GetProperty("name").GetString()!, entry => entry.GetProperty("uid").GetInt32());
            var generated = (kind == "block" ? blockType : itemType).GetFields(BindingFlags.Public | BindingFlags.Static)
                .ToDictionary(field => field.Name, field => (int)field.GetRawConstantValue()!);
            Assert.Equal(registered.OrderBy(pair => pair.Key), generated.OrderBy(pair => pair.Key));
        }
    }

    [Fact]
    public void GeneratedNamesFollowTheBuildSwitchInBothProcesses()
    {
#if RM_STATE_NAMES
        Assert.Equal("Program", ResourceManager.App.StateItems.StateNames.Name(BackendUids.Program));
        Assert.Equal("Program", ResourceManager.NativeUi.StateItems.StateNames.Name(UiUids.Program));
        Assert.Null(ResourceManager.App.StateItems.StateNames.Name(0));
        Assert.Null(ResourceManager.NativeUi.StateItems.StateNames.Comment(-1));
#else
        Assert.Null(typeof(BackendUids).Assembly.GetType("ResourceManager.App.StateItems.StateNames"));
        Assert.Null(typeof(UiUids).Assembly.GetType("ResourceManager.NativeUi.StateItems.StateNames"));
#endif
    }

    internal static void AssertBlocks(string process, IReadOnlyDictionary<string, int> mapping,
        IEnumerable<string> productionIds, params string[] inactive)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(RegistryPath()));
        var registered = document.RootElement.GetProperty("entries").EnumerateArray()
            .Where(entry => entry.GetProperty("process").GetString() == process && entry.GetProperty("kind").GetString() == "block")
            .Select(entry => entry.GetProperty("uid").GetInt32()).ToArray();
        Assert.Equal(mapping.Count, mapping.Values.Distinct().Count());
        Assert.Equal(registered.Order(), mapping.Values.Order());
        var production = productionIds.ToArray();
        Assert.Equal(production.Length, production.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(mapping.Keys.Except(inactive).Order(), production.Order());
        Assert.All(production, id => Assert.Single(registered, uid => uid == mapping[id]));
        if (process == "backend")
            Assert.Equal(registered.Except(inactive.Select(id => mapping[id])), production.Select(id => mapping[id]));
    }

    private static string RegistryPath([CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../src/Shared/StateItems/state-items.json"));
}
