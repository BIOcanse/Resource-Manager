import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import type { OptimizationReportOverview } from "../../types.ts";
import { optimizationReportsDecoder } from "./optimizationReportsDecoder.ts";
import { uiText } from "../../text.ts";

type OptimizationRequestClient = Pick<RequestClient, "request">;

export function getOptimizationReports(
  requestClient: OptimizationRequestClient,
  signal?: AbortSignal
): Promise<OptimizationReportOverview> {
  return requestClient.request({
    key: "optimization.reports",
    url: "/api/optimization/reports",
    fallbackError: uiText.apiError.readOptimizationReportsFailed,
    decoder: optimizationReportsDecoder,
    signal,
    request: { method: "GET" }
  });
}

export function refreshOptimizationReports(
  requestClient: OptimizationRequestClient,
  signal?: AbortSignal
): Promise<OptimizationReportOverview> {
  return requestClient.request({
    key: "optimization.reports.refresh",
    url: "/api/optimization/reports/refresh",
    fallbackError: uiText.stores.optimizationReportRefreshFailed,
    decoder: optimizationReportsDecoder,
    signal,
    request: {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: "{}"
    }
  });
}
