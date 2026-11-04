import { uiText, currentLanguage } from "../text.ts";
import { gpuVfCurveReasonText } from "./gpuVfCurveReasonText.ts";
import type { AppCopy } from "../i18n/copy/index.ts";
import type { ControlApplyReason } from "../features/control/controlTypes.ts";

// Native outcome identities remain facts. Only this frontend table chooses wording.
const groups: Readonly<Record<string, readonly string[]>> = {
  "control-driver-missing": ["nvidia.M2", "nvidia.M24", "adlx.Component"],
  "control-object-missing": ["nvidia.M3", "nvidia.M28"],
  "control-hardware-unsupported": ["nvidia.M4", "nvidia.M6", "nvidia.M7", "nvidia.M25", "nvidia.M52", "nvidia.M53", "amd-cpu.NotSupported", "integrated-gpu.NotSupported", "adlx.Platform"],
  "control-driver-rejected": ["nvidia.M5", "nvidia.M29", "nvidia.M38", "nvidia.M56", "amd-cpu.SmuRejected", "amd-cpu.StatusRejected", "integrated-gpu.SmuRejected", "integrated-gpu.DriverRejected"],
  "control-range-fixed": ["nvidia.M8"],
  "control-number-required": ["nvidia.M9", "nvidia.M50", "amd-cpu.NumberRequired", "intel-cpu.NumberRequired", "integrated-gpu.NumberRequired"],
  "control-writer-missing": ["nvidia.M17", "intel-cpu.MailboxModuleUnavailable", "intel-cpu.PowerLimitModuleUnavailable", "adlx.NotImplemented"],
  "control-firmware-locked": ["nvidia.M18", "nvidia.M26", "intel-cpu.FirmwarePowerLimitLocked", "intel-cpu.FirmwarePowerRegisterLocked"],
  "control-reading-missing": ["nvidia.M23", "nvidia.M34", "nvidia.M46", "nvidia.M57", "nvidia.M62", "amd-cpu.Unreadable", "amd-cpu.ReadingMissing", "integrated-gpu.ReadingMissing", "uniwill-budget-unreadable", "fan-duty-unreadable"],
  "control-permission-required": ["nvidia.M27", "nvidia.M37", "nvidia.M51", "nvidia.M54"],
  "control-out-of-range": ["nvidia.M49", "nvidia.M55", "intel-cpu.OutOfRange", "adlx.InvalidRangeOrStep"],
  "control-range-unavailable": ["nvidia.M63"],
  "control-readback-mismatch": ["nvidia.M47", "amd-cpu.NotEffective", "intel-cpu.VoltageReadbackMismatch", "intel-cpu.PowerReadbackMismatch", "integrated-gpu.NotEffective", "adlx.ReadbackMismatch", "fan-ownership-unverified", "uniwill-register-unverified", "uniwill-enable-unverified"],
  "control-helper-missing": ["amd-cpu.BridgeMissing", "integrated-gpu.BridgeMissing"],
  "control-backend-unavailable": ["amd-cpu.BridgeSilent", "integrated-gpu.BridgeSilent", "fan-core-timeout", "fan-core-failed", "fan-core-silent", "uniwill-platform-missing"],
  "control-fixed-chassis-required": ["adlx.RouteRejected"],
  "control-thermal-limit-unverifiable": ["amd-cpu.TemperatureLimitUnverifiable"],
  "control-current-limit-unconfirmed": ["amd-cpu.CurrentLimitMappingUnconfirmed"],
  "control-baseline-unavailable": ["amd-cpu.BaselineUnavailable", "intel-cpu.BaselineUnavailable", "integrated-gpu.BaselineUnavailable"],
  "control-mmio-write-unimplemented": ["intel-cpu.MmioWriteNotImplemented"],
  "control-register-write-unimplemented": ["intel-cpu.RegisterWriteNotImplemented"],
  "control-intel-driver-missing": ["integrated-gpu.IntelDriverMissing"],
  "control-overclock-consent-required": ["integrated-gpu.OverclockNotAccepted"],
  "control-overclock-unsupported": ["integrated-gpu.WaiverRejected"],
  "control-write-failed": ["adlx.HardwareFailure"],
  "control-write-rejected": ["fan-core-rejected"]
};
const aliases = new Map(Object.entries(groups).flatMap(([key, codes]) => codes.map((code) => [code, key] as const)));

