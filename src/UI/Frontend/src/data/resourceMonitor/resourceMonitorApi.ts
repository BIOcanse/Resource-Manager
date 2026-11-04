import {
  decodeResourceBreakdownWireSnapshot,
  decodeResourceMonitorWireSnapshot,
  decodeResourceProcessDetailsWireSnapshot
} from "../../api/resourceBreakdownWire.ts";
import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import { defineResponseDecoder } from "../../frontendRuntime/request/ResponseDecoder.ts";
import type {
  ResourceBreakdownSnapshot,
  ResourceMonitorSnapshot,
  ResourceProcessDetailsSnapshot,
  ResourceScaleMode,
  ResourceTableViewMode
} from "../../types.ts";
import { uiText } from "../../text.ts";

type ResourceMonitorRequestClient = Pick<RequestClient, "request">;

export interface ResourceBarQuery {
  readonly metricId: string;
  readonly scaleMode: ResourceScaleMode;
  /** Explicit backend process field: subscribe through measures and field-specific modes instead of ids. Null uses metricId. */
  readonly source?: string | null;
}

export interface ResourceBreakdownQuery {
  readonly bars: readonly ResourceBarQuery[] | null;
  /** Opaque backend software ID for process-detail rows. */
  readonly processDetailSoftwareIds: readonly string[];
  /** Select residual details only from requested bars and measures, adding no sampling demand. Empty omits the parameter. */
  readonly residualDetailMetricIds: readonly string[];
}

export interface ResourceMonitorQuery extends ResourceBreakdownQuery {
  readonly processDetailLevel?: "basic" | "full";
  readonly scope?: "bars" | "table" | "combined";
  readonly sampleMetricIds: readonly string[] | null;
  readonly visibleColumnIds: readonly string[] | null;
  readonly sortColumnId: string;
  readonly sortDirection: "asc" | "desc";
  readonly tableMode: ResourceTableViewMode;
}

export const resourceBreakdownDecoder = defineResponseDecoder<ResourceBreakdownSnapshot>(
  "resource-breakdown.snapshot.v3",
  decodeResourceBreakdownWireSnapshot);

export const resourceMonitorDecoder = defineResponseDecoder<ResourceMonitorSnapshot>(
  "resource-monitor.snapshot.v3",
  decodeResourceMonitorWireSnapshot);

export const resourceProcessDetailsDecoder = defineResponseDecoder<ResourceProcessDetailsSnapshot>(
  "resource-monitor.process-details.v1",
  decodeResourceProcessDetailsWireSnapshot);

export function buildResourceProcessDetailsSubscriptionUrl(
  query: ResourceBreakdownQuery,
  intervalMs: number
): string {
  const params = new URLSearchParams();
  params.set("scope", "details");
  appendBars(params, query.bars);
  appendValues(params, "processDetails", query.processDetailSoftwareIds);
  appendResidualDetails(params, query.residualDetailMetricIds);
  params.set("intervalMs", String(Math.max(1, Math.round(intervalMs))));
  return appendQuery("/api/resource-monitor/subscribe", params);
}

export function getResourceBreakdownState(
  requestClient: ResourceMonitorRequestClient,
  query: ResourceBreakdownQuery,
  signal?: AbortSignal
): Promise<ResourceBreakdownSnapshot> {
  return requestClient.request({
    key: "resource-breakdown.snapshot",
    url: buildResourceBreakdownUrl(query),
    fallbackError: uiText.misc.resourceBreakdownReadFailed,
    decoder: resourceBreakdownDecoder,
    signal,
    request: { method: "GET" }
  });
}

export function getResourceMonitorState(
  requestClient: ResourceMonitorRequestClient,
  query: ResourceMonitorQuery,
  signal?: AbortSignal
): Promise<ResourceMonitorSnapshot> {
  return requestClient.request({
    key: "resource-monitor.snapshot",
    url: buildResourceMonitorUrl(query),
    fallbackError: uiText.misc.resourceMonitorReadFailed,
    decoder: resourceMonitorDecoder,
    signal,
    request: { method: "GET" }
  });
}

export function buildResourceBreakdownUrl(query: ResourceBreakdownQuery): string {
  const params = new URLSearchParams();
  appendBars(params, query.bars);
  appendValues(params, "processDetails", query.processDetailSoftwareIds);
  appendResidualDetails(params, query.residualDetailMetricIds);
  return appendQuery("/api/resource-breakdown/snapshot", params);
}

export function buildResourceMonitorUrl(query: ResourceMonitorQuery): string {
  return buildResourceMonitorUrlForPath(
    query.scope === "table" ? "/api/resource-facts/snapshot" : "/api/resource-monitor/snapshot",
    query);
}

export function buildResourceMonitorSubscriptionUrl(
  query: ResourceMonitorQuery,
  intervalMs: number
): string {
  const params = buildResourceMonitorParams(query);
  params.set("intervalMs", String(Math.max(1, Math.round(intervalMs))));
  return appendQuery(query.scope === "table" ? "/api/resource-facts/subscribe" : "/api/resource-monitor/subscribe", params);
}

