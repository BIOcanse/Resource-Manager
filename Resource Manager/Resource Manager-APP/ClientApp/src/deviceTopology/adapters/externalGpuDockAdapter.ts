import {
  connectionFacts,
  deviceTitle,
  displayValue,
  hasAnyEvidence,
  summaryFields
} from "./adapterEvidence.ts";
import type { DeviceAdapter, ExternalGpuDockDeviceModel } from "./types";

export const externalGpuDockAdapter: DeviceAdapter<ExternalGpuDockDeviceModel> = {
  id: "external-gpu-dock",
  matches: ({ port }) => port.advancedInterconnect !== null
    && port.advancedInterconnect !== undefined
    && hasAnyEvidence(port, /thunderbolt|usb4/i)
    && hasAnyEvidence(port, /external gpu|eGPU|graphics adapter|display adapter|外置显卡|显卡扩展坞/i),
  createModel: (context) => {
    const interconnect = context.port.advancedInterconnect!;
    const title = deviceTitle(context, "外置显卡扩展坞");
    const connection = connectionFacts(context);
    const interconnectTechnology = displayValue(interconnect.technology);
    const gpuIdentity = displayValue(context.port.idResolution?.deviceName, context.port.displayName);
    const relationEvidence = displayValue(interconnect.evidence);
    return {
      adapterId: "external-gpu-dock",
      kind: "external-gpu-dock",
      deviceTypeLabel: "显卡扩展坞",
      title,
      subtitle: `${interconnectTechnology} · ${gpuIdentity}`,
      badge: "eGPU",
      iconKind: "external-gpu",
      ...connection,
      interconnectTechnology,
      gpuIdentity,
      relationEvidence,
      summaryFields: summaryFields(
        ["设备类型", "显卡扩展坞"],
        ["上游接口", connection.upstreamInterface],
        ["互连技术", interconnectTechnology],
        ["图形设备", gpuIdentity]
      ),
      capabilityLabels: [interconnectTechnology, "外部图形设备"],
      searchTerms: ["显卡扩展坞", "egpu", interconnectTechnology, gpuIdentity]
    };
  }
};
