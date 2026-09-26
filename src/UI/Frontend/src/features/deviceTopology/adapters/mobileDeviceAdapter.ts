import { portDisplayName } from "../deviceVocabulary.ts";
import { portableDeviceTypeLabel } from "../deviceVocabulary.ts";
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
import { uiText } from "../../../text.ts";

export const mobileDeviceAdapter: DeviceAdapter<MobileDeviceModel> = {
  id: "mobile-device",
  matches: ({ port }) => port.smartDevice != null
    || hasUsbInterface(port, /MTP/i)
    || (hasUsbInterface(port, /Still Image|\[06\/01\/01\]/i)
      && hasUsbInterface(port, /CDC|Vendor Specific|\[(?:02|0a|ff)\//i)),
  createModel: (context) => {
    const title = deviceTitle(context, uiText.deviceAdapters.mobileDevice);
    const connection = connectionFacts(context);
    const protocols = interfaceProtocols(context.port);
    const mediaTransfer = protocols.some((value) => /MTP|Still Image|\[06\/01\/01\]/i.test(value));
    const dataConnection = protocols.some((value) => /CDC|\[(?:02|0a)\//i.test(value));
    const vendorChannel = protocols.some((value) => /Vendor Specific|\[ff\//i.test(value));
    const smart = context.port.smartDevice;
    const deviceType = portableDeviceTypeLabel(smart?.deviceType) ?? uiText.deviceAdapters.mobileDevice;
    const manufacturer = displayValue(smart?.manufacturer, context.port.manufacturer ?? undefined);
    const model = displayValue(smart?.model, portDisplayName(context.port));
    const firmwareVersion = displayValue(smart?.firmwareVersion, uiText.deviceAdapters.deviceNotReported);
    const protocol = displayValue(smart?.protocol, mediaTransfer ? "MTP / PTP" : undefined);
    const transport = displayValue(smart?.transport, connection.currentLink);
    const battery = typeof smart?.batteryPercent === "number" ? `${smart.batteryPercent}%` : uiText.deviceAdapters.deviceNotReported;
    const storages = smart?.storages ?? [];
    return {
      adapterId: "mobile-device",
      kind: "mobile-device",
      deviceTypeLabel: deviceType,
      title,
      subtitle: joinSummary(manufacturer === "--" ? undefined : manufacturer, model, protocol),
      badge: deviceType,
      iconKind: resolveSmartDeviceIcon(smart?.deviceType),
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
        [uiText.deviceAdapters.label.deviceType, deviceType],
        [uiText.deviceAdapters.label.manufacturerModel, joinSummary(manufacturer === "--" ? undefined : manufacturer, model)],
        [uiText.deviceAdapters.label.deviceProtocol, protocol],
        [uiText.deviceAdapters.label.connectionTransport, transport]
      ),
      capabilityLabels: capabilityLabels(context.port),
      searchTerms: [uiText.deviceAdapters.mobileDevice, "手机", "平板", "电脑", "mobile", "mtp", manufacturer, model, ...protocols]
    };
  }
};

// 后端给的是 DevicePortableDeviceTypes 里的 id，图标按 id 选。
function resolveSmartDeviceIcon(deviceType?: string | null): MobileDeviceModel["iconKind"] {
  switch (deviceType) {
    case "tablet": return "tablet";
    case "camera": return "camera";
    default: return "smartphone";
  }
}