function buildResourceMonitorUrlForPath(
  path: string,
  query: ResourceMonitorQuery
): string {
  return appendQuery(path, buildResourceMonitorParams(query));
}

function buildResourceMonitorParams(query: ResourceMonitorQuery): URLSearchParams {
  const params = new URLSearchParams();
  if (query.scope === "table") {
    appendValues(params, "items", query.sampleMetricIds);
    params.set("processDetailLevel", query.processDetailLevel ?? "basic");
    return params;
  }
  appendBars(params, query.bars);
  const scope = query.scope === "bars"
    ? query.scope
    : "combined";
  params.set("scope", scope);
  appendValues(params, "processDetails", query.processDetailSoftwareIds);
  appendResidualDetails(params, query.residualDetailMetricIds);
  return params;
}

export function normalizeResourceMonitorQuery(
  query: ResourceMonitorQuery
): ResourceMonitorQuery {
  const breakdown = normalizeResourceBreakdownQuery(query);
  if (query.scope === "table") {
    return Object.freeze({ bars: [], scope: "table", processDetailLevel: query.processDetailLevel ?? "basic", sampleMetricIds: normalizeSet(query.sampleMetricIds),
      visibleColumnIds: [], sortColumnId: "", sortDirection: "desc", processDetailSoftwareIds: [], residualDetailMetricIds: [], tableMode: "software" });
  }
  return Object.freeze({
    bars: breakdown.bars,
    scope: query.scope === "bars"
      ? query.scope
      : "combined",
    sampleMetricIds: normalizeSet(query.sampleMetricIds),
    visibleColumnIds: normalizeOrderedSet(query.visibleColumnIds),
    sortColumnId: query.sortColumnId.trim(),
    sortDirection: query.sortDirection === "asc" ? "asc" : "desc",
    processDetailSoftwareIds: breakdown.processDetailSoftwareIds,
    residualDetailMetricIds: breakdown.residualDetailMetricIds,
    tableMode: query.tableMode
  });
}

export function normalizeResourceBreakdownQuery(query: ResourceBreakdownQuery): ResourceBreakdownQuery {
  const bars = query.bars === null
    ? null
    : Object.freeze(
      [...new Map(query.bars.map((bar) => {
        const source = bar.source?.trim() || null;
        // Deduplicate explicit measures separately from metric-ID bars.
        return [source ? `measure:${source}` : bar.metricId.trim().toLocaleLowerCase(), Object.freeze({
          metricId: bar.metricId.trim(),
          scaleMode: bar.scaleMode,
          source
        })] as const;
      })).values()]
        .sort((left, right) => (left.source ?? "").localeCompare(right.source ?? "") || left.metricId.localeCompare(right.metricId)));
  return Object.freeze({
    bars,
    processDetailSoftwareIds:
      normalizeSet(query.processDetailSoftwareIds) ?? [],
    residualDetailMetricIds:
      normalizeSet(query.residualDetailMetricIds) ?? []
  });
}

function appendBars(
  params: URLSearchParams,
  bars: readonly ResourceBarQuery[] | null
): void {
  if (bars === null) {
    return;
  }
  const byId = bars.filter((bar) => !bar.source);
  // Send empty ids explicitly when no metric-ID bars are selected, preventing backend defaults.
  if (byId.length === 0) {
    params.append("ids", "");
  }
  for (const bar of byId) {
    params.append("ids", bar.metricId);
    params.append("modes", `${bar.metricId}:${bar.scaleMode}`);
  }
  for (const bar of bars) {
    if (!bar.source) continue;
    params.append("measures", bar.source);
    params.append("modes", `${bar.source}:${bar.scaleMode}`);
  }
}

function appendValues(
  params: URLSearchParams,
  name: string,
  values: readonly string[] | null
): void {
  if (values === null) {
    return;
  }
  if (values.length === 0) {
    params.append(name, "");
    return;
  }
  for (const value of values) {
    params.append(name, value);
  }
}

function appendResidualDetails(params: URLSearchParams, metricIds: readonly string[]): void {
  for (const metricId of metricIds) {
    params.append("residualDetails", metricId);
  }
}

function appendQuery(path: string, params: URLSearchParams): string {
  const query = params.toString();
  return query ? `${path}?${query}` : path;
}

function normalizeSet(values: readonly string[] | null): readonly string[] | null {
  return values === null
    ? null
    : Object.freeze([...new Set(
      values.map((value) => value.trim()).filter(Boolean))]
      .sort((left, right) => left.localeCompare(right)));
}

function normalizeOrderedSet(
  values: readonly string[] | null
): readonly string[] | null {
  return values === null
    ? null
    : Object.freeze([...new Set(
      values.map((value) => value.trim()).filter(Boolean))]);
}
