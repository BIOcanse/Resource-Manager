import {
  defineResponseDecoder,
  requireNonNegativeFiniteNumber,
  requireNullable,
  requireOneOf,
  requireRecord,
  requireString,
  ResponseDecodeError
} from "../frontendRuntime/request/ResponseDecoder.ts";
import { productAutomaticCheckStatuses, type ProductUpdateState } from "../features/settings/productUpdatePresentation.ts";

/** Replay current product-update status, then push each change in the GET response shape. */
export function buildProductUpdateStatusSubscriptionUrl(): string {
  return "/api/updates/product/status/subscribe";
}

// Update timestamps must use UTC with Z or +00:00.
const utcTimePattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:Z|\+00:00)$/;

/** Accept only ISO timestamps with Z or +00:00; reject absent or other offsets. */
export function parseUtcTime(value: string): Date | null {
  if (!utcTimePattern.test(value)) return null;
  const time = new Date(value);
  return Number.isFinite(time.getTime()) ? time : null;
}

/** Format a UTC timestamp for display. */
export function formatUtcTime(value: string): string {
  const time = parseUtcTime(value);
  if (!time) throw new RangeError(`Not a UTC time: ${value}`);
  const pad = (number: number) => String(number).padStart(2, "0");
  return `${time.getUTCFullYear()}-${pad(time.getUTCMonth() + 1)}-${pad(time.getUTCDate())} `
    + `${pad(time.getUTCHours())}:${pad(time.getUTCMinutes())} UTC`;
}

export const productUpdateStatusDecoder = defineResponseDecoder<ProductUpdateState>(
  "product-update-status.v1",
  (value) => {
    const record = requireRecord(value);
    const optional = <T>(key: string, decode: (item: unknown, path: string) => T) =>
      requireNullable(record[key] ?? null, `$.${key}`, decode);
    const text = (item: unknown, path: string) => requireString(item, path);
    const time = (item: unknown, path: string) => {
      const textValue = requireString(item, path);
      if (!parseUtcTime(textValue)) throw new ResponseDecodeError(path, "UTC time with Z or +00:00");
      return textValue;
    };
    const parameters = optional("errorParameters", (item, path) => Object.fromEntries(
      Object.entries(requireRecord(item, path)).map(([key, entry]) => [key, requireString(entry, `${path}.${key}`)])));
    return {
      stage: requireString(record.stage, "$.stage"),
      targetVersion: optional("targetVersion", text),
      errorCode: optional("errorCode", text),
      errorParameters: parameters,
      lastCheckedAt: optional("lastCheckedAt", time),
      checkResult: optional("checkResult", text),
      automaticCheckStatus: optional("automaticCheckStatus", (item, path) => requireOneOf(item, path, productAutomaticCheckStatuses)),
      updatedAt: optional("updatedAt", time),
      failedVersion: optional("failedVersion", text),
      availableVersion: optional("availableVersion", text),
      nextCheckAt: optional("nextCheckAt", time),
      onlineTimeSinceLastCheckSeconds: optional("onlineTimeSinceLastCheckSeconds", requireNonNegativeFiniteNumber)
    };
  });
