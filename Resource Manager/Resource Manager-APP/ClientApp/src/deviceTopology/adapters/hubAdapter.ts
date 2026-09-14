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

export const dockAdapter: DeviceAdapter<DockDeviceModel> = {
  id: "dock",
  matches: (context) => context.port.usb?.deviceIsHub === true
    && Boolean(context.port.usb.downstreamHubDevicePath)
    && /dock|docking|扩展坞/i.test(deviceTitle({
      scope: context.scope,
      port: context.port,
      fallbackTitle: context.port.displayName,
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
  const title = deviceTitle(context, kind === "dock" ? "接口扩展坞" : "USB Hub");
  const connection = connectionFacts(context);
  const total = context.downstreamInterfaceCount;
  const connected = context.connectedDownstreamInterfaceCount;
  const idle = Math.max(0, total - connected);
  const typeLabel = kind === "dock" ? "接口扩展坞" : "USB Hub";
  const specification = usbSpecification(context.port);
  return {
    adapterId: kind,
    kind,
    deviceTypeLabel: typeLabel,
    title,
    subtitle: total > 0 ? `${total} 个下游接口 · ${connected} 个已连接` : connection.currentLink,
    badge: kind === "dock" ? "扩展坞" : "USB Hub",
    iconKind: kind === "dock" ? "dock" : "usb-hub",
    ...connection,
    downstreamInterfaceCount: total,
    connectedDownstreamInterfaceCount: connected,
    idleDownstreamInterfaceCount: idle,
    usbSpecification: specification,
    summaryFields: summaryFields(
      ["设备类型", typeLabel],
      ["上游接口", connection.upstreamInterface],
      ["下游接口", String(total)],
      ["已连接设备", String(connected)]
    ),
    capabilityLabels: [
      total > 0 ? `${total} 个下游接口` : undefined,
      specification !== "--" ? specification : undefined
    ].filter((value): value is string => Boolean(value)),
    searchTerms: [typeLabel, kind, `${total} ports`, displayValue(context.port.usb?.deviceClass)]
  } as DockDeviceModel | UsbHubDeviceModel;
}
