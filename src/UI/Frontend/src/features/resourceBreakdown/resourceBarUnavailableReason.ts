import { uiText } from "../../text.ts";

/**
 * 后端读不到某条资源条时带的原因（b14fc21a 的显式无读数合同）：总量、容量、百分比为 null，没有软件，带 unavailableReason；
 * resource-breakdown-attribution-unavailable 例外，整机总量照常有，只是拆不到软件。
 * 代码的来源：
 * - 传输层 ResourceBreakdownCurrentBars：resource-breakdown-warming、resource-breakdown-total-unavailable、
 *   gpu-dedicated-memory-total-unavailable；
 * - 采样器 WindowsResourceBreakdownSampler.Bars*：disk-io-*、resource-breakdown-attribution-unavailable、
 *   gpu-dedicated-memory-metrics-unsupported、gpu-dedicated-memory-total-unavailable；
 * - 进程数据集失败码原样透传 ResourceBreakdownSnapshotState：resource-breakdown-dataset-*；
 * - 硬件数据集失败码原样透传 NativeMetricSnapshotCommittedProjection / LastSuccessfulHardwareMetricSnapshot：
 *   hardware-source-unavailable、hardware-counter-baseline、hardware-native-*、hardware-dataset-*。
 * 同一类原因共用一句话（九种语言各一句，在 uiText.resourceBreakdown.unavailableReason）；不认识的代码原样显示，不藏。
 */
export type ResourceBarUnavailableFamily =
  | "warming"
  | "sourceUnavailable"
  | "incomplete"
  | "unsupported"
  | "totalUnavailable"
  | "missing"
  | "stale"
  | "failed"
  | "invalid"
  | "attributionUnavailable";

export const resourceBarUnavailableReasonFamilies: Readonly<Record<string, ResourceBarUnavailableFamily>> = Object.freeze({
  "resource-breakdown-warming": "warming",
  "hardware-counter-baseline": "warming",
  "hardware-native-warming": "warming",
  "hardware-dataset-warming": "warming",
  "hardware-source-unavailable": "sourceUnavailable",
  "hardware-native-unavailable": "sourceUnavailable",
  "hardware-dataset-unavailable": "sourceUnavailable",
  "disk-io-source-unavailable": "sourceUnavailable",
  "disk-io-window-incomplete": "incomplete",
  "hardware-native-partial": "incomplete",
  "hardware-dataset-partial": "incomplete",
  "hardware-native-unsupported": "unsupported",
  "hardware-dataset-unsupported": "unsupported",
  "gpu-dedicated-memory-metrics-unsupported": "unsupported",
  "resource-breakdown-total-unavailable": "totalUnavailable",
  "gpu-dedicated-memory-total-unavailable": "totalUnavailable",
  "hardware-dataset-missing": "missing",
  "hardware-native-not-requested": "missing",
  "hardware-dataset-not-requested": "missing",
  "resource-breakdown-dataset-output-missing": "missing",
  "resource-breakdown-dataset-observation-not-current": "stale",
  "hardware-dataset-non-current": "stale",
  "resource-breakdown-dataset-sample-failed": "failed",
  "hardware-native-invalid": "invalid",
  "hardware-dataset-invalid": "invalid",
  "resource-breakdown-attribution-unavailable": "attributionUnavailable"
});

/** 原因代码对应的这一句；不认识的代码原样返回。 */
export function resourceBarUnavailableText(code: string) {
  return Object.hasOwn(resourceBarUnavailableReasonFamilies, code)
    ? uiText.resourceBreakdown.unavailableReason[resourceBarUnavailableReasonFamilies[code]]
    : code;
}
