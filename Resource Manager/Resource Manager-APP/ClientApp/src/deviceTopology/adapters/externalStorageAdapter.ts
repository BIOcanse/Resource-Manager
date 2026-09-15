import {
  capabilityLabels,
  connectionFacts,
  deviceRevision,
  deviceTitle,
  displayValue,
  formatBytes,
  hasAnyEvidence,
  hasUsbInterface,
  interfaceProtocols,
  summaryFields,
  usbSpecification
} from "./adapterEvidence.ts";
import type { DeviceAdapter, ExternalStorageDeviceModel } from "./types";
import { uiText } from "../../text.ts";

export const externalStorageAdapter: DeviceAdapter<ExternalStorageDeviceModel> = {
  id: "external-storage",
  matches: ({ scope, port }) => port.storage != null
    || hasUsbInterface(port, /Mass Storage|SCSI|UAS|\[08\/06\/(?:50|62)\]/i)
    || (scope === "external" && hasAnyEvidence(port, /USBSTOR|UASPSTOR/i)),
  createModel: (context) => {
    const storage = context.port.storage;
    const title = storage?.model?.trim() || deviceTitle(context, uiText.deviceAdapters.externalStorageDevice);
    const connection = connectionFacts(context);
    const protocols = interfaceProtocols(context.port);
    const external = isExternalStorage(context.port.storage?.busType, context.port.usb != null);
    const deviceTypeLabel = external ? uiText.deviceAdapters.externalStorage : uiText.deviceAdapters.disk;
    const transportMode = protocols.length > 0
      ? resolveStorageTransport(protocols)
      : displayValue(storage?.busType, context.port.protocol);
    const specification = usbSpecification(context.port);
    const revision = deviceRevision(context.port);
    const capacity = formatBytes(storage?.capacityBytes);
    const busType = displayValue(storage?.busType);
    const mediaType = displayValue(storage?.mediaType);
    const partitionStyle = displayValue(storage?.partitionStyle);
    const healthStatus = displayValue(storage?.healthStatus);
    const partitions = storage?.partitions ?? [];
    return {
      adapterId: "external-storage",
      kind: "external-storage",
      deviceTypeLabel,
      title,
      subtitle: [capacity, busType, transportMode].filter((value) => value !== "--").join(" · "),
      badge: deviceTypeLabel,
      iconKind: "hard-drive",
      ...connection,
      transportMode,
      usbSpecification: specification,
      deviceRevision: revision,
      interfaceProtocols: protocols,
      capacity,
      busType,
      mediaType,
      partitionStyle,
      healthStatus,
      partitions,
      summaryFields: summaryFields(
        [uiText.deviceAdapters.label.driveCapacity, capacity],
        [uiText.deviceAdapters.label.busAndTransport, [busType, transportMode].filter((value) => value !== "--").join(" / ")],
        [uiText.deviceAdapters.label.partitions, partitions.length > 0 ? uiText.deviceAdapters.partitionCount(partitions.length) : uiText.deviceAdapters.notReported],
        [uiText.deviceAdapters.label.healthState, healthStatus]
      ),
      capabilityLabels: capabilityLabels(context.port),
      searchTerms: [uiText.deviceAdapters.disk, "存储", uiText.deviceAdapters.externalStorage, "移动硬盘", "disk", "external storage", capacity, busType, transportMode, ...protocols]
    };
  }
};

function isExternalStorage(busType: string | null | undefined, hasUsbConnection: boolean) {
  return hasUsbConnection || /USB|IEEE 1394|Thunderbolt/i.test(busType ?? "");
}

function resolveStorageTransport(protocols: readonly string[]) {
  if (protocols.some((value) => /UAS|\[08\/06\/62\]/i.test(value))) {
    return "USB Attached SCSI (UAS)";
  }
  if (protocols.some((value) => /Bulk-Only|\[08\/06\/50\]/i.test(value))) {
    return "USB Mass Storage (Bulk-Only)";
  }
  return "USB Mass Storage";
}
