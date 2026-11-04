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
import { userFacingNetworkState } from "../deviceVocabulary.ts";
import { uiText } from "../../../text.ts";

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
    const busType = facts.transport;
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
    const connectionState = userFacingNetworkState(network?.connectionState);
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
      || port.transport.code === "bluetooth"),
  createModel: (context) => {
    const connection = connectionFacts(context);
    const facts = internalDeviceFacts(context)!;
    const title = deviceTitle(context, uiText.deviceAdapters.bluetoothDevice);
    const bluetoothRole = uiText.deviceAdapters.bluetoothDevice;
    const protocol = facts.transport;
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

function isController(port: import("../../../types").DeviceTopologyPort) {
  return port.advancedInterconnect != null
    || port.usb?.deviceIsHub === true
    || ["scsiadapter", "ucm"].includes(port.pnpClass?.toLocaleLowerCase() ?? "");
}

function resolveControllerType(port: import("../../../types").DeviceTopologyPort) {
  return displayValue(port.hardwareKind, port.pnpClass ?? uiText.deviceAdapters.internalController);
}

function resolveNetworkStandard(port: import("../../../types").DeviceTopologyPort) {
  const wireless = port.network?.wireless;
  if (wireless?.negotiatedStandard != null) return wireless.negotiatedStandard;
  if (wireless?.supportedStandards != null) return wireless.supportedStandards.join(" / ") || "[]";
  const rawCodes = wireless?.negotiatedPhyTypeRaw != null
    ? [wireless.negotiatedPhyTypeRaw] : wireless?.supportedPhyTypesRaw;
  if (rawCodes?.length) return `${uiText.deviceTopology.deviceCategory.unknown} (${rawCodes.join(" / ")})`;
  if (rawCodes != null) return "[]";
  return uiText.deviceTopology.deviceCategory.unknown;
}
