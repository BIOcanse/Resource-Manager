import {
  connectionFacts,
  deviceTitle,
  displayValue,
  internalDeviceFacts,
  joinSummary,
  summaryFields
} from "./adapterEvidence.ts";
import type {
  BluetoothDeviceModel,
  DeviceAdapter,
  GraphicsAdapterDeviceModel,
  InternalControllerDeviceModel,
  NetworkAdapterDeviceModel
} from "./types";
import { uiText } from "../../text.ts";

export const graphicsAdapter: DeviceAdapter<GraphicsAdapterDeviceModel> = {
  id: "graphics-adapter",
  matches: ({ scope, port }) => scope === "internal"
    && port.display == null
    && (port.pnpClass?.toLocaleLowerCase() === "display" || port.busKind.toLocaleLowerCase() === "display"),
  createModel: (context) => {
    const connection = connectionFacts(context);
    const facts = internalDeviceFacts(context)!;
    const title = deviceTitle(context, uiText.deviceAdapters.graphicsAdapter);
    const manufacturer = displayValue(context.port.manufacturer, context.port.idResolution?.vendorName ?? "--");
    const driverService = displayValue(context.port.service);
    const deviceStatus = displayValue(context.port.status);
    const busType = displayValue(context.port.busKind === "display" ? "PCI Express" : context.port.busKind);
    return {
      adapterId: "graphics-adapter",
      kind: "graphics-adapter",
      deviceTypeLabel: uiText.deviceAdapters.graphicsAdapter,
      title,
      subtitle: joinSummary(manufacturer === "--" ? undefined : manufacturer, facts.transport),
      badge: "GPU",
      iconKind: "graphics-card",
      ...connection,
      manufacturer,
      driverService,
      deviceStatus,
      busType,
      summaryFields: summaryFields(
        [uiText.deviceAdapters.label.manufacturer, manufacturer],
        [uiText.deviceAdapters.label.pciLocation, facts.location],
        [uiText.deviceAdapters.label.driverService, driverService],
        [uiText.deviceAdapters.label.deviceStatus, deviceStatus]
      ),
      capabilityLabels: [],
      searchTerms: ["GPU", uiText.deviceAdapters.graphicsAdapter, "graphics adapter", manufacturer, driverService, busType]
    };
  }
};

export const networkAdapter: DeviceAdapter<NetworkAdapterDeviceModel> = {
  id: "network-adapter",
  matches: ({ scope, port }) => scope === "internal"
    && (port.network != null || port.pnpClass?.toLocaleLowerCase() === "net"),
  createModel: (context) => {
    const connection = connectionFacts(context);
    const network = context.port.network;
    const title = deviceTitle(context, uiText.deviceAdapters.networkAdapter);
    const networkStandard = resolveNetworkStandard(context.port);
    const interfaceName = displayValue(network?.interfaceName);
    const connectionState = displayValue(network?.connectionState, context.port.status ?? "--");
    const transmitSpeed = displayValue(network?.transmitLinkSpeed);
    const receiveSpeed = displayValue(network?.receiveLinkSpeed);
    const activeMtu = typeof network?.activeMtuBytes === "number" ? `${network.activeMtuBytes} B` : "--";
    const permanentAddress = displayValue(network?.permanentAddress);
    return {
      adapterId: "network-adapter",
      kind: "network-adapter",
      deviceTypeLabel: uiText.deviceAdapters.networkAdapter,
      title,
      subtitle: joinSummary(networkStandard, connectionState, receiveSpeed === "--" ? undefined : receiveSpeed),
      badge: uiText.deviceAdapters.networkBadge,
      iconKind: "network-adapter",
      ...connection,
      interfaceName,
      connectionState,
      transmitSpeed,
      receiveSpeed,
      activeMtu,
      permanentAddress,
      summaryFields: summaryFields(
        [uiText.deviceAdapters.label.networkStandard, networkStandard],
        [uiText.deviceAdapters.label.connectionState, connectionState],
        [uiText.deviceAdapters.label.receiveLink, receiveSpeed],
        [uiText.deviceAdapters.label.transmitLink, transmitSpeed]
      ),
      capabilityLabels: [],
      searchTerms: ["网卡", uiText.deviceAdapters.networkAdapter, "network adapter", interfaceName, permanentAddress]
    };
  }
};

