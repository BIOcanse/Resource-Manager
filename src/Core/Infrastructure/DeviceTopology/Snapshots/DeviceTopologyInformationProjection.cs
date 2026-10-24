using System.Globalization;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

/// <summary>Explicit typed projection, performed only when the existing owner publishes.</summary>
public static partial class DeviceTopologyInformationProjection
{
    private static readonly JsonSerializerOptions JsonOptions = WindowsInformationFieldProjection.InformationJsonOptions;

    public static DeviceInformationSnapshot Create(DeviceTopologySnapshot? snapshot, DateTimeOffset? observedAt)
    {
        if (snapshot is null) return DeviceInformationSnapshot.Empty;
        var devices = new List<DeviceInformationObject>();
        var disks = (snapshot.DiskLayoutInformation ?? []).GroupBy(static disk => disk.DeviceIdentity, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1).Select(static group => group.Single()).ToArray();
        var disksByIdentity = disks.ToDictionary(static disk => disk.DeviceIdentity, StringComparer.Ordinal);
        var physicalDiskCandidates = WindowsInformationFieldProjection.PhysicalDiskRelationCandidates(snapshot.StorageInformation);
        var projectedDisks = new HashSet<string>(StringComparer.Ordinal);
        var uniquePortIdentities = snapshot.Ports.Where(static port => !string.IsNullOrWhiteSpace(port.DeviceId))
            .GroupBy(static port => "windows-pnp|" + port.DeviceId.Trim().ToUpperInvariant(), StringComparer.Ordinal)
            .Where(static group => group.Count() == 1).Select(static group => group.Key).ToHashSet(StringComparer.Ordinal);
        var system = snapshot.System;
        var security = snapshot.SecurityInformation ?? DeviceTopologySecurityInformation.Unavailable;
        Add("machine", "machine", [
            Text(F.MachineAllManufacturer, system.Manufacturer),
            Text(F.MachineAllModelDeclared, system.Model),
            .. WindowsInformationFieldProjection.SecurityFields(security),
            .. WindowsInformationFieldProjection.NetworkEndpointFields(snapshot.NetworkEndpointInformation)]);
        Add("bios", "bios", [
            Text(F.BiosAllVendor, system.BiosManufacturer),
            Text(F.BiosAllVersionDeclared, system.BiosVersion),
            Text(F.BiosAllReleaseDate, snapshot.FirmwareDeclarations?.BiosReleaseDate)]);
        Add("motherboard", "motherboard", [
            Text(F.MotherboardAllManufacturer, system.BaseBoardManufacturer),
            Text(F.MotherboardAllModelDeclared, system.BaseBoardProduct),
            Text(F.MotherboardAllSerialNumber, system.BaseBoardSerialNumber)]);

        foreach (var module in snapshot.MemoryModules)
        {
            if (string.IsNullOrWhiteSpace(module.DeviceIdentity)) continue;
            // SMBIOS handle is a local record identity, not a serial number or a cross-boot ID.
            Add(module.DeviceIdentity, "memory_dimm", WindowsInformationFieldProjection.MemoryModuleFields(module));
        }

        foreach (var slot in snapshot.FirmwareDeclarations?.Slots ?? [])
        {
            if (!string.IsNullOrWhiteSpace(slot.DeviceIdentity))
                Add(slot.DeviceIdentity, "motherboard_slot", WindowsInformationFieldProjection.SlotFields(slot));
        }

        foreach (var socket in snapshot.FirmwareDeclarations?.ProcessorSockets ?? [])
        {
            if (!string.IsNullOrWhiteSpace(socket.DeviceIdentity))
                Add(socket.DeviceIdentity, "cpu_socket", WindowsInformationFieldProjection.ProcessorSocketFields(socket));
        }

        foreach (var battery in snapshot.Batteries ?? [])
        {
            if (!string.IsNullOrWhiteSpace(battery.DeviceIdentity))
                Add(battery.DeviceIdentity, "battery_device", WindowsInformationFieldProjection.BatteryDeclarationFields(battery));
        }

        foreach (var storage in snapshot.StorageInformation ?? [])
        {
            var fields = WindowsInformationFieldProjection.StorageFields(storage);
            if (!string.IsNullOrWhiteSpace(storage.DeviceIdentity) && fields.Count > 0)
                Add(storage.DeviceIdentity, "physical_disk", fields);
        }

        foreach (var volume in snapshot.VolumeSpaceInformation ?? [])
        {
            if (!string.IsNullOrWhiteSpace(volume.DeviceIdentity))
                Add(volume.DeviceIdentity, "volume", WindowsInformationFieldProjection.VolumeSpaceFields(volume));
        }

        foreach (var file in snapshot.PageFileInformation ?? [])
        {
            if (!string.IsNullOrWhiteSpace(file.DeviceIdentity))
                Add(file.DeviceIdentity, "page_file", WindowsInformationFieldProjection.PageFileFields(file));
        }

        foreach (var port in snapshot.Ports)
        {
            var fields = new List<DeviceInformationField>();
            var pnpIdentity = "windows-pnp|" + port.DeviceId.Trim().ToUpperInvariant();
            var knownDisk = disksByIdentity.TryGetValue(pnpIdentity, out var diskLayout);
            var isDisk = knownDisk && uniquePortIdentities.Contains(pnpIdentity);
            if (isDisk)
            {
                fields.AddRange(WindowsInformationFieldProjection.DiskLayoutFields(diskLayout!, disks, snapshot.VolumeSpaceInformation, physicalDiskCandidates));
                projectedDisks.Add(pnpIdentity);
            }
            if (port.PciIdentification is not null || port.BusKind == DeviceBusKinds.Pci)
            {
                fields.AddRange(PciFields(port));
            }
            if (port.Network is { } network)
            {
                fields.AddRange([
                    Text(F.NetworkAllAdapterIdDeclared, port.DeviceId),
                    Text(F.NetworkAllInterfaceNameReported, network.InterfaceName),
                    Integer(F.NetworkAllLinkStateActual, network.MediaConnectState),
                    Integer(F.NetworkAllTransmitLinkSpeedActualBitsPerSecond, network.TransmitLinkSpeedBitsPerSecond),
                    Integer(F.NetworkAllReceiveLinkSpeedActualBitsPerSecond, network.ReceiveLinkSpeedBitsPerSecond),
                    Text(F.NetworkAllMacAddressDeclared, network.PermanentAddress),
                    Integer(F.NetworkAllMtuActual, network.ActiveMtuBytes)]);
            }
            if (port.Usb is { DeviceConnected: true } usb)
            {
                fields.AddRange(WindowsInformationFieldProjection.UsbFields(new(pnpIdentity, usb.ActualParentPortIdentity,
                    usb.ManufacturerName, usb.ProductName, usb.SerialNumber,
                    port.Hid?.InputPollingIntervalMicroseconds / 1000, port.Hid?.TheoreticalReportRateHz,
                    port.Camera?.NativeModes.Select(mode => new DeviceTopologyUsbCameraModeDescriptor(
                        mode.Width, mode.Height, mode.MaximumFrameRate, mode.PixelFormat)
                        { FrameIntervalsDeclared = mode.FrameIntervalsDeclared }).ToArray(),
                    HasHidCapabilities: port.Hid is not null,
                    NegotiatedLinkSpeedMegabitsPerSecond: usb.NegotiatedLinkSpeedMegabitsPerSecond,
                    DeclaredMaximumLinkSpeedMegabitsPerSecond: usb.DeclaredMaximumLinkSpeedMegabitsPerSecond)));
            }
            if (fields.Count > 0 && !string.IsNullOrWhiteSpace(port.DeviceId) && (!knownDisk || isDisk))
            {
                Add(pnpIdentity,
                    isDisk ? "os_disk" : port.Network is not null ? "network_adapter" : port.Usb is not null ? "usb_device" : "pci_device", fields);
            }
            if (port.Display is { MonitorDevicePath.Length: > 0 } display)
            {
                // Display path and attached monitor remain part of the caller-owned identity.
                var identity = "windows-display-path|" + port.Id + "|" + display.MonitorDevicePath.Trim().ToUpperInvariant();
                Add(identity, "display_path", WindowsInformationFieldProjection.DisplayFields(new(identity, display.Active, display.TargetAvailable,
                    display.EdidRawDeclaredBase64, display.ChromaticityDeclared, display.HdrLuminanceDeclared,
                    display.CurrentWidth, display.CurrentHeight, display.CurrentRefreshRateNumerator,
                    display.CurrentRefreshRateDenominator, display.ClockwiseRotationDegrees, display.HdrSupportedReported,
                    display.HdrUserEnabledActual, display.HdrActiveActual, display.ColorModeActual,
                    display.CurrentBitsPerColorChannel, display.AdvancedColorSupported, display.AdvancedColorEnabled,
                    display.SdrWhiteLevelNits, display.ColorEncoding, display.MinimumLuminanceNits,
                    display.MaximumLuminanceNits, display.MaximumFullFrameLuminanceNits)
                    { ModeCapabilityDeclared = display.ModeCapabilityDeclared }));
            }
        }

        foreach (var disk in disks)
            if (!projectedDisks.Contains(disk.DeviceIdentity))
                Add(disk.DeviceIdentity, "os_disk", WindowsInformationFieldProjection.DiskLayoutFields(disk, disks, snapshot.VolumeSpaceInformation, physicalDiskCandidates));

        WindowsInformationFieldProjection.AppendHidDeclarations(devices, snapshot.HidInformation, observedAt);
        WindowsInformationFieldProjection.AppendSecurityProfiles(devices, security, observedAt);
        devices.AddRange(WindowsInformationFieldProjection.NetworkInterfaces(snapshot.NetworkInterfaceInformation, observedAt).Devices);
        WindowsInformationFieldProjection.AppendWindowsGpuDeclarations(devices, snapshot.GpuInformation, observedAt);
        WindowsInformationFieldProjection.AppendGpuDeclarations(devices,snapshot.NvidiaGpuInformation,observedAt);
        WindowsInformationFieldProjection.AppendAmdGpuDeclarations(devices,snapshot.AmdGpuInformation,observedAt);
        devices.AddRange(WindowsInformationFieldProjection.Drivers(snapshot.DriverInformation, observedAt).Devices);
        devices.AddRange(WindowsInformationFieldProjection.Npus(snapshot.NpuInformation, observedAt).Devices);
        var unique = devices.GroupBy(static device => device.DeviceIdentity, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1).Select(static group => group.Single())
            .OrderBy(static device => device.DeviceIdentity, StringComparer.Ordinal).ToArray();
        return new(observedAt, Array.AsReadOnly(unique));

        void Add(string identity, string objectType, IReadOnlyCollection<DeviceInformationField> fields)
        {
            var values = fields.Select(field => observedAt is null ? field with { Value = null } : field).ToArray();
            devices.Add(new(identity, objectType, Array.AsReadOnly(values)));
        }
    }

    private static DeviceInformationField Text(DeviceInformationDefinition definition, string? value)
        => Value(definition, string.IsNullOrWhiteSpace(value) ? null : value.Trim());

    private static DeviceInformationField Integer(DeviceInformationDefinition definition, ulong? value)
        => Value(definition, value?.ToString(CultureInfo.InvariantCulture));

    private static DeviceInformationField Number(DeviceInformationDefinition definition, double? value)
        => Value<double?>(definition, value is { } number && double.IsFinite(number) ? number : null);

    private static DeviceInformationField Boolean(DeviceInformationDefinition definition, bool? value)
        => Value(definition, value);

    private static DeviceInformationField Value<T>(DeviceInformationDefinition definition, T value)
        => new(definition, value is null ? null : JsonSerializer.SerializeToElement(value, JsonOptions));
}

