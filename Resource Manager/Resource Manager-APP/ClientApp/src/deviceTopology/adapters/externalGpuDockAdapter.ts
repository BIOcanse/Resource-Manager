import {
  connectionFacts,
  deviceTitle,
  displayValue,
  hasAnyEvidence,
  summaryFields
} from "./adapterEvidence.ts";
import type { DeviceAdapter, ExternalGpuDockDeviceModel } from "./types";
import { uiText } from "../../text.ts";

export const externalGpuDockAdapter: DeviceAdapter<ExternalGpuDockDeviceModel> = {
  id: "external-gpu-dock",
  matches: ({ port }) => port.advancedInterconnect !== null
    && port.advancedInterconnect !== undefined
    && hasAnyEvidence(port, /thunderbolt|usb4/i)
    && hasAnyEvidence(port, /external gpu|eGPU|graphics adapter|display adapter|外置显卡|显卡扩展坞/i),
  createModel: (context) => {
    const interconnect = context.port.advancedInterconnect!;
    const title = deviceTitle(context, uiText.deviceAdapters.externalGpuDock);
    const connection = connectionFacts(context);
    const interconnectTechnology = displayValue(interconnect.technology);
    const gpuIdentity = displayValue(context.port.idResolution?.deviceName, context.port.displayName);
    const relationEvidence = displayValue(interconnect.evidence);
    return {
      adapterId: "external-gpu-dock",
      kind: "external-gpu-dock",
      deviceTypeLabel: uiText.deviceAdapters.gpuDock,
      title,
      subtitle: `${interconnectTechnology} · ${gpuIdentity}`,
      badge: "eGPU",
      iconKind: "external-gpu",
      ...connection,
      interconnectTechnology,
      gpuIdentity,
      relationEvidence,
      summaryFields: summaryFields(
        [uiText.deviceAdapters.label.deviceType, uiText.deviceAdapters.gpuDock],
        [uiText.deviceAdapters.label.upstreamInterface, connection.upstreamInterface],
        [uiText.deviceAdapters.label.interconnectTechnology, interconnectTechnology],
        [uiText.deviceAdapters.label.graphicsDevice, gpuIdentity]
      ),
      capabilityLabels: [interconnectTechnology, uiText.deviceAdapters.externalGraphicsDevice],
      searchTerms: [uiText.deviceAdapters.gpuDock, "egpu", interconnectTechnology, gpuIdentity]
    };
  }
};