export const bluetoothAdapter: DeviceAdapter<BluetoothDeviceModel> = {
  id: "bluetooth-device",
  matches: ({ scope, port }) => scope === "internal"
    && (port.busKind.toLocaleLowerCase() === "bluetooth"
      || port.pnpClass?.toLocaleLowerCase() === "bluetooth"
      || /^BTH/i.test(port.deviceId)),
  createModel: (context) => {
    const connection = connectionFacts(context);
    const facts = internalDeviceFacts(context)!;
    const title = deviceTitle(context, uiText.deviceAdapters.bluetoothDevice);
    const bluetoothRole = resolveBluetoothRole(context.port.deviceId, context.port.service, context.port.displayName);
    const protocol = resolveBluetoothTransport(context.port.service, facts.transport);
    const driverService = displayValue(context.port.service);
    const deviceStatus = displayValue(context.port.status);
    return {
      adapterId: "bluetooth-device",
      kind: "bluetooth-device",
      deviceTypeLabel: bluetoothRole,
      title,
      subtitle: joinSummary(bluetoothRole, protocol),
      badge: "Bluetooth",
      iconKind: "bluetooth-device",
      ...connection,
      bluetoothRole,
      protocol,
      driverService,
      deviceStatus,
      summaryFields: summaryFields(
        [uiText.deviceAdapters.label.deviceRole, bluetoothRole],
        [uiText.deviceAdapters.label.internalTransport, protocol],
        [driverService === "--" ? uiText.deviceAdapters.label.manufacturer : uiText.deviceAdapters.label.driverService, driverService === "--" ? facts.manufacturer : driverService],
        [uiText.deviceAdapters.label.deviceStatus, deviceStatus]
      ),
      capabilityLabels: [],
      searchTerms: ["蓝牙", "Bluetooth", bluetoothRole, protocol, driverService]
    };
  }
};

export const internalControllerAdapter: DeviceAdapter<InternalControllerDeviceModel> = {
  id: "internal-controller",
  matches: ({ scope, port }) => scope === "internal" && isController(port),
  createModel: (context) => {
    const connection = connectionFacts(context);
    const facts = internalDeviceFacts(context)!;
    const title = deviceTitle(context, uiText.deviceAdapters.internalController);
    const controllerType = resolveControllerType(context.port);
    const interconnectTechnology = displayValue(
      context.port.advancedInterconnect?.technology,
      facts.transport
    );
    const driverService = displayValue(context.port.service);
    const deviceStatus = displayValue(context.port.status);
    return {
      adapterId: "internal-controller",
      kind: "internal-controller",
      deviceTypeLabel: uiText.deviceAdapters.internalController,
      title,
      subtitle: joinSummary(controllerType, interconnectTechnology),
      badge: uiText.deviceAdapters.controllerBadge,
      iconKind: "controller",
      ...connection,
      controllerType,
      interconnectTechnology,
      driverService,
      deviceStatus,
      downstreamDeviceCount: context.downstreamInterfaceCount,
      summaryFields: summaryFields(
        [uiText.deviceAdapters.label.controllerType, controllerType],
        [uiText.deviceAdapters.label.interconnectTechnology, interconnectTechnology],
        [uiText.deviceAdapters.label.directDevices, String(context.downstreamInterfaceCount)],
        [uiText.deviceAdapters.label.deviceStatus, deviceStatus]
      ),
      capabilityLabels: [],
      searchTerms: [uiText.deviceAdapters.controllerBadge, "controller", controllerType, interconnectTechnology, driverService]
    };
  }
};

