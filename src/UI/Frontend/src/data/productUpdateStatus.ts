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

/** 统一订阅通道上的产品更新状态：订阅时先发当前状态，之后每变一次发一次；帧形状与 GET /api/updates/product/status 相同。 */
export function buildProductUpdateStatusSubscriptionUrl(): string {
  return "/api/updates/product/status/subscribe";
}

// 更新状态里的时间一律是协调世界时（用户 2026-10-25「时间按照协调世界时」），传输带 Z 或 +00:00。
const utcTimePattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:Z|\+00:00)$/;

/** 只接受带 Z 或 +00:00 的 ISO 时间；没有时区或其他时区的时间不是合同里的值。 */
export function parseUtcTime(value: string): Date | null {
  if (!utcTimePattern.test(value)) return null;
  const time = new Date(value);
  return Number.isFinite(time.getTime()) ? time : null;
}

/** 例如 2026-10-26 08:00 UTC。 */
export function formatUtcTime(value: string): string {
  const time = parseUtcTime(value);
  if (!time) throw new RangeError(`Not a UTC time: ${value}`);
  const pad = (number: number) => String(number).padStart(2, "0");
  return `${time.getUTCFullYear()}-${pad(time.getUTCMonth() + 1)}-${pad(time.getUTCDate())} `
    + `${pad(time.getUTCHours())}:${pad(time.getUTCMinutes())} UTC`;
}

/** 已累计的联网时长，例如 25:04:09（时:分:秒）。 */
export function formatConnectedDuration(seconds: number): string {
  const total = Math.floor(seconds);
  const pad = (number: number) => String(number).padStart(2, "0");
  return `${Math.floor(total / 3600)}:${pad(Math.floor(total / 60) % 60)}:${pad(total % 60)}`;
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
