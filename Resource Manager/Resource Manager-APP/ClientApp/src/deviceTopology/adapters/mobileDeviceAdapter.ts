import {
  capabilityLabels,
  connectionFacts,
  displayValue,
  deviceTitle,
  hasUsbInterface,
  interfaceProtocols,
  joinSummary,
  summaryFields
} from "./adapterEvidence.ts";
import type { DeviceAdapter, MobileDeviceModel } from "./types";

export const mobileDeviceAdapter: DeviceAdapter<MobileDeviceModel> = {
  id: "mobile-device",
  matches: ({ port }) => port.smartDevice != null
    || hasUsbInterface(port, /MTP/i)
    || (hasUsbInterface(port, /Still Image|\[06\/01\/01\]/i)
      && hasUsbInterface(port, /CDC|Vendor Specific|\[(?:02|0a|ff)\//i)),
  createModel: (context) => {
    const title = deviceTitle(context, "移动设备");
    const connection = connectionFacts(context);
    const protocols = interfaceProtocols(context.port);
    const mediaTransfer = protocols.some((value) => /MTP|Still Image|\[06\/01\/01\]/i.test(value));
    const dataConnection = protocols.some((value) => /CDC|\[(?:02|0a)\//i.test(value));
    const vendorChannel = protocols.some((value) => /Vendor Specific|\[ff\//i.test(value));
    const smart = context.port.smartDevice;
    const deviceType = displayValue(smart?.deviceType, "移动设备");
    const manufacturer = displayValue(smart?.manufacturer, context.port.manufacturer ?? undefined);
    const model = displayValue(smart?.model, context.port.displayName);
    const firmwareVersion = displayValue(smart?.firmwareVersion, "设备未报告");
    const protocol = displayValue(smart?.protocol, mediaTransfer ? "MTP / PTP" : undefined);
    const transport = displayValue(smart?.transport, connection.currentLink);
    const battery = typeof smart?.batteryPercent === "number" ? `${smart.batteryPercent}%` : "设备未报告";
    const storages = smart?.storages ?? [];
    return {
      adapterId: "mobile-device",
      kind: "mobile-device",
      deviceTypeLabel: deviceType,
      title,
      subtitle: joinSummary(manufacturer === "--" ? undefined : manufacturer, model, protocol),
      badge: deviceType,
      iconKind: resolveSmartDeviceIcon(deviceType),
      ...connection,
      mediaTransfer,
      dataConnection,
      vendorChannel,
      interfaceProtocols: protocols,
      manufacturer,
      model,
      firmwareVersion,
      protocol,
      transport,
      battery,
      storages,
      summaryFields: summaryFields(
        ["设备类型", deviceType],
        ["制造商 / 型号", joinSummary(manufacturer === "--" ? undefined : manufacturer, model)],
        ["设备协议", protocol],
        ["连接传输", transport]
      ),
      capabilityLabels: capabilityLabels(context.port),
      searchTerms: ["移动设备", "手机", "平板", "电脑", "mobile", "mtp", manufacturer, model, ...protocols]
    };
  }
};

function resolveSmartDeviceIcon(deviceType: string): MobileDeviceModel["iconKind"] {
  if (deviceType.includes("平板")) return "tablet";
  if (deviceType.includes("电脑")) return "laptop";
  if (deviceType.includes("相机")) return "camera";
  return "smartphone";
}