function isController(port: import("../../types").DeviceTopologyPort) {
  const service = port.service?.toLocaleLowerCase() ?? "";
  return port.advancedInterconnect != null
    || port.usb?.deviceIsHub === true
    || ["scsiadapter", "ucm"].includes(port.pnpClass?.toLocaleLowerCase() ?? "")
    || /^(stornvme|storahci|usbxhci|usbhub3|ucmucsi|usb4)/.test(service)
    || /controller|root hub|控制器|根集线器|连接器管理器/i.test(`${port.displayName} ${port.hardwareKind}`);
}

function resolveBluetoothRole(deviceId: string, service?: string | null, displayName?: string | null) {
  const evidence = `${deviceId} ${service ?? ""} ${displayName ?? ""}`;
  if (/BTHUSB/i.test(service ?? "") || /adapter|适配器/i.test(displayName ?? "")) return uiText.deviceAdapters.bluetoothAdapter;
  if (/BTHMODEM/i.test(evidence)) return uiText.deviceAdapters.bluetoothSerialPort;
  if (/RFCOMM/i.test(evidence)) return "Bluetooth RFCOMM";
  if (/BTHLEDEVICE|UmPass/i.test(evidence)) return uiText.deviceAdapters.bluetoothLeGatt;
  if (/AVRCP/i.test(evidence)) return uiText.deviceAdapters.bluetoothAvrcp;
  if (/A2DP/i.test(evidence)) return uiText.deviceAdapters.bluetoothStereoAudio;
  if (/HFAUD/i.test(evidence)) return uiText.deviceAdapters.bluetoothHandsFreeAudio;
  if (/BTHENUM\\DEV_|BTHLE\\DEV_/i.test(deviceId)) return uiText.deviceAdapters.bluetoothEndpoint;
  if (/MS_BTHLE/i.test(deviceId) || /^BthLEEnum$/i.test(service ?? "")) return uiText.deviceAdapters.bluetoothLeEnumerator;
  if (/MS_BTHBRB/i.test(deviceId) || /^BthEnum$/i.test(service ?? "")) return uiText.deviceAdapters.bluetoothClassicEnumerator;
  return uiText.deviceAdapters.bluetoothDevice;
}

function resolveBluetoothTransport(service: string | null | undefined, transport: string) {
  return /BTHUSB/i.test(service ?? "") ? "USB / Bluetooth" : displayValue(transport, "Bluetooth");
}

function resolveControllerType(port: import("../../types").DeviceTopologyPort) {
  const service = port.service?.toLocaleLowerCase() ?? "";
  if (service === "stornvme") return uiText.deviceAdapters.nvmeController;
  if (service === "storahci") return uiText.deviceAdapters.sataAhciController;
  if (service === "usbxhci") return uiText.deviceAdapters.usbXhciController;
  if (service === "usbhub3" && /ROOT_HUB/i.test(port.deviceId)) return uiText.deviceAdapters.usbRootHub;
  if (service.startsWith("ucmucsi")) return uiText.deviceAdapters.usbCConnectorManager;
  if (service.startsWith("usb4")) return uiText.deviceAdapters.usb4Router;
  if (port.usb?.deviceIsHub) return "USB Hub";
  return displayValue(port.hardwareKind, port.pnpClass ?? uiText.deviceAdapters.internalController);
}

function resolveNetworkStandard(port: import("../../types").DeviceTopologyPort) {
  const evidence = `${port.displayName} ${port.protocol} ${port.idResolution?.deviceName ?? ""}`;
  if (/Wi-?Fi\s*7|802\.11be/i.test(evidence)) return "Wi-Fi 7";
  if (/Wi-?Fi\s*6E/i.test(evidence)) return "Wi-Fi 6E";
  if (/Wi-?Fi\s*6|802\.11ax/i.test(evidence)) return "Wi-Fi 6";
  if (/Wi-?Fi\s*5|802\.11ac/i.test(evidence)) return "Wi-Fi 5";
  if (/Wi-?Fi|Wireless|802\.11/i.test(evidence)) return "Wi-Fi";
  if (/Ethernet|以太网/i.test(evidence)) return "Ethernet";
  return displayValue(port.protocol, uiText.deviceAdapters.networkAdapter);
}
