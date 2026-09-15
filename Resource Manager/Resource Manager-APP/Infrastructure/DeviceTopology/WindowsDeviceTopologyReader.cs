using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

public sealed class WindowsDeviceTopologyReader : IDeviceTopologyReader
{
    private static readonly Regex ExplicitUsbGigabitRate = new(
        @"(?<![\d.])(?<rate>80|40|20|10|5)\s*Gbps\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ExplicitUsb480MegabitRate = new(
        @"(?<![\d.])480\s*Mbps\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private const string UnknownSpeed = "未知";

    private readonly DeviceIdCatalog deviceIdCatalog;
    private readonly HostManagerDisplayCoordinatorOwner displayCoordinator;

    public WindowsDeviceTopologyReader(
        DeviceIdCatalog deviceIdCatalog,
        HostManagerDisplayCoordinatorOwner displayCoordinator)
    {
        this.deviceIdCatalog = deviceIdCatalog;
        this.displayCoordinator = displayCoordinator;
    }

    public DeviceTopologySnapshot ReadSnapshot()
    {
        var diagnostics = new List<DeviceTopologySourceDiagnostic>();
        var notes = new List<BackendMessage>
        {
            Note(BackendMessageCodes.DeviceTopology.EnumerationOnly)
        };
        var system = ReadSystemIdentity(notes);
        var pnp = DeviceTopologyWmiUtilities.QueryObjects(
            @"root\CIMV2",
            "SELECT Name, Caption, Description, Manufacturer, Service, Status, PNPClass, PNPDeviceID, DeviceID, ClassGuid, HardwareID, CompatibleID FROM Win32_PnPEntity");

        if (pnp.Error is not null)
        {
            AddRequiredSourceFailure(
                diagnostics,
                notes,
                "pnp-wmi",
                "device-topology-pnp-wmi-incomplete",
                BackendMessageCodes.DeviceTopology.PnpEnumerationFailed,
                pnp.Error);
        }

        var nativeDevices = WindowsDeviceTopologyNativeReader.ReadSnapshot();
        if (nativeDevices.Error is not null)
        {
            AddRequiredSourceFailure(
                diagnostics,
                notes,
                "setupapi-cfgmgr32",
                "device-topology-native-device-properties-incomplete",
                BackendMessageCodes.DeviceTopology.NativeDevicePropertiesFailed,
                nativeDevices.Error);
        }

        var usbPorts = WindowsUsbHubIoctlReader.ReadSnapshot();
        if (usbPorts.Error is not null)
        {
            AddRequiredSourceFailure(
                diagnostics,
                notes,
                "usb-hub-ioctl",
                "device-topology-usb-hub-ioctl-incomplete",
                BackendMessageCodes.DeviceTopology.UsbHubIoctlFailed,
                usbPorts.Error);
        }

        var networkAdapters = WindowsNetworkAdapterTopologyReader.ReadSnapshot();
        if (networkAdapters.Error is not null)
        {
            AddRequiredSourceFailure(
                diagnostics,
                notes,
                "network-adapters",
                "device-topology-network-adapters-incomplete",
                BackendMessageCodes.DeviceTopology.NetworkAdapterPropertiesFailed,
                networkAdapters.Error);
        }

        HostManagerDisplayCoordinatorReadModel displaySnapshot;
        try
        {
            displaySnapshot = displayCoordinator.Refresh();
            if (displaySnapshot.State != HostManagerDisplayCoordinatorReadState.Ready)
            {
                AddRequiredSourceFailure(
                    diagnostics,
                    notes,
                    "display-coordinator",
                    "device-topology-display-coordinator-incomplete",
                    BackendMessageCodes.DeviceTopology.DisplayCoordinatorNotReady,
                    displaySnapshot.State.ToString());
            }
        }
        catch (Exception ex)
        {
            AddRequiredSourceFailure(
                diagnostics,
                notes,
                "display-coordinator",
                "device-topology-display-coordinator-read-failed",
                BackendMessageCodes.DeviceTopology.DisplayCoordinatorReadFailed,
                ex.Message);
            displaySnapshot = new HostManagerDisplayCoordinatorReadModel(
                HostManagerDisplayCoordinatorReadState.Warming,
                [],
                0,
                0);
        }

        var storageDevices = WindowsStorageDeviceCapabilityReader.ReadSnapshot();
        if (storageDevices.Error is not null)
        {
            AddRequiredSourceFailure(
                diagnostics,
                notes,
                "storage-capabilities",
                "device-topology-storage-capabilities-incomplete",
                BackendMessageCodes.DeviceTopology.StorageCapabilitiesIncomplete,
                storageDevices.Error);
        }

        var displayPorts = displaySnapshot.DisplayPaths
            .Select(CreateDisplayPathCandidate)
            .ToArray();
        var representedDisplayDevices = displayPorts
            .Select(static port => NormalizeDeviceId(port.DeviceId))
            .Where(static id => id.StartsWith("DISPLAY\\", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var usbTopology = ReadUsbTopologyIndex(
            pnp.Rows,
            notes,
            diagnostics);
        var pnpCandidates = pnp.Rows
            .Select(row => CreateCandidate(
                row,
                usbTopology,
                nativeDevices.Devices,
                usbPorts.PortsByDriverKey,
                networkAdapters.Adapters,
                storageDevices))
            .Where(static item => item is not null)
            .Select(static item => item!)
            .ToArray();
        var pnpPorts = ResolveDuplicateDeviceIds(pnpCandidates, notes)
            .Where(port => !representedDisplayDevices.Contains(NormalizeDeviceId(port.DeviceId)))
            .ToArray();
        var representedUsbPorts = pnpPorts
            .Where(static port => port.Usb is not null)
            .Select(static port => DeviceTopologyUsbConnectorGrouper.CreatePortIdentity(
                port.Usb!.HubDevicePath,
                port.Usb.PortNumber))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unrepresentedConnectors = DeviceTopologyUsbConnectorGrouper
            .GroupUserConnectablePorts(usbPorts.Ports)
            .Where(group => !group.Ports.Any(port => representedUsbPorts.Contains(
                DeviceTopologyUsbConnectorGrouper.CreatePortIdentity(port.HubDevicePath, port.PortNumber))))
            .Select(CreateUsbConnectorCandidate);
        var ports = pnpPorts
            .Concat(unrepresentedConnectors)
            .Concat(displayPorts)
            .OrderBy(GetConnectorSortKey)
            .ThenBy(static port => port.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (ports.Length == 0)
        {
            notes.Add(Note(BackendMessageCodes.DeviceTopology.NoVisibleNodes));
        }
        if (diagnostics.Count > 0)
        {
            throw new DeviceTopologyIncompleteSourcesException(diagnostics);
        }

        return new DeviceTopologySnapshot(
            DateTimeOffset.Now,
            system,
            ports,
            notes);
    }

    internal static IReadOnlyList<DeviceTopologyPort> ResolveDuplicateDeviceIds(
        IEnumerable<DeviceTopologyPort> candidates,
        ICollection<BackendMessage> notes)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(notes);

        var resolved = new List<DeviceTopologyPort>();
        foreach (var group in candidates
            .GroupBy(static port => port.DeviceId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var canonical = group
                .Select(static port => (
                    Port: DeviceTopologySemanticComparer.NormalizePort(port),
                    Payload: DeviceTopologySemanticComparer.ComputeCanonicalPortPayload(port)))
                .GroupBy(static item => item.Payload, StringComparer.Ordinal)
                .ToArray();
            if (canonical.Length != 1)
            {
                notes.Add(Note(BackendMessageCodes.DeviceTopology.ConflictingFacts, group.Key));
                continue;
            }

            resolved.Add(canonical[0].First().Port);
        }

        return resolved;
    }

    internal static DeviceTopologySystemIdentity ReadSystemIdentity(ICollection<BackendMessage> notes)
    {
        var bios = FirstRow(DeviceTopologyWmiUtilities.QueryObjects(
            @"root\CIMV2",
            "SELECT Manufacturer, SMBIOSBIOSVersion, Version FROM Win32_BIOS"));
        var computer = FirstRow(DeviceTopologyWmiUtilities.QueryObjects(
            @"root\CIMV2",
            "SELECT Manufacturer, Model, SystemFamily, SystemSKUNumber FROM Win32_ComputerSystem"));
        var baseBoard = FirstRow(DeviceTopologyWmiUtilities.QueryObjects(
            @"root\CIMV2",
            "SELECT Manufacturer, Product FROM Win32_BaseBoard"));

        var manufacturer = Clean(DeviceTopologyWmiUtilities.ReadString(computer, "Manufacturer"))
            ?? Clean(DeviceTopologyWmiUtilities.ReadString(bios, "Manufacturer"))
            ?? "Unknown manufacturer";
        var model = Clean(DeviceTopologyWmiUtilities.ReadString(computer, "Model"))
            ?? Clean(DeviceTopologyWmiUtilities.ReadString(baseBoard, "Product"))
            ?? "Unknown model";
        var family = Clean(DeviceTopologyWmiUtilities.ReadString(computer, "SystemFamily"));
        var sku = Clean(DeviceTopologyWmiUtilities.ReadString(computer, "SystemSKUNumber"));
        var logo = ResolveLogoText(manufacturer, model, family, sku);
        var brand = ResolveBrandDisplayName(manufacturer, model, family, logo);

        if (manufacturer.Equals("Unknown manufacturer", StringComparison.OrdinalIgnoreCase)
            || model.Equals("Unknown model", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add(Note(BackendMessageCodes.DeviceTopology.IncompleteBrandModel));
        }

        return new DeviceTopologySystemIdentity(
            manufacturer,
            model,
            brand,
            logo,
            Clean(DeviceTopologyWmiUtilities.ReadString(bios, "SMBIOSBIOSVersion")
                ?? DeviceTopologyWmiUtilities.ReadString(bios, "Version")),
            Clean(DeviceTopologyWmiUtilities.ReadString(baseBoard, "Manufacturer")),
            Clean(DeviceTopologyWmiUtilities.ReadString(baseBoard, "Product")));
    }

    private static Dictionary<string, object?>? FirstRow(DeviceTopologyWmiQueryResult result)
    {
        return result.Rows.Count > 0 ? result.Rows[0] : null;
    }

    private static UsbTopologyIndex ReadUsbTopologyIndex(
        IReadOnlyList<Dictionary<string, object?>> pnpRows,
        ICollection<BackendMessage> notes,
        ICollection<DeviceTopologySourceDiagnostic> diagnostics)
    {
        var devices = new Dictionary<string, UsbTopologyDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in pnpRows)
        {
            var deviceId = Clean(DeviceTopologyWmiUtilities.ReadString(row, "PNPDeviceID"))
                ?? Clean(DeviceTopologyWmiUtilities.ReadString(row, "DeviceID"));
            var displayName = Clean(DeviceTopologyWmiUtilities.ReadString(row, "Name"))
                ?? Clean(DeviceTopologyWmiUtilities.ReadString(row, "Caption"))
                ?? Clean(DeviceTopologyWmiUtilities.ReadString(row, "Description"));
            AddUsbDevice(devices, deviceId, displayName, "PnP Device", Clean(DeviceTopologyWmiUtilities.ReadString(row, "PNPClass")));
        }

        var controllers = DeviceTopologyWmiUtilities.QueryObjects(
            @"root\CIMV2",
            "SELECT Name, Caption, Description, Manufacturer, DeviceID, PNPDeviceID, Status FROM Win32_USBController");
        if (controllers.Error is not null)
        {
            AddRequiredSourceFailure(
                diagnostics,
                notes,
                "usb-controller-wmi",
                "device-topology-usb-controller-wmi-incomplete",
                BackendMessageCodes.DeviceTopology.UsbControllerEnumerationFailed,
                controllers.Error);
        }

        foreach (var row in controllers.Rows)
        {
            AddUsbDevice(
                devices,
                Clean(DeviceTopologyWmiUtilities.ReadString(row, "PNPDeviceID"))
                    ?? Clean(DeviceTopologyWmiUtilities.ReadString(row, "DeviceID")),
                Clean(DeviceTopologyWmiUtilities.ReadString(row, "Name"))
                    ?? Clean(DeviceTopologyWmiUtilities.ReadString(row, "Caption"))
                    ?? Clean(DeviceTopologyWmiUtilities.ReadString(row, "Description")),
                "USB Host Controller",
                "USB");
        }

        var hubs = DeviceTopologyWmiUtilities.QueryObjects(
            @"root\CIMV2",
            "SELECT Name, Caption, Description, DeviceID, PNPDeviceID, Status FROM Win32_USBHub");
        if (hubs.Error is not null)
        {
            AddRequiredSourceFailure(
                diagnostics,
                notes,
                "usb-hub-wmi",
                "device-topology-usb-hub-wmi-incomplete",
                BackendMessageCodes.DeviceTopology.UsbHubEnumerationFailed,
                hubs.Error);
        }

        foreach (var row in hubs.Rows)
        {
            AddUsbDevice(
                devices,
                Clean(DeviceTopologyWmiUtilities.ReadString(row, "PNPDeviceID"))
                    ?? Clean(DeviceTopologyWmiUtilities.ReadString(row, "DeviceID")),
                Clean(DeviceTopologyWmiUtilities.ReadString(row, "Name"))
                    ?? Clean(DeviceTopologyWmiUtilities.ReadString(row, "Caption"))
                    ?? Clean(DeviceTopologyWmiUtilities.ReadString(row, "Description")),
                "USB Hub",
                "USB");
        }

        var associations = DeviceTopologyWmiUtilities.QueryObjects(
            @"root\CIMV2",
            "SELECT Antecedent, Dependent FROM Win32_USBControllerDevice");
        if (associations.Error is not null)
        {
            AddRequiredSourceFailure(
                diagnostics,
                notes,
                "usb-association-wmi",
                "device-topology-usb-association-wmi-incomplete",
                BackendMessageCodes.DeviceTopology.UsbControllerRelationshipFailed,
                associations.Error);
        }

        var upstreamByDeviceId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in associations.Rows)
        {
            var upstream = ExtractWmiDeviceReference(DeviceTopologyWmiUtilities.ReadString(row, "Antecedent"));
            var dependent = ExtractWmiDeviceReference(DeviceTopologyWmiUtilities.ReadString(row, "Dependent"));
            if (string.IsNullOrWhiteSpace(upstream) || string.IsNullOrWhiteSpace(dependent))
            {
                continue;
            }

            var dependentKey = NormalizeDeviceId(dependent);
            var upstreamKey = NormalizeDeviceId(upstream);
            if (dependentKey.Length == 0
                || upstreamKey.Length == 0
                || dependentKey.Equals(upstreamKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            upstreamByDeviceId.TryAdd(dependentKey, upstreamKey);
        }

        if (associations.Error is null && associations.Rows.Count == 0)
        {
            notes.Add(Note(BackendMessageCodes.DeviceTopology.UsbChainUnavailable));
        }

        return new UsbTopologyIndex(devices, upstreamByDeviceId);
    }

    private static BackendMessage Note(byte code, params string[] args)
        => BackendMessage.Create(BackendMessageDomains.DeviceTopology, code, args);

    private static void AddRequiredSourceFailure(
        ICollection<DeviceTopologySourceDiagnostic> diagnostics,
        ICollection<BackendMessage> notes,
        string sourceId,
        string code,
        byte messageCode,
        string detail)
    {
        var message = Note(messageCode, detail);
        diagnostics.Add(new DeviceTopologySourceDiagnostic(
            sourceId,
            DeviceTopologySourceDiagnosticStatus.RequiredIncomplete,
            code,
            message));
        notes.Add(message);
    }

    private static void AddUsbDevice(
        IDictionary<string, UsbTopologyDevice> devices,
        string? deviceId,
        string? displayName,
        string kind,
        string? pnpClass)
    {
        var key = NormalizeDeviceId(deviceId);
        if (key.Length == 0)
        {
            return;
        }

        var name = Clean(displayName) ?? key;
        devices[key] = new UsbTopologyDevice(key, name, kind, pnpClass);
    }

    private DeviceTopologyPort? CreateCandidate(
        Dictionary<string, object?> row,
        UsbTopologyIndex usbTopology,
        IReadOnlyDictionary<string, DeviceTopologyNativeDevice> nativeDevices,
        IReadOnlyDictionary<string, DeviceTopologyUsbPort> usbPortsByDriverKey,
        IReadOnlyDictionary<string, DeviceTopologyNetworkAdapter> networkAdapters,
        DeviceTopologyStorageSnapshot storageDevices)
    {
        var deviceId = Clean(DeviceTopologyWmiUtilities.ReadString(row, "PNPDeviceID"))
            ?? Clean(DeviceTopologyWmiUtilities.ReadString(row, "DeviceID"))
            ?? string.Empty;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        var name = Clean(DeviceTopologyWmiUtilities.ReadString(row, "Name"))
            ?? Clean(DeviceTopologyWmiUtilities.ReadString(row, "Caption"))
            ?? Clean(DeviceTopologyWmiUtilities.ReadString(row, "Description"))
            ?? deviceId;
        var pnpClass = Clean(DeviceTopologyWmiUtilities.ReadString(row, "PNPClass"));
        var description = Clean(DeviceTopologyWmiUtilities.ReadString(row, "Description"));
        var manufacturer = Clean(DeviceTopologyWmiUtilities.ReadString(row, "Manufacturer"));
        var service = Clean(DeviceTopologyWmiUtilities.ReadString(row, "Service"));
        var native = FindNativeDevice(nativeDevices, deviceId);
        var effectiveService = service ?? native?.Service;
        var advancedInterconnect = DeviceTopologyInterconnectClassifier.Classify(service)
            ?? DeviceTopologyInterconnectClassifier.Classify(native?.Service);
        var hardwareIds = MergeStrings(
            DeviceTopologyWmiUtilities.ReadStringArray(row, "HardwareID"),
            native?.HardwareIds);
        var compatibleIds = MergeStrings(
            DeviceTopologyWmiUtilities.ReadStringArray(row, "CompatibleID"),
            native?.CompatibleIds);
        var searchText = BuildSearchText(
            name,
            description,
            pnpClass,
            manufacturer,
            effectiveService,
            deviceId,
            hardwareIds,
            compatibleIds,
            native?.FriendlyName,
            native?.DeviceDescription,
            native?.ClassName,
            native?.EnumeratorName,
            native?.LocationInfo,
            native?.LocationPaths);

        if (!ShouldShowDevice(searchText, pnpClass, deviceId, advancedInterconnect is not null))
        {
            return null;
        }

        var busKind = advancedInterconnect?.Kind == DeviceInterconnectKinds.UcsiConnectorManager
            ? DeviceBusKinds.System
            : advancedInterconnect is not null
                ? DeviceBusKinds.Usb4
                : ResolveBusKind(searchText, pnpClass, deviceId);
        var usbPort = FindUsbPort(native, usbPortsByDriverKey);
        networkAdapters.TryGetValue(NormalizeDeviceId(deviceId), out var networkAdapter);
        var connectorKind = advancedInterconnect is not null
            ? DeviceConnectorKinds.Generic
            : RefineUsbConnectorKind(
                ResolveConnectorKind(searchText, pnpClass, busKind),
                usbPort?.ConnectorProperties);
        var hardwareKind = ResolveHardwareKind(searchText, pnpClass, busKind);
        var protocol = advancedInterconnect?.Technology ?? ResolveProtocol(searchText, pnpClass, busKind);
        var inferredSpeed = advancedInterconnect is null ? ResolveSpeed(searchText, busKind) : UnknownSpeed;
        var speed = usbPort?.NegotiatedSpeed
            ?? (networkAdapter is null ? null : FormatNetworkLinkSpeed(networkAdapter))
            ?? inferredSpeed;
        var idResolution = usbPort is not null && usbPort.VendorId != 0
            ? deviceIdCatalog.ResolveUsb(usbPort.VendorId, usbPort.ProductId)
                ?? deviceIdCatalog.ResolveHardwareIds(hardwareIds)
            : deviceIdCatalog.ResolveHardwareIds(hardwareIds);
        var displayName = ResolvePreferredDeviceName(
            name,
            deviceId,
            usbPort?.Descriptor.ProductName,
            idResolution?.DeviceName);
        var effectiveManufacturer = manufacturer
            ?? usbPort?.Descriptor.ManufacturerName
            ?? idResolution?.VendorName;
        var topology = ResolveTopologyInfo(deviceId, displayName, usbTopology);
        var nativeParent = native?.ParentDeviceId is null ? null : FindNativeDevice(nativeDevices, native.ParentDeviceId);
        var nativePath = ResolveNativePath(deviceId, displayName, nativeDevices);
        var hasUsbPort = usbPort is not null;
        var usbConnection = CreateUsbConnection(usbPort);
        var hid = DeviceTopologySpecializedCapabilityProjector.CreateHid(
            usbPort,
            usbConnection?.Endpoints ?? []);
        var camera = DeviceTopologySpecializedCapabilityProjector.CreateCamera(usbPort);
        var smartDevice = DeviceTopologySpecializedCapabilityProjector.CreateSmartDevice(
            usbPort,
            displayName,
            description,
            pnpClass,
            effectiveService,
            effectiveManufacturer);
        var storage = WindowsStorageDeviceCapabilityReader.Resolve(
            storageDevices,
            deviceId,
            nativeDevices);

        return new DeviceTopologyPort(
            CreateStableId(deviceId),
            usbPort?.ConnectorProperties?.PortIsUserConnectable == true
                || networkAdapter?.ConnectorPresent == true,
            displayName,
            connectorKind,
            busKind,
            hardwareKind,
            protocol,
            speed,
            deviceId,
            pnpClass,
            effectiveManufacturer,
            effectiveService,
            Clean(DeviceTopologyWmiUtilities.ReadString(row, "Status")),
            networkAdapter is not null
                ? Note(BackendMessageCodes.DeviceTopology.ConfidenceNetAdapter)
                : advancedInterconnect is not null
                ? Note(BackendMessageCodes.DeviceTopology.ConfidencePnpServiceRole)
                : ResolveConfidence(speed, busKind, topology.HasUsbRelationship, native is not null, hasUsbPort),
            networkAdapter is not null
                ? Note(BackendMessageCodes.DeviceTopology.SourceNetAdapterSetupApiPnp)
                : advancedInterconnect is not null
                ? Note(BackendMessageCodes.DeviceTopology.SourcePnpServiceSetupApi)
                : ResolveSource(topology.HasUsbRelationship, native is not null, hasUsbPort),
            topology.UpstreamDeviceId,
            topology.UpstreamDisplayName,
            topology.HasUsbRelationship ? topology.TopologyPath : nativePath,
            native?.ParentDeviceId,
            nativeParent?.FriendlyName ?? nativeParent?.DeviceDescription,
            native?.LocationInfo,
            native?.LocationPaths ?? [],
            native?.ClassGuid,
            null,
            CreateNetworkConnection(networkAdapter),
            idResolution,
            advancedInterconnect,
            usbConnection,
            hardwareIds,
            compatibleIds,
            native?.DevNodeStatus,
            native?.ProblemCode,
            PhysicalMaximumSpeed: usbPort is null
                ? null
                : WindowsUsbHubIoctlReader.DescribeMaximumUsbSpeed([usbPort.Capability]),
            Hid: hid,
            Camera: camera,
            SmartDevice: smartDevice,
            Storage: storage);
    }

    private DeviceTopologyPort CreateUsbConnectorCandidate(DeviceTopologyUsbConnectorGroup group)
    {
        var port = group.Representative;
        var connectorKind = RefineUsbConnectorKind(DeviceConnectorKinds.Generic, port.ConnectorProperties);
        var connectorName = connectorKind == DeviceConnectorKinds.UsbC ? "USB-C" : "USB-A";
        var stableId = CreateStableId($"USB-CONNECTOR|{group.StableKey}");
        var idResolution = port.DeviceConnected
            ? deviceIdCatalog.ResolveUsb(port.VendorId, port.ProductId)
            : null;
        var displayName = port.DeviceConnected
            ? Clean(port.Descriptor.ProductName)
                ?? idResolution?.DeviceName
                ?? $"{connectorName} 已连接设备"
            : $"空闲 {connectorName} 接口";
        var usbConnection = CreateUsbConnection(port);
        var hid = DeviceTopologySpecializedCapabilityProjector.CreateHid(
            port,
            usbConnection?.Endpoints ?? []);
        var camera = DeviceTopologySpecializedCapabilityProjector.CreateCamera(port);
        var smartDevice = DeviceTopologySpecializedCapabilityProjector.CreateSmartDevice(
            port,
            displayName,
            modelName: null,
            pnpClass: "USB",
            service: null,
            manufacturer: port.Descriptor.ManufacturerName ?? idResolution?.VendorName);

        return new DeviceTopologyPort(
            stableId,
            true,
            displayName,
            connectorKind,
            DeviceBusKinds.Usb,
            $"{connectorName} 物理连接器",
            FormatUsbSupportedProtocols(port.Capability),
            port.NegotiatedSpeed,
            $"USBPORT\\{stableId.ToUpperInvariant()}",
            "USB",
            port.Descriptor.ManufacturerName ?? idResolution?.VendorName,
            null,
            port.ConnectionStatus,
            Note(BackendMessageCodes.DeviceTopology.ConfidenceUsbConnectorProperties),
            Note(BackendMessageCodes.DeviceTopology.SourceUsbHubIoctl),
            null,
            null,
            $"USB Hub > 端口 {port.PortNumber}",
            null,
            null,
            null,
            [],
            null,
            null,
            null,
            idResolution,
            null,
            usbConnection,
            [],
            [],
            PhysicalMaximumSpeed: WindowsUsbHubIoctlReader.DescribeMaximumUsbSpeed(
                group.Ports.Select(static member => member.Capability)),
            Hid: hid,
            Camera: camera,
            SmartDevice: smartDevice);
    }

    private static DeviceTopologyPort CreateDisplayPathCandidate(DeviceTopologyResolvedDisplayPath resolved)
    {
        var path = resolved.Path;
        var profile = resolved.Profile;
        var technology = WindowsDisplayPathTopologyReader.DescribeOutputTechnology(path.OutputTechnology);
        var connectorKind = profile?.ConnectorKind
            ?? WindowsDisplayPathTopologyReader.ResolveConnectorKind(path.OutputTechnology);
        var internalOutput = WindowsDisplayPathTopologyReader.IsInternalOutput(path.OutputTechnology);
        var resolution = WindowsDisplayPathTopologyReader.FormatResolution(path.Width, path.Height);
        var refreshRate = WindowsDisplayPathTopologyReader.FormatRefreshRate(
            path.RefreshRateNumerator,
            path.RefreshRateDenominator);
        var displayName = Clean(path.MonitorFriendlyName)
            ?? profile?.DisplayName
            ?? (internalOutput ? "内置显示面板" : $"{technology} 活动显示器");
        var normalizedDeviceId = WindowsDisplayPathTopologyReader.NormalizeMonitorDevicePath(path.MonitorDevicePath);
        var deviceId = normalizedDeviceId.Length > 0
            ? normalizedDeviceId
            : profile is not null
                ? $"OEMDISPLAY\\{profile.Id.ToUpperInvariant()}"
                : $"DISPLAYPATH\\{path.AdapterHighPart:X8}_{path.AdapterLowPart:X8}_{path.TargetId}";
        var speed = path.Active
            ? resolution is null ? refreshRate : $"{resolution} @ {refreshRate}"
            : "未连接";
        var connectorTechnology = profile?.Protocol ?? technology;
        var source = Note(profile is null
            ? BackendMessageCodes.DeviceTopology.SourceQueryDisplayConfig
            : BackendMessageCodes.DeviceTopology.SourceOemProfileQueryDisplayConfig);
        var confidence = Note(profile is null
            ? BackendMessageCodes.DeviceTopology.ConfidenceActiveDisplayPath
            : BackendMessageCodes.DeviceTopology.ConfidenceOemProfileDisplayTarget);
        var advancedColor = path.AdvancedColor;
        var edid = path.Edid;

        return new DeviceTopologyPort(
            profile is null
                ? CreateStableId($"DISPLAY-PATH|{path.AdapterHighPart:X8}|{path.AdapterLowPart:X8}|{path.TargetId}")
                : CreateStableId($"OEM-DISPLAY-CONNECTOR|{profile.Id}"),
            WindowsDisplayPathTopologyReader.IsUserConnectableOutput(path.OutputTechnology),
            displayName,
            connectorKind,
            DeviceBusKinds.Display,
            profile?.HardwareKind ?? (internalOutput ? "内置显示面板" : $"{technology} 活动显示路径"),
            connectorTechnology,
            speed,
            deviceId,
            "Monitor",
            null,
            null,
            path.Active ? "活动" : "未连接",
            confidence,
            source,
            null,
            null,
            profile is null
                ? $"Windows 显示路径 > {technology} > {displayName}"
                : $"机型接口档案 > {profile.DisplayName}",
            null,
            null,
            null,
            [],
            null,
            new DeviceTopologyDisplayConnection(
                connectorTechnology,
                displayName,
                resolution,
                refreshRate,
                path.Active,
                path.TargetAvailable,
                internalOutput,
                path.ConnectorInstance,
                Clean(path.MonitorDevicePath),
                advancedColor?.BitsPerColorChannel ?? path.Dxgi?.BitsPerColorChannel ?? edid?.BitsPerColorChannel,
                advancedColor?.ColorEncoding,
                advancedColor?.AdvancedColorSupported,
                advancedColor?.AdvancedColorEnabled,
                advancedColor?.WideColorEnforced,
                path.SdrWhiteLevelNits,
                edid?.HdrFormats,
                edid?.DisplayTechnology,
                edid?.PanelTechnology,
                edid?.Version,
                edid?.ProductName,
                edid?.SerialNumber,
                edid?.WidthMillimeters is > 0 && edid.HeightMillimeters is > 0
                    ? $"{edid.WidthMillimeters} x {edid.HeightMillimeters} mm"
                    : null,
                MinimumLuminanceNits: path.Dxgi?.MinimumLuminanceNits,
                MaximumLuminanceNits: path.Dxgi?.MaximumLuminanceNits,
                MaximumFullFrameLuminanceNits: path.Dxgi?.MaximumFullFrameLuminanceNits,
                ColorCapabilitySource: BuildDisplayColorCapabilitySource(advancedColor, edid, path.Dxgi),
                ColorSpace: path.Dxgi?.ColorSpace),
            null,
            null,
            null,
            null,
            [],
            [],
            PhysicalMaximumSpeed: profile?.PhysicalMaximumSpeed);
    }

    private static string? BuildDisplayColorCapabilitySource(
        DeviceTopologyDisplayAdvancedColor? advancedColor,
        DeviceTopologyEdidCapabilities? edid,
        DeviceTopologyDxgiDisplayCapabilities? dxgi)
    {
        var sources = new List<string>(3);
        if (advancedColor is not null) sources.Add("DisplayConfigGetDeviceInfo");
        if (dxgi is not null) sources.Add("IDXGIOutput6::GetDesc1");
        if (edid is not null) sources.Add("EDID");
        return sources.Count == 0 ? null : string.Join(" + ", sources);
    }

    private static DeviceTopologyNetworkConnection? CreateNetworkConnection(DeviceTopologyNetworkAdapter? adapter)
    {
        if (adapter is null)
        {
            return null;
        }

        return new DeviceTopologyNetworkConnection(
            adapter.InterfaceName,
            DescribeNetworkConnectionState(adapter.MediaConnectState),
            FormatBitsPerSecond(adapter.TransmitLinkSpeedBitsPerSecond),
            FormatBitsPerSecond(adapter.ReceiveLinkSpeedBitsPerSecond),
            adapter.PermanentAddress,
            adapter.ActiveMtuBytes,
            adapter.HardwareInterface,
            adapter.ConnectorPresent);
    }

    internal static string FormatNetworkLinkSpeed(DeviceTopologyNetworkAdapter adapter)
    {
        var transmit = adapter.TransmitLinkSpeedBitsPerSecond ?? 0;
        var receive = adapter.ReceiveLinkSpeedBitsPerSecond ?? 0;
        if (transmit == 0 && receive == 0)
        {
            return adapter.MediaConnectState == 2 ? "未连接" : "未报告";
        }

        if (transmit == receive || transmit == 0 || receive == 0)
        {
            return FormatBitsPerSecond(Math.Max(transmit, receive)) ?? "未报告";
        }

        return $"接收 {FormatBitsPerSecond(receive)} / 发送 {FormatBitsPerSecond(transmit)}";
    }

    internal static string DescribeNetworkConnectionState(uint? state)
    {
        return state switch
        {
            1 => "已连接",
            2 => "未连接",
            0 => "未知",
            null => "未报告",
            _ => $"状态 {state}"
        };
    }

    internal static string? FormatBitsPerSecond(ulong? bitsPerSecond)
    {
        if (bitsPerSecond is null || bitsPerSecond == 0)
        {
            return null;
        }

        var value = bitsPerSecond.Value;
        if (value >= 1_000_000_000)
        {
            return $"{(value / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture)} Gbps";
        }

        if (value >= 1_000_000)
        {
            return $"{(value / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture)} Mbps";
        }

        if (value >= 1_000)
        {
            return $"{(value / 1_000d).ToString("0.##", CultureInfo.InvariantCulture)} Kbps";
        }

        return $"{value} bps";
    }

    private static DeviceTopologyUsbConnection? CreateUsbConnection(DeviceTopologyUsbPort? port)
    {
        if (port is null)
        {
            return null;
        }

        return new DeviceTopologyUsbConnection(
            port.HubDevicePath,
            port.PortNumber,
            port.DeviceConnected,
            port.ConnectionStatus,
            port.NegotiatedSpeed,
            port.DeviceAddress,
            FormatUsbId(port.VendorId),
            FormatUsbId(port.ProductId),
            port.DeviceIsHub,
            FormatUsbSupportedProtocols(port.Capability),
            port.Capability?.OperatingAtSuperSpeedOrHigher,
            port.Capability?.SuperSpeedCapableOrHigher,
            port.Capability?.OperatingAtSuperSpeedPlusOrHigher,
            port.Capability?.SuperSpeedPlusCapableOrHigher,
            port.ConnectorProperties?.PortIsUserConnectable,
            port.ConnectorProperties?.PortIsDebugCapable,
            port.ConnectorProperties?.PortHasMultipleCompanions,
            port.ConnectorProperties?.PortConnectorIsTypeC,
            port.ConnectorProperties?.CompanionPorts
                .Select(static companion => new DeviceTopologyUsbCompanionPort(
                    companion.CompanionIndex,
                    companion.PortNumber,
                    companion.HubSymbolicLinkName))
                .ToArray()
                ?? [],
            port.Descriptor.DeviceSpecification,
            port.Descriptor.DeviceRevision,
            port.Descriptor.DeviceClass,
            port.Descriptor.ManufacturerName,
            port.Descriptor.ProductName,
            port.Descriptor.SerialNumber,
            port.Descriptor.InterfaceProtocols,
            port.DownstreamHubDevicePath,
            DeviceTopologySpecializedCapabilityProjector.CreateUsbEndpoints(port));
    }

    internal static string RefineUsbConnectorKind(
        string inferredConnectorKind,
        DeviceTopologyUsbConnectorProperties? properties)
    {
        if (properties?.PortConnectorIsTypeC == true
            && inferredConnectorKind is DeviceConnectorKinds.UsbA or DeviceConnectorKinds.Generic)
        {
            return DeviceConnectorKinds.UsbC;
        }

        if (properties?.PortIsUserConnectable == true
            && inferredConnectorKind == DeviceConnectorKinds.Generic)
        {
            return DeviceConnectorKinds.UsbA;
        }

        if (properties?.PortIsUserConnectable == false
            && inferredConnectorKind == DeviceConnectorKinds.UsbA)
        {
            return DeviceConnectorKinds.Generic;
        }

        return inferredConnectorKind;
    }

    private static string FormatUsbSupportedProtocols(DeviceTopologyUsbPortCapability? capability)
    {
        if (capability is null)
        {
            return "未知";
        }

        var protocols = new List<string>(3);
        if (capability.SupportsUsb11)
        {
            protocols.Add("USB 1.1");
        }

        if (capability.SupportsUsb20)
        {
            protocols.Add("USB 2.0");
        }

        if (capability.SupportsUsb30)
        {
            protocols.Add("USB 3.x");
        }

        return protocols.Count == 0 ? "未报告" : string.Join(" / ", protocols);
    }

    private static DeviceTopologyNativeDevice? FindNativeDevice(
        IReadOnlyDictionary<string, DeviceTopologyNativeDevice> nativeDevices,
        string deviceId)
    {
        return nativeDevices.TryGetValue(NormalizeDeviceId(deviceId), out var device) ? device : null;
    }

    private static DeviceTopologyUsbPort? FindUsbPort(
        DeviceTopologyNativeDevice? native,
        IReadOnlyDictionary<string, DeviceTopologyUsbPort> usbPortsByDriverKey)
    {
        var driverKey = WindowsUsbHubIoctlReader.NormalizeDriverKey(native?.DriverKey);
        return driverKey.Length > 0 && usbPortsByDriverKey.TryGetValue(driverKey, out var port)
            ? port
            : null;
    }

    private static DeviceTopologyRelationshipInfo ResolveTopologyInfo(
        string deviceId,
        string displayName,
        UsbTopologyIndex usbTopology)
    {
        var key = NormalizeDeviceId(deviceId);
        if (key.Length == 0 || usbTopology.UpstreamByDeviceId.Count == 0)
        {
            return new DeviceTopologyRelationshipInfo(null, null, "PnP 设备枚举", false);
        }

        usbTopology.UpstreamByDeviceId.TryGetValue(key, out var upstreamKey);
        var upstreamDevice = upstreamKey is null ? null : FindUsbDevice(usbTopology.Devices, upstreamKey);
        var chain = BuildUsbTopologyPath(key, displayName, usbTopology);

        return new DeviceTopologyRelationshipInfo(
            upstreamKey,
            upstreamDevice?.DisplayName,
            chain.Count > 1 ? string.Join(" -> ", chain) : "PnP 设备枚举",
            upstreamKey is not null);
    }

    private static IReadOnlyList<string> BuildUsbTopologyPath(
        string deviceKey,
        string displayName,
        UsbTopologyIndex usbTopology)
    {
        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cursor = deviceKey;
        for (var depth = 0; depth < 6 && cursor.Length > 0 && seen.Add(cursor); depth++)
        {
            var device = FindUsbDevice(usbTopology.Devices, cursor);
            chain.Add(device?.DisplayName ?? (depth == 0 ? displayName : cursor));
            if (!usbTopology.UpstreamByDeviceId.TryGetValue(cursor, out var upstream))
            {
                break;
            }

            cursor = upstream;
        }

        chain.Reverse();
        return chain;
    }

    private static UsbTopologyDevice? FindUsbDevice(
        IReadOnlyDictionary<string, UsbTopologyDevice> devices,
        string deviceId)
    {
        return devices.TryGetValue(NormalizeDeviceId(deviceId), out var device) ? device : null;
    }

    private static string ResolveNativePath(
        string deviceId,
        string displayName,
        IReadOnlyDictionary<string, DeviceTopologyNativeDevice> nativeDevices)
    {
        var key = NormalizeDeviceId(deviceId);
        if (key.Length == 0 || nativeDevices.Count == 0)
        {
            return "PnP 设备枚举";
        }

        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cursor = key;
        for (var depth = 0; depth < 8 && cursor.Length > 0 && seen.Add(cursor); depth++)
        {
            if (!nativeDevices.TryGetValue(cursor, out var device))
            {
                chain.Add(depth == 0 ? displayName : cursor);
                break;
            }

            chain.Add(device.FriendlyName ?? device.DeviceDescription ?? cursor);
            cursor = device.ParentDeviceId ?? string.Empty;
        }

        chain.Reverse();
        return chain.Count > 1 ? string.Join(" -> ", chain) : "设备管理器属性";
    }

    private static string ExtractWmiDeviceReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return string.Empty;
        }

        var marker = "DeviceID=\"";
        var start = reference.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            marker = "PNPDeviceID=\"";
            start = reference.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        }

        if (start < 0)
        {
            return string.Empty;
        }

        start += marker.Length;
        var end = reference.IndexOf('"', start);
        if (end <= start)
        {
            return string.Empty;
        }

        return NormalizeDeviceId(reference[start..end]
            .Replace(@"\\", @"\")
            .Replace("\\\"", "\"", StringComparison.Ordinal));
    }

    private static bool ShouldShowDevice(
        string searchText,
        string? pnpClass,
        string deviceId,
        bool hasAdvancedInterconnectRole)
    {
        if (hasAdvancedInterconnectRole)
        {
            return true;
        }

        if (ContainsAny(searchText, "ACPI\\", "ROOT\\", "SWD\\", "HTREE\\", "DISPLAY\\DEFAULT_MONITOR"))
        {
            return false;
        }

        if (deviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
            || deviceId.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase)
            || deviceId.StartsWith("BTH\\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (deviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
        {
            return IsDisplayClass(pnpClass)
                || IsNetworkClass(pnpClass)
                || IsAudioClass(pnpClass)
                || IsStorageClass(pnpClass)
                || ContainsAny(searchText, "USB Controller", "xHCI", "eXtensible Host Controller", "SD Host", "Card Reader");
        }

        return pnpClass is not null
            && ContainsAny(
                pnpClass,
                "USB",
                "USBDevice",
                "Bluetooth",
                "Net",
                "MEDIA",
                "Display",
                "Monitor",
                "Ports",
                "DiskDrive",
                "SCSIAdapter",
                "SDHost",
                "Image",
                "Camera",
                "Keyboard",
                "Mouse");
    }

    private static string ResolveConnectorKind(string searchText, string? pnpClass, string busKind)
    {
        if (busKind is DeviceBusKinds.Usb4 or DeviceBusKinds.Thunderbolt
            || ContainsAny(searchText, "USB Type-C", "USB-C", "Type C", "UCSI", "Billboard"))
        {
            return DeviceConnectorKinds.Generic;
        }

        if (ContainsAny(searchText, "HDMI"))
        {
            return DeviceConnectorKinds.Hdmi;
        }

        if (ContainsAny(searchText, "DisplayPort", "DP Alt Mode"))
        {
            return DeviceConnectorKinds.DisplayPort;
        }

        if (busKind == DeviceBusKinds.Usb)
        {
            return DeviceConnectorKinds.Generic;
        }

        if (busKind == DeviceBusKinds.Bluetooth)
        {
            return DeviceConnectorKinds.Bluetooth;
        }

        if (busKind == DeviceBusKinds.Network)
        {
            return ContainsAny(searchText, "Wi-Fi", "Wireless", "WLAN", "Bluetooth")
                ? DeviceConnectorKinds.Generic
                : DeviceConnectorKinds.Rj45;
        }

        if (busKind == DeviceBusKinds.Audio)
        {
            return DeviceConnectorKinds.Audio;
        }

        if (ContainsAny(searchText, "SD Host", "SD Card", "Card Reader", "Realtek PCIE CardReader"))
        {
            return DeviceConnectorKinds.SdCard;
        }

        if (busKind == DeviceBusKinds.Pci)
        {
            return DeviceConnectorKinds.Pcie;
        }

        return DeviceConnectorKinds.Generic;
    }

    private static string ResolveBusKind(string searchText, string? pnpClass, string deviceId)
    {
        if (ContainsAny(searchText, "Thunderbolt"))
        {
            return DeviceBusKinds.Thunderbolt;
        }

        if (ContainsAny(searchText, "USB4"))
        {
            return DeviceBusKinds.Usb4;
        }

        if (deviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
            || deviceId.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase)
            || ContainsAny(searchText, "USB", "xHCI", "eXtensible Host Controller"))
        {
            return DeviceBusKinds.Usb;
        }

        if (deviceId.StartsWith("BTH\\", StringComparison.OrdinalIgnoreCase)
            || ContainsAny(searchText, "Bluetooth"))
        {
            return DeviceBusKinds.Bluetooth;
        }

        if (pnpClass is not null && pnpClass.Equals("Net", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceBusKinds.Network;
        }

        if (pnpClass is not null && pnpClass.Equals("MEDIA", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceBusKinds.Audio;
        }

        if (pnpClass is not null && (pnpClass.Equals("Display", StringComparison.OrdinalIgnoreCase)
            || pnpClass.Equals("Monitor", StringComparison.OrdinalIgnoreCase)))
        {
            return DeviceBusKinds.Display;
        }

        if (pnpClass is not null && (pnpClass.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase)
            || pnpClass.Equals("SCSIAdapter", StringComparison.OrdinalIgnoreCase)))
        {
            return DeviceBusKinds.Storage;
        }

        if (deviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceBusKinds.Pci;
        }

        return DeviceBusKinds.Unknown;
    }

    private static bool IsDisplayClass(string? pnpClass)
    {
        return pnpClass is not null
            && (pnpClass.Equals("Display", StringComparison.OrdinalIgnoreCase)
                || pnpClass.Equals("Monitor", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNetworkClass(string? pnpClass)
    {
        return pnpClass is not null && pnpClass.Equals("Net", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAudioClass(string? pnpClass)
    {
        return pnpClass is not null && pnpClass.Equals("MEDIA", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStorageClass(string? pnpClass)
    {
        return pnpClass is not null
            && (pnpClass.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase)
                || pnpClass.Equals("SCSIAdapter", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveHardwareKind(string searchText, string? pnpClass, string busKind)
    {
        if (ContainsAny(searchText, "Host Controller", "xHCI", "eXtensible Host Controller"))
        {
            return "USB Host Controller";
        }

        if (ContainsAny(searchText, "Root Hub"))
        {
            return "USB Root Hub";
        }

        if (ContainsAny(searchText, "Generic USB Hub", "Hub"))
        {
            return "USB Hub";
        }

        if (ContainsAny(searchText, "Composite Device"))
        {
            return "USB Composite Device";
        }

        return busKind switch
        {
            DeviceBusKinds.Usb => "USB Device",
            DeviceBusKinds.Usb4 => "USB4 Device",
            DeviceBusKinds.Thunderbolt => "Thunderbolt Device",
            DeviceBusKinds.Pci => "PCI Express Device",
            DeviceBusKinds.Network => "Network Adapter",
            DeviceBusKinds.Audio => "Audio Device",
            DeviceBusKinds.Display => "Display Device",
            DeviceBusKinds.Storage => "Storage Device",
            DeviceBusKinds.Bluetooth => "Bluetooth Device",
            _ => string.IsNullOrWhiteSpace(pnpClass) ? "Device" : pnpClass!
        };
    }

    private static string ResolveProtocol(string searchText, string? pnpClass, string busKind)
    {
        if (ContainsAny(searchText, "USBSTOR", "Mass Storage"))
        {
            return "USB Mass Storage";
        }

        if (ContainsAny(searchText, "HID", "Keyboard", "Mouse", "Input Device"))
        {
            return "HID";
        }

        if (ContainsAny(searchText, "Camera", "Webcam", "UVC"))
        {
            return "UVC / Camera";
        }

        if (ContainsAny(searchText, "Audio", "UAC", "Headset", "Microphone", "Speaker"))
        {
            return "Audio";
        }

        if (busKind == DeviceBusKinds.Bluetooth)
        {
            return "Bluetooth";
        }

        if (busKind == DeviceBusKinds.Network)
        {
            return ContainsAny(searchText, "Wi-Fi", "Wireless", "WLAN") ? "Wi-Fi" : "Ethernet";
        }

        if (busKind == DeviceBusKinds.Display)
        {
            return ContainsAny(searchText, "HDMI") ? "HDMI" : "Display";
        }

        if (ContainsAny(searchText, "Hub"))
        {
            return "Hub";
        }

        return string.IsNullOrWhiteSpace(pnpClass) ? "Unknown" : pnpClass!;
    }

    internal static string ResolveSpeed(string searchText, string busKind)
    {
        if (busKind is not (DeviceBusKinds.Usb or DeviceBusKinds.Usb4 or DeviceBusKinds.Thunderbolt))
        {
            return "不适用";
        }

        var reportedRates = ExplicitUsbGigabitRate.Matches(searchText)
            .Select(static match => int.TryParse(match.Groups["rate"].Value, out var rate) ? rate : 0)
            .Where(static rate => rate > 0)
            .ToArray();
        if (reportedRates.Length > 0)
        {
            return $"{reportedRates.Max()}Gbps";
        }

        if (ExplicitUsb480MegabitRate.IsMatch(searchText))
        {
            return "480Mbps";
        }

        return UnknownSpeed;
    }

    private static BackendMessage ResolveConfidence(
        string speed,
        string busKind,
        bool hasUsbRelationship,
        bool hasNativeDeviceProperties,
        bool hasUsbPortIoctl)
    {
        if (hasUsbPortIoctl)
        {
            return Note(BackendMessageCodes.DeviceTopology.ConfidenceUsbHubIoctl);
        }

        // 速度不是「未知」说明它是从名字里推出来的，可信度要单独标出来。
        var inferredFromName = !speed.Equals(UnknownSpeed, StringComparison.OrdinalIgnoreCase);
        if (hasUsbRelationship)
        {
            if (hasNativeDeviceProperties)
            {
                return Note(inferredFromName
                    ? BackendMessageCodes.DeviceTopology.ConfidenceWmiChainDeviceManagerNameInference
                    : BackendMessageCodes.DeviceTopology.ConfidenceWmiChainDeviceManager);
            }

            return Note(inferredFromName
                ? BackendMessageCodes.DeviceTopology.ConfidenceWmiChainNameInference
                : BackendMessageCodes.DeviceTopology.ConfidenceWmiChain);
        }

        if (hasNativeDeviceProperties)
        {
            return Note(inferredFromName
                ? BackendMessageCodes.DeviceTopology.ConfidenceDeviceManagerNameInference
                : BackendMessageCodes.DeviceTopology.ConfidenceDeviceManager);
        }

        if (busKind is DeviceBusKinds.Usb or DeviceBusKinds.Usb4 or DeviceBusKinds.Thunderbolt)
        {
            return Note(inferredFromName
                ? BackendMessageCodes.DeviceTopology.ConfidenceNameInference
                : BackendMessageCodes.DeviceTopology.ConfidenceDeviceEnumeration);
        }

        return Note(BackendMessageCodes.DeviceTopology.ConfidenceDeviceEnumeration);
    }

    private static BackendMessage ResolveSource(
        bool hasUsbRelationship,
        bool hasNativeDeviceProperties,
        bool hasUsbPortIoctl)
    {
        if (hasUsbPortIoctl)
        {
            return Note(hasUsbRelationship
                ? BackendMessageCodes.DeviceTopology.SourceUsbIoctlWmiSetupApiPnp
                : BackendMessageCodes.DeviceTopology.SourceUsbIoctlSetupApiPnp);
        }

        return Note((hasUsbRelationship, hasNativeDeviceProperties) switch
        {
            (true, true) => BackendMessageCodes.DeviceTopology.SourceWmiSetupApiPnp,
            (true, false) => BackendMessageCodes.DeviceTopology.SourceWmiPnp,
            (false, true) => BackendMessageCodes.DeviceTopology.SourceSetupApiPnp,
            _ => BackendMessageCodes.DeviceTopology.SourcePnpEnumeration
        });
    }

    private static string ResolveLogoText(string manufacturer, string model, string? family, string? sku)
    {
        var text = BuildSearchText(manufacturer, model, family, sku);
        if (ContainsAny(text, "LEGION", "拯救者"))
        {
            return "LEGION";
        }

        if (ContainsAny(text, "ROG", "Republic of Gamers"))
        {
            return "ROG";
        }

        if (ContainsAny(text, "ThinkPad"))
        {
            return "ThinkPad";
        }

        if (ContainsAny(text, "MECHREVO", "机械革命"))
        {
            return "MECHREVO";
        }

        if (ContainsAny(text, "LENOVO"))
        {
            return "LENOVO";
        }

        if (ContainsAny(text, "ASUSTeK", "ASUS"))
        {
            return "ASUS";
        }

        if (ContainsAny(text, "HP", "Hewlett"))
        {
            return "HP";
        }

        if (ContainsAny(text, "Dell"))
        {
            return "DELL";
        }

        return manufacturer.Length <= 12 ? manufacturer.ToUpperInvariant() : "PC";
    }

    private static string ResolveBrandDisplayName(string manufacturer, string model, string? family, string logo)
    {
        if (!string.IsNullOrWhiteSpace(family)
            && !model.Contains(family, StringComparison.OrdinalIgnoreCase))
        {
            return $"{NormalizeManufacturer(manufacturer)} {family} / {model}";
        }

        if (!logo.Equals("PC", StringComparison.OrdinalIgnoreCase)
            && !model.Contains(logo, StringComparison.OrdinalIgnoreCase))
        {
            return $"{NormalizeManufacturer(manufacturer)} {logo} / {model}";
        }

        return $"{NormalizeManufacturer(manufacturer)} / {model}";
    }

    private static string NormalizeManufacturer(string manufacturer)
    {
        return manufacturer switch
        {
            var value when value.Contains("LENOVO", StringComparison.OrdinalIgnoreCase) => "Lenovo",
            var value when value.Contains("ASUSTeK", StringComparison.OrdinalIgnoreCase) => "ASUS",
            var value when value.Contains("Hewlett", StringComparison.OrdinalIgnoreCase) => "HP",
            _ => manufacturer
        };
    }

    private static string BuildSearchText(params object?[] values)
    {
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            switch (value)
            {
                case null:
                    continue;
                case IEnumerable<string> items:
                    foreach (var item in items)
                    {
                        builder.Append(' ').Append(item);
                    }
                    break;
                default:
                    builder.Append(' ').Append(value);
                    break;
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> MergeStrings(
        IReadOnlyList<string> first,
        IReadOnlyList<string>? second)
    {
        return first
            .Concat(second ?? [])
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? FormatUsbId(ushort? value)
    {
        return value is null || value.Value == 0 ? null : $"0x{value.Value:X4}";
    }

    private static string CreateStableId(string deviceId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(deviceId.ToUpperInvariant()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    internal static string ResolvePreferredDeviceName(
        string currentName,
        string deviceId,
        string? descriptorProductName,
        string? catalogDeviceName)
    {
        if (!IsGenericDeviceName(currentName, deviceId))
        {
            return currentName;
        }

        return Clean(descriptorProductName)
            ?? Clean(catalogDeviceName)
            ?? currentName;
    }

    private static bool IsGenericDeviceName(string name, string deviceId)
    {
        return name.Equals(deviceId, StringComparison.OrdinalIgnoreCase)
            || ContainsAny(
                name,
                "USB Composite Device",
                "USB Input Device",
                "Unknown USB Device",
                "Generic USB",
                "PCI Device",
                "Base System Device",
                "USB 复合设备",
                "USB 输入设备",
                "未知 USB 设备",
                "PCI 设备",
                "基本系统设备");
    }

    private static string NormalizeDeviceId(string? deviceId)
    {
        return string.IsNullOrWhiteSpace(deviceId)
            ? string.Empty
            : deviceId.Trim().Replace(@"\\", @"\", StringComparison.Ordinal).ToUpperInvariant();
    }

    private static int GetConnectorSortKey(DeviceTopologyPort port)
    {
        return port.ConnectorKind switch
        {
            DeviceConnectorKinds.Thunderbolt => 0,
            DeviceConnectorKinds.UsbC => 1,
            DeviceConnectorKinds.UsbA => 2,
            DeviceConnectorKinds.Hdmi => 3,
            DeviceConnectorKinds.DisplayPort => 4,
            DeviceConnectorKinds.MiniDisplayPort => 5,
            DeviceConnectorKinds.Dvi => 6,
            DeviceConnectorKinds.Vga => 7,
            DeviceConnectorKinds.InternalDisplay => 8,
            DeviceConnectorKinds.WirelessDisplay => 9,
            DeviceConnectorKinds.Rj45 => 10,
            DeviceConnectorKinds.Audio => 11,
            DeviceConnectorKinds.SdCard => 12,
            DeviceConnectorKinds.Bluetooth => 13,
            DeviceConnectorKinds.Pcie => 14,
            _ => 20
        };
    }

    private static bool ContainsAny(string value, params string[] patterns)
    {
        return patterns.Any(pattern => value.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record UsbTopologyIndex(
        IReadOnlyDictionary<string, UsbTopologyDevice> Devices,
        IReadOnlyDictionary<string, string> UpstreamByDeviceId);

    private sealed record UsbTopologyDevice(
        string DeviceId,
        string DisplayName,
        string Kind,
        string? PnpClass);

    private sealed record DeviceTopologyRelationshipInfo(
        string? UpstreamDeviceId,
        string? UpstreamDisplayName,
        string TopologyPath,
        bool HasUsbRelationship);
}
