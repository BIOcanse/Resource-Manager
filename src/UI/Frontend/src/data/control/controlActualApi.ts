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
} from "../../features/control/controlTypes.ts";
import { readControlReason } from "../../features/control/controlApi.ts";

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
            unreadableReason: readControlReason(entry.unreadableReason, `$.values[${index}].unreadableReason`)
          };
        }),
      readAt: requireString(record.readAt, "$.readAt")
    };
  });

/** Subscribe to current state; backend reads do not trigger hardware sampling. */
export function buildControlActualStateSubscriptionUrl(intervalMs: number): string {
  const params = new URLSearchParams({
    intervalMs: String(Math.max(1, Math.ceil(intervalMs)))
  });
  return `/api/control/actual/subscribe?${params.toString()}`;
}
