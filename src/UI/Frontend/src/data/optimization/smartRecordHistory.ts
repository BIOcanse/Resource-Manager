import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import {
  defineResponseDecoder, requireArray, requireNonEmptyString, requireNullable, requireOneOf,
  requireRecord, requireSafeInteger, requireString, ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import { uiText } from "../../text.ts";
import type { SmartRecordHistoryEntry, SmartRecordHistoryPage } from "../../types.ts";

/* Page history by descending sequence, starting at the newest when no cursor is supplied. Sequence determines identity, order and pagination; time is display-only. */
export const smartRecordRestoreResults = ["restored", "targetExited", "ownershipLost", "failed", "unknown"] as const;

/** Maximum page size; the API accepts 1..500. */
export const smartRecordHistoryMaximumLimit = 500;

const positiveSequence = (value: unknown, path: string) => {
  const sequence = requireSafeInteger(value, path);
  if (sequence < 1) throw new ResponseDecodeError(path, "positive sequence");
  return sequence;
};

function decodeEntry(value: unknown, index: number): SmartRecordHistoryEntry {
  const path = `records[${index}]`;
  const row = requireRecord(value, path);
  if (row.state !== "history") throw new ResponseDecodeError(`${path}.state`, "history");
  return {
    sequence: positiveSequence(row.sequence, `${path}.sequence`),
    state: "history",
    kind: requireString(row.kind, `${path}.kind`),
    recordId: requireNonEmptyString(row.recordId, `${path}.recordId`),
    targetId: requireString(row.targetId, `${path}.targetId`),
    softwareId: requireNullable(row.softwareId, `${path}.softwareId`, requireString),
    resourceKind: requireString(row.resourceKind, `${path}.resourceKind`),
    appliedAt: requireString(row.appliedAt, `${path}.appliedAt`),
    processId: requireNullable(row.processId, `${path}.processId`, requireSafeInteger),
    processStartKey: requireNullable(row.processStartKey, `${path}.processStartKey`, requireString),
    processName: requireNullable(row.processName, `${path}.processName`, requireString),
    recordedAt: requireString(row.recordedAt, `${path}.recordedAt`),
    restoredAt: requireString(row.restoredAt, `${path}.restoredAt`),
    restoreResult: requireOneOf(row.restoreResult, `${path}.restoreResult`, smartRecordRestoreResults)
  };
}

export const smartRecordHistoryDecoder = defineResponseDecoder<SmartRecordHistoryPage>(
  "smart.records.history.v1", (value) => {
    const page = requireRecord(value);
    const records = requireArray(page.records, "records").map(decodeEntry);
    // Sequences within a page must be unique and strictly descending.
    for (let index = 1; index < records.length; index += 1) {
      if (records[index].sequence >= records[index - 1].sequence) {
        throw new ResponseDecodeError(`records[${index}].sequence`, "strictly descending sequence");
      }
    }
    return {
      records,
      nextBeforeSequence: requireNullable(page.nextBeforeSequence, "nextBeforeSequence", positiveSequence)
    };
  });

export function getSmartRecordHistory(
  client: Pick<RequestClient, "request">,
  query: { beforeSequence: number | null; limit: number },
  signal?: AbortSignal
): Promise<SmartRecordHistoryPage> {
  const search = new URLSearchParams({ limit: String(query.limit) });
  if (query.beforeSequence !== null) search.set("beforeSequence", String(query.beforeSequence));
  return client.request({
    key: "smart.records.history",
    url: `/api/optimization/smart/records/history?${search}`,
    fallbackError: uiText.smartReport.history.readFailed,
    decoder: smartRecordHistoryDecoder,
    signal
  });
}
