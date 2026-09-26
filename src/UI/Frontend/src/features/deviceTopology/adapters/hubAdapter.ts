import { portDisplayName } from "../deviceVocabulary.ts";
import {
  connectionFacts,
  deviceTitle,
  displayValue,
  summaryFields,
  usbSpecification
} from "./adapterEvidence.ts";
import type {
  DeviceAdapter,
  DeviceAdapterContext,
  DockDeviceModel,
  UsbHubDeviceModel
} from "./types";
import { uiText } from "../../../text.ts";

export const dockAdapter: DeviceAdapter<DockDeviceModel> = {
  id: "dock",
  matches: (context) => context.port.usb?.deviceIsHub === true
    && Boolean(context.port.usb.downstreamHubDevicePath)
    && /dock|docking|扩展坞/i.test(deviceTitle({
      scope: context.scope,
      port: context.port,
      fallbackTitle: portDisplayName(context.port),
      downstreamInterfaceCount: 0,
      connectedDownstreamInterfaceCount: 0
    })),
  createModel: (context) => createHubModel(context, "dock")
};

export const usbHubAdapter: DeviceAdapter<UsbHubDeviceModel> = {
  id: "usb-hub",
  matches: ({ port }) => port.usb?.deviceIsHub === true,
  createModel: (context) => createHubModel(context, "usb-hub")
};

function createHubModel(context: DeviceAdapterContext, kind: "dock"): DockDeviceModel;
function createHubModel(context: DeviceAdapterContext, kind: "usb-hub"): UsbHubDeviceModel;
function createHubModel(
  context: DeviceAdapterContext,
  kind: "dock" | "usb-hub"
): DockDeviceModel | UsbHubDeviceModel {
  const title = deviceTitle(context, kind === "dock" ? uiText.deviceAdapters.dock : "USB Hub");
  const connection = connectionFacts(context);
  const total = context.downstreamInterfaceCount;
  const connected = context.connectedDownstreamInterfaceCount;
  const idle = Math.max(0, total - connected);
  const typeLabel = kind === "dock" ? uiText.deviceAdapters.dock : "USB Hub";
  const specification = usbSpecification(context.port);
  return {
    adapterId: kind,
    kind,
    deviceTypeLabel: typeLabel,
    title,
    subtitle: total > 0 ? uiText.deviceAdapters.hubSummary(total, connected) : connection.currentLink,
    badge: kind === "dock" ? uiText.deviceAdapters.dockBadge : "USB Hub",
    iconKind: kind === "dock" ? "dock" : "usb-hub",
    ...connection,
    downstreamInterfaceCount: total,
    connectedDownstreamInterfaceCount: connected,
    idleDownstreamInterfaceCount: idle,
    usbSpecification: specification,
    summaryFields: summaryFields(
      [uiText.deviceAdapters.label.deviceType, typeLabel],
      [uiText.deviceAdapters.label.upstreamInterface, connection.upstreamInterface],
      [uiText.deviceAdapters.label.downstreamInterfaces, String(total)],
      [uiText.deviceAdapters.label.connectedDevices, String(connected)]
    ),
    capabilityLabels: [
      total > 0 ? uiText.deviceAdapters.hubDownstreamSummary(total) : undefined,
      specification !== "--" ? specification : undefined
    ].filter((value): value is string => Boolean(value)),
    searchTerms: [typeLabel, kind, `${total} ports`, displayValue(context.port.usb?.deviceClass)]
  } as DockDeviceModel | UsbHubDeviceModel;
}
