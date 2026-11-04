import { formatInterfaceNumber } from "../../../presentation/interfaceNumbers.ts";
import { renderBackendMessage } from "../../../presentation/backendMessage.ts";
import {
  capabilityLabels,
  connectionFacts,
  deviceTitle,
  hasUsbInterface,
  internalDeviceFacts,
  interfaceProtocols,
  joinSummary,
  summaryFields,
  usbInterfaceCapability
} from "./adapterEvidence.ts";
import type { CameraDeviceModel, DeviceAdapter } from "./types";
import { uiText } from "../../../text.ts";

export const cameraAdapter: DeviceAdapter<CameraDeviceModel> = {
  id: "camera",
  matches: ({ port }) => port.camera != null
    || port.pnpClass?.toLocaleLowerCase() === "camera"
    || hasUsbInterface(port, 0x0e, 0x01)
    || hasUsbInterface(port, 0x0e, 0x02),
  createModel: (context) => {
    const reportedTitle = deviceTitle(context, uiText.deviceAdapters.usbCamera);
    const title = context.scope === "internal" && /^USB Composite Device$/i.test(reportedTitle)
      ? uiText.deviceAdapters.internalCamera
      : reportedTitle;
    const connection = connectionFacts(context);
    const facts = internalDeviceFacts(context);
    const protocols = interfaceProtocols(context.port);
    const videoControl = usbInterfaceCapability(context.port, 0x0e, 0x01);
    const videoStreaming = usbInterfaceCapability(context.port, 0x0e, 0x02);
    const audioCapable = context.port.usb?.interfaces == null ? null
      : hasUsbInterface(context.port, 0x01, 0x01) || hasUsbInterface(context.port, 0x01, 0x02);
    const hasInterfaces = (context.port.usb?.interfaces?.length ?? 0) > 0;
    const nativeModes = context.port.camera?.nativeModes ?? [];
    const bestMode = formatCameraMode(context.port.camera?.bestMode);
    const functionOnly = context.scope === "internal" && context.port.camera == null && !hasInterfaces;
    const typeLabel = functionOnly ? uiText.deviceAdapters.cameraVideoFunction : uiText.deviceAdapters.camera;
    const capabilitySource = renderBackendMessage(
      context.port.camera?.capabilitySource,
      hasInterfaces
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
          [uiText.deviceAdapters.label.companionAudio, context.port.usb?.interfaces == null
            ? uiText.deviceTopology.deviceCategory.unknown
            : audioCapable ? uiText.deviceAdapters.supported : uiText.deviceAdapters.notReported],
          [uiText.deviceAdapters.label.currentLink, connection.currentLink]
        ),
      capabilityLabels: capabilityLabels(context.port),
      searchTerms: [uiText.deviceAdapters.camera, "相机", "camera", "video", bestMode, ...protocols]
    };
  }
};

function formatCameraMode(mode: import("../../../types").DeviceTopologyCameraMode | null | undefined) {
  if (!mode) return "--";
  const frameRate = formatInterfaceNumber(mode.maximumFrameRate, { maximumFractionDigits: 2 });
  return `${mode.width} x ${mode.height} @ ${frameRate} fps · ${mode.pixelFormat}`;
}
