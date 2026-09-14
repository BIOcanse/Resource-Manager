import {
  connectionFacts,
  deviceTitle,
  displayValue,
  joinSummary,
  summaryFields
} from "./adapterEvidence.ts";
import type { DeviceAdapter, MonitorDeviceModel } from "./types";
import type { DeviceTopologyDisplayConnection } from "../../types";

export const monitorAdapter: DeviceAdapter<MonitorDeviceModel> = {
  id: "monitor",
  matches: ({ port }) => port.display?.active === true
    && port.display.targetAvailable === true,
  createModel: (context) => {
    const display = context.port.display!;
    const title = deviceTitle(context, "外接显示器");
    const resolution = displayValue(display.resolution);
    const refreshRate = displayValue(display.refreshRate);
    const connectorTechnology = displayValue(display.connectorTechnology, context.port.protocol);
    const hdrState = resolveHdrState(display);
    const bitDepth = display.bitsPerColorChannel
      ? `${display.bitsPerColorChannel} bit / 色通道`
      : "--";
    const colorEncoding = displayValue(display.colorEncoding);
    const colorSpace = displayValue(display.colorSpace);
    const displayTechnology = displayValue(display.displayTechnology);
    const panelTechnology = displayValue(display.panelTechnology, "EDID 未报告");
    const sdrWhiteLevel = display.sdrWhiteLevelNits
      ? `${display.sdrWhiteLevelNits.toLocaleString()} nits`
      : "--";
    const peakLuminance = display.maximumLuminanceNits
      ? `${display.maximumLuminanceNits.toLocaleString()} nits`
      : "--";
    const fullFrameLuminance = display.maximumFullFrameLuminanceNits
      ? `${display.maximumFullFrameLuminanceNits.toLocaleString()} nits`
      : "--";
    const physicalSize = displayValue(display.physicalSize);
    const connection = connectionFacts(context);
    const deviceType = display.internal ? "内置显示面板" : "显示器";
    return {
      adapterId: "monitor",
      kind: "monitor",
      deviceTypeLabel: deviceType,
      title,
      subtitle: joinSummary(resolution, refreshRate, hdrState === "--" ? undefined : hdrState),
      badge: display.internal ? "内屏" : "显示器",
      iconKind: "monitor",
      ...connection,
      resolution,
      refreshRate,
      connectorTechnology,
      displayState: "活动显示目标",
      hdrState,
      bitDepth,
      colorEncoding,
      colorSpace,
      displayTechnology,
      panelTechnology,
      sdrWhiteLevel,
      peakLuminance,
      fullFrameLuminance,
      physicalSize,
      summaryFields: summaryFields(
        ["当前模式", joinSummary(resolution, refreshRate)],
        ["HDR / 高级颜色", hdrState],
        ["输出位深", bitDepth],
        ["显示技术", display.panelTechnology ?? display.displayTechnology]
      ),
      capabilityLabels: [display.hdrFormats, display.edidVersion, display.physicalSize]
        .filter((value): value is string => Boolean(value?.trim())),
      searchTerms: ["显示器", "monitor", resolution, refreshRate, connectorTechnology, hdrState, bitDepth]
    };
  }
};

function resolveHdrState(display: DeviceTopologyDisplayConnection) {
  const format = display.hdrFormats?.trim();
  if (display.advancedColorEnabled === true) {
    return joinSummary(format, "已开启");
  }
  if (display.advancedColorSupported === true) {
    return joinSummary(format ?? "支持高级颜色", "当前关闭");
  }
  if (display.advancedColorSupported === false) {
    return format ? `${format} · 系统当前不可用` : "不支持高级颜色";
  }
  return format ?? "--";
}
