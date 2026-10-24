import { curveOnTemperatures } from "./fanCurve.ts";
import type { ControlCapability, ControlCurvePoint, ControlSetting } from "./controlTypes.ts";

/** Selecting an item starts at its observed value or declared default, never at an invented minimum. */
export function initialSettingFor(capability: ControlCapability, actual?: number | null): ControlSetting {
  if (capability.valueKind === "toggle") return { capabilityId: capability.id, toggle: true };
  if (capability.valueKind === "curve") return { capabilityId: capability.id, curve: null };
  return {
    capabilityId: capability.id,
    number: typeof actual === "number" && Number.isFinite(actual)
      ? actual : capability.range?.defaultValue ?? null,
    unit: capability.range?.unit ?? ""
  };
}

/** A failed or empty firmware read cannot initialize a writable curve. */
export function curveSettingFromRead(
  capabilityId: string,
  firmware: readonly ControlCurvePoint[] | null,
  temperatures: readonly number[],
  curveExecution: string
): ControlSetting | null {
  return firmware?.length ? {
    capabilityId,
    curve: curveOnTemperatures(firmware, temperatures),
    curveExecution
  } : null;
}
