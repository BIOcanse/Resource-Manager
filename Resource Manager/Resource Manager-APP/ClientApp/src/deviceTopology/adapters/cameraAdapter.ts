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

export const cameraAdapter: DeviceAdapter<CameraDeviceModel> = {
  id: "camera",
  matches: ({ port }) => port.camera != null
    || port.pnpClass?.toLocaleLowerCase() === "camera"
    || hasUsbInterface(port, /Video (?:Control|Streaming)|\[0e\/(?:01|02)\//i),
  createModel: (context) => {
    const reportedTitle = deviceTitle(context, "USB 摄像头");
    const title = context.scope === "internal" && /^USB Composite Device$/i.test(reportedTitle)
      ? "内置摄像头"
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
    const typeLabel = functionOnly ? "摄像头视频功能" : "摄像头";
    const capabilitySource = context.port.camera?.capabilitySource
      ?? (protocols.length > 0 ? "USB Video Class 接口描述符" : "Windows PnP 摄像头功能");
    return {
      adapterId: "camera",
      kind: "camera",
      deviceTypeLabel: typeLabel,
      title,
      subtitle: functionOnly
        ? joinSummary(typeLabel, facts?.transport)
        : joinSummary(bestMode === "--" ? undefined : bestMode, connection.currentLink),
      badge: functionOnly ? "视频功能" : "摄像头",
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
          ["设备角色", typeLabel],
          ["内部传输", facts?.transport],
          ["驱动服务", facts?.driverService],
          ["设备状态", facts?.deviceStatus]
        )
        : summaryFields(
          ["最高原生模式", bestMode],
          ["原生模式", nativeModes.length > 0 ? `${nativeModes.length} 组` : "未报告"],
          ["伴随音频", audioCapable ? "支持" : "未报告"],
          ["当前链路", connection.currentLink]
        ),
      capabilityLabels: capabilityLabels(context.port),
      searchTerms: ["摄像头", "相机", "camera", "video", bestMode, ...protocols]
    };
  }
};

function formatCameraMode(mode: import("../../types").DeviceTopologyCameraMode | null | undefined) {
  if (!mode) return "--";
  const frameRate = mode.maximumFrameRate.toLocaleString(undefined, { maximumFractionDigits: 2 });
  return `${mode.width} x ${mode.height} @ ${frameRate} fps · ${mode.pixelFormat}`;
}
