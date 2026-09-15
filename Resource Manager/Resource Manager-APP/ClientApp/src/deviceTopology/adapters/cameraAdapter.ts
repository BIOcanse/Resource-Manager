import { renderBackendMessage } from "../../presentation/backendMessage.ts";
import {
  capabilityLabels,
  connectionFacts,
  deviceTitle,
  hasUsbInterface,
  internalDeviceFacts,
  interfaceProtocols,
  joinSummary,
  summaryFields
} from "./adapterEvidence.ts";
import type { CameraDeviceModel, DeviceAdapter } from "./types";
import { uiText } from "../../text.ts";

export const cameraAdapter: DeviceAdapter<CameraDeviceModel> = {
  id: "camera",
  matches: ({ port }) => port.camera != null
    || port.pnpClass?.toLocaleLowerCase() === "camera"
    || hasUsbInterface(port, /Video (?:Control|Streaming)|\[0e\/(?:01|02)\//i),
  createModel: (context) => {
    const reportedTitle = deviceTitle(context, uiText.deviceAdapters.usbCamera);
    const title = context.scope === "internal" && /^USB Composite Device$/i.test(reportedTitle)
      ? uiText.deviceAdapters.internalCamera
      : reportedTitle;
    const connection = connectionFacts(context);
    const facts = internalDeviceFacts(context);
    const protocols = interfaceProtocols(context.port);
    const videoControl = protocols.some((value) => /Video Control|\[0e\/01\//i.test(value));
    const videoStreaming = protocols.some((value) => /Video Streaming|\[0e\/02\//i.test(value));
    const audioCapable = protocols.some((value) => /Audio (?:Control|Streaming)|\[01\/(?:01|02)\//i.test(value));
    const nativeModes = context.port.camera?.nativeModes ?? [];
    const bestMode = formatCameraMode(context.port.camera?.bestMode);
    const functionOnly = context.scope === "internal" && context.port.camera == null && protocols.length === 0;
    const typeLabel = functionOnly ? uiText.deviceAdapters.cameraVideoFunction : uiText.deviceAdapters.camera;
    const capabilitySource = renderBackendMessage(
      context.port.camera?.capabilitySource,
      protocols.length > 0
        ? uiText.deviceAdapters.uvcInterfaceDescriptor
        : uiText.deviceAdapters.windowsPnpCameraFunction);
    return {
      adapterId: "camera",
      kind: "camera",
      deviceTypeLabel: typeLabel,
      title,
      subtitle: functionOnly
        ? joinSummary(typeLabel, facts?.transport)
        : joinSummary(bestMode === "--" ? undefined : bestMode, connection.currentLink),
      badge: functionOnly ? uiText.deviceAdapters.videoFunctionBadge : uiText.deviceAdapters.camera,
      iconKind: "camera",
      ...connection,
      videoControl,
      videoStreaming,
      audioCapable,
      interfaceProtocols: protocols,
      bestMode,
      nativeModes,
      capabilitySource,
      summaryFields: functionOnly
        ? summaryFields(
          [uiText.deviceAdapters.label.deviceRole, typeLabel],
          [uiText.deviceAdapters.label.internalTransport, facts?.transport],
          [uiText.deviceAdapters.label.driverService, facts?.driverService],
          [uiText.deviceAdapters.label.deviceStatus, facts?.deviceStatus]
        )
        : summaryFields(
          [uiText.deviceAdapters.label.highestNativeMode, bestMode],
          [uiText.deviceAdapters.label.nativeModes, nativeModes.length > 0 ? uiText.deviceAdapters.modeGroupCount(nativeModes.length) : uiText.deviceAdapters.notReported],
          [uiText.deviceAdapters.label.companionAudio, audioCapable ? uiText.deviceAdapters.supported : uiText.deviceAdapters.notReported],
          [uiText.deviceAdapters.label.currentLink, connection.currentLink]
        ),
      capabilityLabels: capabilityLabels(context.port),
      searchTerms: [uiText.deviceAdapters.camera, "相机", "camera", "video", bestMode, ...protocols]
    };
  }
};

function formatCameraMode(mode: import("../../types").DeviceTopologyCameraMode | null | undefined) {
  if (!mode) return "--";
  const frameRate = mode.maximumFrameRate.toLocaleString(undefined, { maximumFractionDigits: 2 });
  return `${mode.width} x ${mode.height} @ ${frameRate} fps · ${mode.pixelFormat}`;
}
