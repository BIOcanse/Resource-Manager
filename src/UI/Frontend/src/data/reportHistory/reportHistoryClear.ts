import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import {
  defineResponseDecoder, requireNonNegativeSafeInteger, requireRecord
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import { uiText } from "../../text.ts";

/*
 * 清空历史条目（docs/report_lifecycle_2026-10-28.md“一键清空历史”）：每类报告一个接口，一次删除这类报告里
 * 全部 history 条目（定向录制是全部已完成的录制），active 不动；回复是删除的条数 { "removedCount": <整数> }。
 */
export const reportHistoryKinds = ["optimizationReports", "kernelLeaks", "targetedRecordings", "smartRecords"] as const;
export type ReportHistoryKind = typeof reportHistoryKinds[number];

export const reportHistoryClearPaths: Readonly<Record<ReportHistoryKind, string>> = {
  optimizationReports: "/api/optimization/reports/history/clear",
  kernelLeaks: "/api/kernel-leaks/history/clear",
  targetedRecordings: "/api/targeted-recordings/history/clear",
  smartRecords: "/api/optimization/smart/records/history/clear"
};

export const reportHistoryClearDecoder = defineResponseDecoder<number>("report-history.clear.v1", (value) =>
  requireNonNegativeSafeInteger(requireRecord(value).removedCount, "removedCount"));

/** 发出清空请求，返回后端删除的条数。 */
export function clearReportHistory(client: Pick<RequestClient, "request">, kind: ReportHistoryKind): Promise<number> {
  return client.request({
    key: `report-history.clear.${kind}`,
    url: reportHistoryClearPaths[kind],
    fallbackError: uiText.reportHistory.failed,
    decoder: reportHistoryClearDecoder,
    request: { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" }
  });
}
