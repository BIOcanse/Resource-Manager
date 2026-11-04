import { uiText } from "../../text.ts";

/** Share backend fact and bar reasons. Missing readings remain null; attribution-only failure may retain totals. Localize known classes and preserve unknown codes and exception suffixes. */
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

/** Preserve the exception type suffix in hardware-sample reasons. */
export const resourceBarHardwareSamplePrefix = "hardware-sample-";

/** Return localized known reasons or null so the caller can place the raw code. */
export function resourceUnavailableReasonText(code: string): string | null {
  if (Object.hasOwn(resourceBarUnavailableReasonFamilies, code)) {
    return uiText.resourceBreakdown.unavailableReason[resourceBarUnavailableReasonFamilies[code]];
  }
  if (code.startsWith(resourceBarHardwareSamplePrefix) && code.length > resourceBarHardwareSamplePrefix.length) {
    return uiText.resourceBreakdown.unavailableReason.sampleException(code.slice(resourceBarHardwareSamplePrefix.length));
  }
  return null;
}

/** Show unknown codes unchanged in the bar heading. */
export function resourceBarUnavailableText(code: string) {
  return resourceUnavailableReasonText(code) ?? code;
}
