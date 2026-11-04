import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import {
  defineResponseDecoder, requireArray, requireNonEmptyString, requireNonNegativeSafeInteger, requireNullable, requireOneOf,
  requireRecord, requireString, ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import { uiText } from "../../text.ts";

/* Device restarts require a backend-suggested action and explicit user confirmation. Unknown numeric result codes remain displayable. */

/** Backend reasonCode names for values 0..10. */
export const deviceRestartReasonNames = [
  "completed", "confirmation-required", "report-not-found", "unsupported-report", "device-evidence-unavailable",
  "invalid-settings", "record-capacity-reached", "persistence-failed", "device-resolution-failed", "device-restart-failed",
  "notification-failed"
] as const;
export type DeviceRestartReasonName = typeof deviceRestartReasonNames[number];

/** Result names for 0..16; record-only 16 means the service stopped during restart. */
export const deviceResultNames = [
  "success", "confirmation-required", "invalid-instance-id", "administrator-required", "device-not-found", "device-unavailable",
  "disable-failed", "enable-failed", "reboot-required", "verification-failed", "driver-not-found", "ambiguous-device",
  "invalid-module", "enumeration-failed", "buffer-too-small", "unsupported-platform", "interrupted"
] as const;
export type DeviceResultName = typeof deviceResultNames[number];

/** Risk codes sent with suggested restart-device actions. */
export const deviceRestartRiskCodes = [
  "DeviceRestartNetworkInterrupted", "DeviceRestartDisplayStateLost", "DeviceRestartMayRequireReboot"
] as const;

export const deviceRestartActionKind = "restart-device";

export function deviceRestartReasonName(code: number): DeviceRestartReasonName | null {
  return deviceRestartReasonNames[code] ?? null;
}

export function deviceResultName(code: number): DeviceResultName | null {
  return deviceResultNames[code] ?? null;
}

/** Restart response; null sequence means the request was not accepted and no record was written. */
export interface DeviceRestartReply {
  readonly reasonCode: number;
  readonly deviceResultCode: number;
  readonly detail: number;
  readonly sequence: number | null;
}

/** One device-action history record. */
export interface DeviceActionRecord {
  readonly sequence: number;
  readonly state: "active" | "history";
  readonly reportEntryId: string;
  readonly deviceInstanceId: string;
  readonly occurredAt: string;
  readonly resultCode: number;
  readonly detail: number;
}

export interface DeviceActionRecordPage {
  readonly records: readonly DeviceActionRecord[];
}

/** Accepted list, including the pagination cursor state, retained by the runtime source. */
export interface DeviceActionRecordsView extends DeviceActionRecordPage {
  readonly hasMore: boolean;
}

/** Number of records requested per page by the records view. */
export const deviceActionRecordsPageSize = 50;

/** Backend-supported page-size range. */
export const deviceActionRecordPageLimit = { minimum: 1, maximum: 16384 } as const;

function timestamp(value: unknown, path: string): string {
  const text = requireString(value, path);
  if (!Number.isFinite(Date.parse(text))) throw new ResponseDecodeError(path, "timestamp");
  return text;
}

export function decodeDeviceRestartReply(value: unknown, path = "$"): DeviceRestartReply {
  const row = requireRecord(value, path);
  return Object.freeze({
    reasonCode: requireNonNegativeSafeInteger(row.reasonCode, `${path}.reasonCode`),
    deviceResultCode: requireNonNegativeSafeInteger(row.deviceResultCode, `${path}.deviceResultCode`),
    detail: requireNonNegativeSafeInteger(row.detail, `${path}.detail`),
    sequence: requireNullable(row.sequence === undefined ? null : row.sequence, `${path}.sequence`, requireNonNegativeSafeInteger)
  });
}

export function decodeDeviceActionRecord(value: unknown, path = "$"): DeviceActionRecord {
  const row = requireRecord(value, path);
  return Object.freeze({
    sequence: requireNonNegativeSafeInteger(row.sequence, `${path}.sequence`),
    state: requireOneOf(row.state, `${path}.state`, ["active", "history"] as const),
    reportEntryId: requireNonEmptyString(row.reportEntryId, `${path}.reportEntryId`),
    deviceInstanceId: requireString(row.deviceInstanceId, `${path}.deviceInstanceId`),
    occurredAt: timestamp(row.occurredAt, `${path}.occurredAt`),
    resultCode: requireNonNegativeSafeInteger(row.resultCode, `${path}.resultCode`),
    detail: requireNonNegativeSafeInteger(row.detail, `${path}.detail`)
  });
}

/** Require descending sequences to keep pagination cursors valid. */
export function decodeDeviceActionRecordPage(value: unknown, path = "$"): DeviceActionRecordPage {
  const row = requireRecord(value, path);
  const records = requireArray(row.records, `${path}.records`)
    .map((item, index) => decodeDeviceActionRecord(item, `${path}.records[${index}]`));
  for (let index = 1; index < records.length; index += 1) {
    if (records[index]!.sequence >= records[index - 1]!.sequence) {
      throw new ResponseDecodeError(`${path}.records[${index}].sequence`, "descending sequence");
    }
  }
  return Object.freeze({ records: Object.freeze(records) });
}

export const deviceRestartReplyDecoder = defineResponseDecoder("device-actions.restart-reply.v1", decodeDeviceRestartReply);
export const deviceActionRecordPageDecoder = defineResponseDecoder("device-actions.records.v1", decodeDeviceActionRecordPage);

type Client = Pick<RequestClient, "request">;

/** Call after explicit confirmation; every request sends confirmed: true. */
export function restartReportDevice(client: Client, reportEntryId: string): Promise<DeviceRestartReply> {
  return client.request({
    key: `device-actions.restart.${reportEntryId}`,
    url: `/api/reports/${encodeURIComponent(reportEntryId)}/actions/restart-device`,
    fallbackError: uiText.deviceActions.restart.failed,
    decoder: deviceRestartReplyDecoder,
    request: { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ confirmed: true }) }
  });
}

/** Read newest records, or those older than beforeSequence. */
export function readDeviceActionRecords(
  client: Client,
  page: { readonly limit: number; readonly beforeSequence?: number | null },
  signal?: AbortSignal
): Promise<DeviceActionRecordPage> {
  const query = new URLSearchParams({ limit: String(page.limit) });
  if (page.beforeSequence !== undefined && page.beforeSequence !== null) query.set("beforeSequence", String(page.beforeSequence));
  return client.request({
    key: `device-actions.records.${page.beforeSequence ?? "latest"}`,
    url: `/api/device-actions/records?${query.toString()}`,
    fallbackError: uiText.deviceActions.records.readFailed,
    decoder: deviceActionRecordPageDecoder,
    signal
  });
}