export function controlReasonText(reason: ControlApplyReason | null | undefined, copy: AppCopy["control"] = uiText.control): string {
  const curveReason = gpuVfCurveReasonText(reason, currentLanguage());
  if (curveReason) return curveReason;
  const text = copy.recovery;
  const code = aliases.get(reason?.code ?? "") ?? reason?.code;
  switch (code) {
    case "control-object-missing": return text.objectMissing;
    case "control-capability-missing": case "control-capability-mismatch": return text.capabilityMissing;
    case "control-writer-missing": return text.writerMissing;
    case "control-backend-unavailable": return text.backendUnavailable;
    case "control-release-failed": return text.releaseFailed;
    case "control-unit-mismatch": return text.unitMismatch;
    case "control-write-failed": return text.writeFailed;
    case "control-write-rejected": return text.writeRejected;
    case "startup-profile-read-only": return text.readOnly;
    case "control-preset-name-invalid": return reason?.arguments?.maximum != null
      ? copy.presentation.presetNameLimit(reason.arguments.maximum) : copy.presentation.text["control-invalid-request"];
    case "control-preset-count-limit": return reason?.arguments?.maximum != null
      ? copy.presentation.presetCountLimit(reason.arguments.maximum) : copy.presentation.text["control-invalid-request"];
    case "nvidia.M36": {
      const args = reason?.arguments;
      return args?.minimum != null && args.maximum != null && args.unit != null
        ? copy.presentation.clockRangeRejected(args.minimum, args.maximum, args.unit)
        : copy.presentation.text["control-out-of-range"];
    }
    case "control-readback-mismatch": {
      const args = reason?.arguments;
      if (args?.requested != null && args.observed != null && args.unit != null)
        return copy.presentation.readbackMismatch(args.requested, args.observed, args.unit);
      break;
    }
    case "control-access-level-required": {
      const level = reason?.arguments?.requiredAccessLevel;
      return level ? text.accessRequired(copy.accessLevel.name[level]) : text.writeRejected;
    }
    case "control-out-of-range": {
      const args = reason?.arguments;
      if (args?.minimum != null && args.maximum != null && args.unit != null)
        return text.outOfRange(args.minimum, args.maximum, args.unit === "档" ? copy.presentation.text["档"] : args.unit);
      break;
    }
  }
  const localized = code ? copy.presentation.text[code] : undefined;
  if (localized) return localized;
  if (code) console.warn("Unknown control reason", code);
  return text.writeRejected;
}

/** Decode the small failure envelope without ever displaying raw exception strings. */
export function controlRequestErrorText(payload: unknown): string | null {
  if (!payload || typeof payload !== "object" || !("reason" in payload)) return null;
  const reason = payload.reason;
  if (!reason || typeof reason !== "object" || !("code" in reason) || typeof reason.code !== "string") return null;
  const raw = "arguments" in reason ? reason.arguments : null;
  if (raw == null) return controlReasonText({ code: reason.code });
  if (typeof raw !== "object") return null;
  const record = raw as Record<string, unknown>;
  const args: NonNullable<ControlApplyReason["arguments"]> = {};
  for (const key of ["minimum", "maximum", "requested", "observed"] as const) {
    if (record[key] == null) continue;
    const value = record[key];
    if (typeof value !== "number" || !Number.isFinite(value)) return null;
    args[key] = value;
  }
  if (record.unit != null) {
    if (typeof record.unit !== "string") return null;
    args.unit = record.unit;
  }
  return controlReasonText({ code: reason.code, arguments: args });
}
