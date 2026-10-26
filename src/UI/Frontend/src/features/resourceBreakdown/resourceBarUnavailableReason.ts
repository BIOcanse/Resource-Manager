import { uiText } from "../../text.ts";

/**
 * 后端读不到某条资源条时带的原因。权威列表：dev docs/runtime_process_attribution_and_io_network_telemetry.md
 * 的「Resource bar and fact unavailable reasons」一节（daad5039 起按代码追出来的 30 项：29 个代码和一个带参数的家族
 * hardware-sample-{ExceptionTypeName}）。资源条和资源表事实用同一套原因，条原样照抄事实的原因。
 * 读不到时总量、容量、百分比为 null，没有软件；resource-breakdown-attribution-unavailable 例外，总量照常有，只是拆不到软件。
 * 同一类原因共用一句话（九种语言各一句，在 uiText.resourceBreakdown.unavailableReason）；
 * 家族的后缀（异常类型名）是数据，原样写进那句话；不认识的代码原样显示，不藏。
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
  "gpu-measure-total-unavailable": "totalUnavailable",
  "system-total-unavailable": "totalUnavailable",
  "hardware-dataset-missing": "missing",
  "hardware-native-not-requested": "missing",
  "hardware-dataset-not-requested": "missing",
  "resource-breakdown-dataset-output-missing": "missing",
  "gpu-measure-observation-unavailable": "missing",
  "resource-breakdown-dataset-observation-not-current": "stale",
  "hardware-dataset-non-current": "stale",
  "resource-breakdown-dataset-sample-failed": "failed",
  "hardware-native-invalid": "invalid",
  "hardware-dataset-invalid": "invalid",
  "resource-breakdown-attribution-unavailable": "attributionUnavailable"
});

/** 带参数的家族：hardware-sample-{ExceptionTypeName}，后缀是采样时抛出的异常的类型名。 */
export const resourceBarHardwareSamplePrefix = "hardware-sample-";

/** 原因代码对应的这一句；家族把后缀原样写进句子；不认识的代码原样返回。 */
export function resourceBarUnavailableText(code: string) {
  if (Object.hasOwn(resourceBarUnavailableReasonFamilies, code)) {
    return uiText.resourceBreakdown.unavailableReason[resourceBarUnavailableReasonFamilies[code]];
  }
  if (code.startsWith(resourceBarHardwareSamplePrefix) && code.length > resourceBarHardwareSamplePrefix.length) {
    return uiText.resourceBreakdown.unavailableReason.sampleException(code.slice(resourceBarHardwareSamplePrefix.length));
  }
  return code;
}
