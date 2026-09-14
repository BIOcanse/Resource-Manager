import {
  capabilityLabels,
  connectionFacts,
  deviceRevision,
  deviceTitle,
  displayValue,
  internalDeviceFacts,
  interfaceProtocols,
  joinSummary,
  summaryFields,
  usbSpecification
} from "./adapterEvidence.ts";
import type { DeviceAdapter, GenericDeviceModel } from "./types";

export const genericDeviceAdapter: DeviceAdapter<GenericDeviceModel> = {
  id: "generic-device",
  matches: () => true,
  createModel: (context) => {
    const title = deviceTitle(context);
    const connection = connectionFacts(context);
    const protocols = interfaceProtocols(context.port);
    const deviceClass = displayValue(context.port.usb?.deviceClass, context.port.hardwareKind);
    const specification = usbSpecification(context.port);
    const revision = deviceRevision(context.port);
    const internal = context.scope === "internal";
    const facts = internalDeviceFacts(context);
    const typeLabel = resolveGenericType(context.port, internal);
    return {
      adapterId: "generic-device",
      kind: "generic-device",
      deviceTypeLabel: typeLabel,
      title,
      subtitle: internal
        ? joinSummary(typeLabel, facts?.transport)
        : joinSummary(deviceClass, connection.currentLink),
      badge: typeLabel,
      iconKind: "device",
      ...connection,
      deviceClass,
      usbSpecification: specification,
      deviceRevision: revision,
      interfaceProtocols: protocols,
      summaryFields: internal
        ? summaryFields(
          ["设备类型", typeLabel],
          ["内部传输", facts?.transport],
          ["驱动服务", facts?.driverService],
          ["设备状态", facts?.deviceStatus]
        )
        : summaryFields(
          ["设备类型", deviceClass],
          ["上游接口", connection.upstreamInterface],
          ["USB 规范", specification],
          ["当前链路", connection.currentLink]
        ),
      capabilityLabels: capabilityLabels(context.port),
      searchTerms: [typeLabel, internal ? "内部设备" : "外接设备", "device", deviceClass, ...protocols]
    };
  }
};

function resolveGenericType(port: import("../../types").DeviceTopologyPort, internal: boolean) {
  if (!internal) return "外接设备";
  if (/Composite/i.test(`${port.displayName} ${port.usb?.deviceClass ?? ""}`)) return "USB 复合设备";
  if (port.pnpClass?.toLocaleLowerCase() === "ports") return "内部通信端口";
  return "内部设备";
}
