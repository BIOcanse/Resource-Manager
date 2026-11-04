import { readDashboardPreferences } from "../../preferences/dashboardPreferences.ts";
import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import type {
  DashboardSettingsResult,
  MetricDefinition,
  MetricSnapshot
} from "../../types.ts";
import {
  metricCatalogDecoder,
  metricSnapshotDecoder
} from "./monitorSourceDecoders.ts";
import { uiText } from "../../text.ts";

type MonitorRequestClient = Pick<RequestClient, "request">;

export interface MetricSnapshotQuery {
  readonly ids: readonly string[] | null;
}

export function normalizeMetricSnapshotQuery(
  query: MetricSnapshotQuery
): MetricSnapshotQuery {
  if (query.ids === null) {
    return Object.freeze({ ids: null });
  }
  return Object.freeze({
    ids: Object.freeze(Array.from(new Set(
      query.ids.map((id) => id.trim()).filter(Boolean))).sort())
  });
}

export function getMetricCatalogSource(
  requestClient: MonitorRequestClient,
  signal?: AbortSignal
): Promise<MetricDefinition[]> {
  return requestClient.request({
    key: "monitor.metric-catalog",
    url: "/api/metrics/catalog",
    fallbackError: uiText.misc.metricCatalogRefreshFailed,
    decoder: metricCatalogDecoder,
    signal,
    request: { method: "GET" }
  });
}

export function getDashboardSettingsSource(
  requestClient: MonitorRequestClient,
  signal?: AbortSignal
): Promise<DashboardSettingsResult> {
  return getMetricCatalogSource(requestClient, signal).then(catalog => readDashboardPreferences(catalog));
}

export function getMetricSnapshotSource(
  requestClient: MonitorRequestClient,
  query: MetricSnapshotQuery,
  signal?: AbortSignal
): Promise<MetricSnapshot> {
  const normalized = normalizeMetricSnapshotQuery(query);
  const params = new URLSearchParams();
  appendMetricIds(params, normalized.ids);
  const suffix = params.size > 0 ? `?${params.toString()}` : "";
  return requestClient.request({
    key: "monitor.metric-snapshot",
    url: `/api/metrics/snapshot${suffix}`,
    fallbackError: uiText.misc.liveMetricRefreshFailed,
    decoder: metricSnapshotDecoder,
    signal,
    request: { method: "GET" }
  });
}

export function buildMetricSnapshotSubscriptionUrl(
  query: MetricSnapshotQuery,
  intervalMs: number
): string {
  const normalized = normalizeMetricSnapshotQuery(query);
  const params = new URLSearchParams();
  appendMetricIds(params, normalized.ids);
  params.set("intervalMs", String(Math.max(1, Math.round(intervalMs))));
  return `/api/metrics/subscribe?${params.toString()}`;
}

function appendMetricIds(
  params: URLSearchParams,
  ids: readonly string[] | null
): void {
  if (ids === null) {
    return;
  }
  if (ids.length === 0) {
    params.append("ids", "");
    return;
  }
  ids.forEach((id) => params.append("ids", id));
}
