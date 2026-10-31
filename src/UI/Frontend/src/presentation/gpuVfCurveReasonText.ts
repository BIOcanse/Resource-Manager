import type { ControlApplyReason } from "../features/control/controlTypes.ts";
import { gpuVfCurveReasons } from "../i18n/copy/gpuVfCurveReasons.ts";
import type { GpuVfReasonCategory } from "../i18n/copy/gpuVfCurveReasons.ts";
import type { ConcreteAppLanguageMode } from "../i18n/settingsTypes.ts";

export function gpuVfCurveReasonText(reason: ControlApplyReason | null | undefined, language: ConcreteAppLanguageMode): string | null {
  const code = reason?.code;
  if (!code?.startsWith("gpu-vf-") && !code?.startsWith("nvidia-vf-")) return null;
  let category: GpuVfReasonCategory = "unavailable";
  if (code === "nvidia-vf-restore-protocol-unverified") category = "restore";
  else if (code.includes("legacy-")) category = "legacy";
  else if (code.includes("binding") || code.endsWith("point-sequence-mismatch")) category = "binding";
  else if (code.endsWith("out-of-range")) category = "range";
  else if (code.endsWith("in-use")) category = "inUse";
  else if (code.endsWith("not-found")) category = "missing";
  else if (code.endsWith("-limit") || code.endsWith("name-invalid")) category = "limit";
  else if (code.endsWith("default-unavailable") || code.endsWith("default-frequency-unavailable")) category = "defaults";
  else if (code.endsWith("invalid") || code.endsWith("conflict") || code.endsWith("ambiguous") || code.endsWith("execution-missing")) category = "input";
  const args = reason?.arguments;
  const range = args?.minimum != null && args.maximum != null ? ` [${args.minimum}, ${args.maximum}] ${args.unit ?? ""}`
    : args?.maximum != null ? ` ≤ ${args.maximum}` : "";
  return `${(gpuVfCurveReasons[language] ?? gpuVfCurveReasons["en-US"])[category]} (${code}${range})`;
}
