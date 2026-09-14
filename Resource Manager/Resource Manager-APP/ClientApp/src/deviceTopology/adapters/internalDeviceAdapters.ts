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

export const graphicsAdapter: DeviceAdapter<GraphicsAdapterDeviceModel> = {
  id: "graphics-adapter",
  matches: ({ scope, port }) => scope === "internal"
    && port.display == null
    && (port.pnpClass?.toLocaleLowerCase() === "display" || port.busKind.toLocaleLowerCase() === "display"),
  createModel: (context) => {
    const connection = connectionFacts(context);
    const facts = internalDeviceFacts(context)!;
    const title = deviceTitle(context, "图形适配器");
    const manufacturer = displayValue(context.port.manufacturer, context.port.idResolution?.vendorName ?? "--");
    const driverService = displayValue(context.port.service);
    const deviceStatus = displayValue(context.port.status);
    const busType = displayValue(context.port.busKind === "display" ? "PCI Express" : context.port.busKind);
    return {
      adapterId: "graphics-adapter",
      kind: "graphics-adapter",
      deviceTypeLabel: "图形适配器",
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
        ["厂商", manufacturer],
        ["PCI 位置", facts.location],
        ["驱动服务", driverService],
        ["设备状态", deviceStatus]
      ),
      capabilityLabels: [],
      searchTerms: ["GPU", "图形适配器", "graphics adapter", manufacturer, driverService, busType]
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
    const title = deviceTitle(context, "网络适配器");
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
      deviceTypeLabel: "网络适配器",
      title,
      subtitle: joinSummary(networkStandard, connectionState, receiveSpeed === "--" ? undefined : receiveSpeed),
      badge: "网络",
      iconKind: "network-adapter",
      ...connection,
      interfaceName,
      connectionState,
      transmitSpeed,
      receiveSpeed,
      activeMtu,
      permanentAddress,
      summaryFields: summaryFields(
        ["网络制式", networkStandard],
        ["连接状态", connectionState],
        ["接收链路", receiveSpeed],
        ["发送链路", transmitSpeed]
      ),
      capabilityLabels: [],
      searchTerms: ["网卡", "网络适配器", "network adapter", interfaceName, permanentAddress]
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
    const title = deviceTitle(context, "Bluetooth 设备");
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
        ["设备角色", bluetoothRole],
        ["内部传输", protocol],
        [driverService === "--" ? "厂商" : "驱动服务", driverService === "--" ? facts.manufacturer : driverService],
        ["设备状态", deviceStatus]
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
    const title = deviceTitle(context, "内部控制器");
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
      deviceTypeLabel: "内部控制器",
      title,
      subtitle: joinSummary(controllerType, interconnectTechnology),
      badge: "控制器",
      iconKind: "controller",
      ...connection,
      controllerType,
      interconnectTechnology,
      driverService,
      deviceStatus,
      downstreamDeviceCount: context.downstreamInterfaceCount,
      summaryFields: summaryFields(
        ["控制器类型", controllerType],
        ["互连技术", interconnectTechnology],
        ["直属设备", String(context.downstreamInterfaceCount)],
        ["设备状态", deviceStatus]
      ),
      capabilityLabels: [],
      searchTerms: ["控制器", "controller", controllerType, interconnectTechnology, driverService]
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
  if (/BTHUSB/i.test(service ?? "") || /adapter|适配器/i.test(displayName ?? "")) return "Bluetooth 适配器";
  if (/BTHMODEM/i.test(evidence)) return "Bluetooth 串行端口";
  if (/RFCOMM/i.test(evidence)) return "Bluetooth RFCOMM";
  if (/BTHLEDEVICE|UmPass/i.test(evidence)) return "Bluetooth LE GATT 服务";
  if (/AVRCP/i.test(evidence)) return "Bluetooth AVRCP 传输";
  if (/A2DP/i.test(evidence)) return "Bluetooth 立体声音频";
  if (/HFAUD/i.test(evidence)) return "Bluetooth 免提音频";
  if (/BTHENUM\\DEV_|BTHLE\\DEV_/i.test(deviceId)) return "Bluetooth 终端设备";
  if (/MS_BTHLE/i.test(deviceId) || /^BthLEEnum$/i.test(service ?? "")) return "Bluetooth LE 枚举器";
  if (/MS_BTHBRB/i.test(deviceId) || /^BthEnum$/i.test(service ?? "")) return "Bluetooth 经典枚举器";
  return "Bluetooth 设备";
}

function resolveBluetoothTransport(service: string | null | undefined, transport: string) {
  return /BTHUSB/i.test(service ?? "") ? "USB / Bluetooth" : displayValue(transport, "Bluetooth");
}

function resolveControllerType(port: import("../../types").DeviceTopologyPort) {
  const service = port.service?.toLocaleLowerCase() ?? "";
  if (service === "stornvme") return "NVMe 控制器";
  if (service === "storahci") return "SATA AHCI 控制器";
  if (service === "usbxhci") return "USB xHCI 主控制器";
  if (service === "usbhub3" && /ROOT_HUB/i.test(port.deviceId)) return "USB 根集线器";
  if (service.startsWith("ucmucsi")) return "USB-C 连接器管理器";
  if (service.startsWith("usb4")) return "USB4 路由器";
  if (port.usb?.deviceIsHub) return "USB Hub";
  return displayValue(port.hardwareKind, port.pnpClass ?? "内部控制器");
}

function resolveNetworkStandard(port: import("../../types").DeviceTopologyPort) {
  const evidence = `${port.displayName} ${port.protocol} ${port.idResolution?.deviceName ?? ""}`;
  if (/Wi-?Fi\s*7|802\.11be/i.test(evidence)) return "Wi-Fi 7";
  if (/Wi-?Fi\s*6E/i.test(evidence)) return "Wi-Fi 6E";
  if (/Wi-?Fi\s*6|802\.11ax/i.test(evidence)) return "Wi-Fi 6";
  if (/Wi-?Fi\s*5|802\.11ac/i.test(evidence)) return "Wi-Fi 5";
  if (/Wi-?Fi|Wireless|802\.11/i.test(evidence)) return "Wi-Fi";
  if (/Ethernet|以太网/i.test(evidence)) return "Ethernet";
  return displayValue(port.protocol, "网络适配器");
}
