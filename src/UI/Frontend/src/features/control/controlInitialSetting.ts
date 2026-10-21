import type { ControlCapability, ControlSetting } from "./controlTypes.ts";

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
