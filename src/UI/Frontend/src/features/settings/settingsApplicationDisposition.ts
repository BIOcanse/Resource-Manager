import type { AppSettingsResult } from "../../types";

export type SettingsApplicationOutcome =
  | "published"
  | "capability-constrained"
  | "persisted-pending"
  | "rejected";

export function classifySettingsApplicationOutcome(
  disposition: AppSettingsResult["runtimeApplicationDisposition"]
): SettingsApplicationOutcome {
  if (disposition === "committedAndApplied") {
    return "published";
  }
  if (disposition === "committedWithCapabilityConstraints") {
    return "capability-constrained";
  }
  if (disposition === "committedWithDeliveryFailures" || disposition === "savedNotApplied") {
    return "persisted-pending";
  }
  return "rejected";
}
