import {
  defineResponseDecoder,
  requireArray,
  requireNonEmptyString,
  requireRecord,
  requireString
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type {
  ControlActualState,
  ControlActualValue
} from "../../control/controlTypes.ts";

function optionalNumber(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

function optionalString(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}

export const controlActualStateDecoder = defineResponseDecoder<ControlActualState>(
  "control.actual.v1",
  (value) => {
    const record = requireRecord(value, "$");
    return {
      values: requireArray(record.values, "$.values").map(
        (row, index): ControlActualValue => {
          const entry = requireRecord(row, `$.values[${index}]`);
          return {
            objectId: requireNonEmptyString(entry.objectId, `$.values[${index}].objectId`),
            capabilityId: requireNonEmptyString(
              entry.capabilityId,
              `$.values[${index}].capabilityId`),
            number: optionalNumber(entry.number),
            toggle: typeof entry.toggle === "boolean" ? entry.toggle : null,
            unit: optionalString(entry.unit),
            unreadableReason: optionalString(entry.unreadableReason)
          };
        }),
      readAt: requireString(record.readAt, "$.readAt")
    };
  });

/**
 * 实际状态的订阅地址。
 *
 * 后端那一侧读取只返回当前值、不触发采样，所以这里订得勤一点也不会多碰硬件。
 */
export function buildControlActualStateSubscriptionUrl(intervalMs: number): string {
  const params = new URLSearchParams({
    intervalMs: String(Math.max(1, Math.ceil(intervalMs)))
  });
  return `/api/control/actual/subscribe?${params.toString()}`;
}
