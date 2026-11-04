import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import {
  defineResponseDecoder, requireNonNegativeSafeInteger, requireRecord
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import { uiText } from "../../text.ts";

/* Each endpoint clears history entries, or completed recordings, while preserving active entries. The response reports removedCount. */
export const reportHistoryKinds = ["optimizationReports", "kernelLeaks", "targetedRecordings", "smartRecords", "memoryProtection",
  "systemEvents", "freezeReviews", "deviceActions", "cpuMonopolyProtection"] as const;
export type ReportHistoryKind = typeof reportHistoryKinds[number];

export const reportHistoryClearPaths: Readonly<Record<ReportHistoryKind, string>> = {
  optimizationReports: "/api/optimization/reports/history/clear",
  kernelLeaks: "/api/kernel-leaks/history/clear",
  targetedRecordings: "/api/targeted-recordings/history/clear",
  smartRecords: "/api/optimization/smart/records/history/clear",
  memoryProtection: "/api/memory-protection/records/history/clear",
  systemEvents: "/api/system-events/history/clear",
  freezeReviews: "/api/freeze-recorder/reviews/history/clear",
  deviceActions: "/api/device-actions/records/history/clear",
  cpuMonopolyProtection: "/api/cpu-monopoly-protection/records/history/clear"
};

export const reportHistoryClearDecoder = defineResponseDecoder<number>("report-history.clear.v1", (value) =>
  requireNonNegativeSafeInteger(requireRecord(value).removedCount, "removedCount"));

/** Clear history and return the backend removal count. */
export function clearReportHistory(client: Pick<RequestClient, "request">, kind: ReportHistoryKind): Promise<number> {
  return client.request({
    key: `report-history.clear.${kind}`,
    url: reportHistoryClearPaths[kind],
    fallbackError: uiText.reportHistory.failed,
    decoder: reportHistoryClearDecoder,
    request: { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" }
  });
}
