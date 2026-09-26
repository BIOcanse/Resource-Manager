import {
  decodeResourceBreakdownWireSnapshot,
  decodeResourceMonitorWireSnapshot
} from "../../api/resourceBreakdownWire.ts";
import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import { defineResponseDecoder } from "../../frontendRuntime/request/ResponseDecoder.ts";
import type {
  ResourceBreakdownSnapshot,
  ResourceMonitorSnapshot,
  ResourceScaleMode,
  ResourceTableViewMode
} from "../../types.ts";
import { uiText } from "../../text.ts";

type ResourceMonitorRequestClient = Pick<RequestClient, "request">;

export interface ResourceBarQuery {
  readonly metricId: string;
  readonly scaleMode: ResourceScaleMode;
}

export interface ResourceBreakdownQuery {
  readonly bars: readonly ResourceBarQuery[] | null;
  readonly processDetailSoftwareIds: readonly string[];
}

export interface ResourceMonitorQuery extends ResourceBreakdownQuery {
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
  return appendQuery("/api/resource-breakdown/snapshot", params);
}

export function buildResourceMonitorUrl(query: ResourceMonitorQuery): string {
  return buildResourceMonitorUrlForPath(
    "/api/resource-monitor/snapshot",
    query);
}

export function buildResourceMonitorSubscriptionUrl(
  query: ResourceMonitorQuery,
  intervalMs: number
): string {
  const params = buildResourceMonitorParams(query);
  params.set("intervalMs", String(Math.max(1, Math.round(intervalMs))));
  return appendQuery("/api/resource-monitor/subscribe", params);
}

function buildResourceMonitorUrlForPath(
  path: string,
  query: ResourceMonitorQuery
): string {
  return appendQuery(path, buildResourceMonitorParams(query));
}

function buildResourceMonitorParams(query: ResourceMonitorQuery): URLSearchParams {
  const params = new URLSearchParams();
  appendBars(params, query.bars);
  appendValues(params, "sampleIds", query.sampleMetricIds);
  appendValues(params, "columns", query.visibleColumnIds);
  const scope = query.scope === "bars" || query.scope === "table"
    ? query.scope
    : "combined";
  params.set("scope", scope);
  params.set(
    "sort",
    `${query.sortColumnId}:${query.sortDirection === "asc" ? "asc" : "desc"}`);
  params.set("mode", query.tableMode === "process" ? "process" : "software");
  if (scope !== "bars") {
    params.set("includeProcesses", "all");
  }
  appendValues(params, "processDetails", query.processDetailSoftwareIds);
  return params;
}

export function normalizeResourceMonitorQuery(
  query: ResourceMonitorQuery
): ResourceMonitorQuery {
  const bars = query.bars === null
    ? null
    : Object.freeze(
      [...new Map(query.bars.map((bar) => [
        bar.metricId.trim().toLocaleLowerCase(),
        Object.freeze({
          metricId: bar.metricId.trim(),
          scaleMode: bar.scaleMode
        })
      ])).values()]
        .sort((left, right) => left.metricId.localeCompare(right.metricId)));
  return Object.freeze({
    bars,
    scope: query.scope === "bars" || query.scope === "table"
      ? query.scope
      : "combined",
    sampleMetricIds: normalizeSet(query.sampleMetricIds),
    visibleColumnIds: normalizeOrderedSet(query.visibleColumnIds),
    sortColumnId: query.sortColumnId.trim(),
    sortDirection: query.sortDirection === "asc" ? "asc" : "desc",
    processDetailSoftwareIds:
      normalizeSet(query.processDetailSoftwareIds) ?? [],
    tableMode: query.tableMode
  });
}

function appendBars(
  params: URLSearchParams,
  bars: readonly ResourceBarQuery[] | null
): void {
  if (bars === null) {
    return;
  }
  if (bars.length === 0) {
    params.append("ids", "");
    return;
  }
  for (const bar of bars) {
    params.append("ids", bar.metricId);
    params.append("modes", `${bar.metricId}:${bar.scaleMode}`);
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
