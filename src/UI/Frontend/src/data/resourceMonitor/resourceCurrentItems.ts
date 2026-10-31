import type { PushValueItem, PushValueItemPolicy } from "../../frontendRuntime/push/PushCurrentItems.ts";
import type { ResourceFactsMonitorSnapshot, ResourceMonitorSnapshot, ResourceProcessDetailsSnapshot } from "../../types.ts";
import type { ResourceBarQuery, ResourceBreakdownQuery, ResourceMonitorQuery } from "./resourceMonitorApi.ts";

function barKey(metricId: string, scaleMode: string): string {
  return JSON.stringify([metricId, scaleMode.toLowerCase()]);
}

function requestedBar(query: ResourceBreakdownQuery, metricId: string): ResourceBarQuery | undefined {
  return query.bars?.find(bar => (bar.source || bar.metricId) === metricId);
}

function includesBar(query: ResourceBreakdownQuery, key: string): boolean {
  const [metricId, scaleMode] = JSON.parse(key) as [string, string];
  const bar = requestedBar(query, metricId);
  return (query.bars === null || bar?.scaleMode.toLowerCase() === scaleMode) && key === barKey(metricId, scaleMode);
}

function identityKey(query: ResourceMonitorQuery): string {
  return `identities:${query.processDetailLevel ?? "basic"}`;
}

function needsIdentities(query: ResourceMonitorQuery): boolean {
  return query.sampleMetricIds === null || query.sampleMetricIds.some(id => id === "process.inventory" || id === "process.attribution");
}

function factKeys(query: ResourceMonitorQuery): string[] {
  return (query.sampleMetricIds ?? []).filter(id => id !== "process.inventory" && id !== "process.attribution").map(id => "facts:" + id);
}

/** Each metric is atomic with its own totals, window and software/process rows. */
export const resourceMonitorCurrentItems: PushValueItemPolicy<ResourceMonitorQuery, ResourceMonitorSnapshot> = {
  // Numeric items have backend unavailable payloads; only identities form a replaceable set.
  absentMeansEmpty: (_query, key) => key.startsWith("identities:"),
  includes: (query, key) => {
    if (query.scope === "table") {
      if (key.startsWith("identities:")) return key === identityKey(query) && needsIdentities(query);
      return key.startsWith("facts:") && (query.sampleMetricIds === null || query.sampleMetricIds.includes(key.slice(6)));
    }
    return key.startsWith("bar:") && includesBar(query, key.slice(4));
  },
  isPending: (query, keys) => query.scope === "table"
    ? factKeys(query).some(key => !keys.has(key)) || (needsIdentities(query) && !keys.has(identityKey(query)))
    : query.bars?.some(bar => !keys.has("bar:" + barKey(bar.source || bar.metricId, bar.scaleMode))) ?? false,
  split: (query, value) => {
    // A frame of the other scope has none of this query's items.
    if (query.scope === "table") {
      if (value.scope !== "table") return [];
      const facts = value.facts;
      const fields = new Map((facts.fields ?? []).map(field => [field.field, field]));
      const items: PushValueItem<ResourceMonitorSnapshot>[] = facts.metrics.map(metric => ({
        key: "facts:" + metric.metricId, capturedAt: metric.capturedAt ?? facts.capturedAt ?? null,
        value: { ...value, facts: { capturedAt: facts.capturedAt, metrics: [metric],
          fields: fields.has(metric.metricId) ? [fields.get(metric.metricId)!] : [] } }
      }));
      const metricIds = new Set(facts.metrics.map(metric => metric.metricId));
      for (const field of fields.values()) if (!metricIds.has(field.field)) {
        items.push({ key: "facts:" + field.field, capturedAt: field.capturedAt ?? facts.capturedAt ?? null,
          value: { ...value, facts: { capturedAt: facts.capturedAt, metrics: [], fields: [field] } } });
      }
      if (needsIdentities(query)) {
        items.push({ key: identityKey(query), capturedAt: facts.capturedAt ?? null,
          value: { ...value, facts: { capturedAt: facts.capturedAt, identities: facts.identities, metrics: [] } } });
      }
      return items;
    }
    if (value.scope !== "bars") return [];
    const items: PushValueItem<ResourceMonitorSnapshot>[] = (value.breakdown.bars ?? []).map(bar => ({
      key: "bar:" + barKey(bar.metricId, bar.scaleMode), capturedAt: bar.capturedAt ?? value.capturedAt ?? null,
      value: { ...value, breakdown: { ...value.breakdown, bars: [bar] } }
    }));
    return items;
  },
  // Items come only from split, which keeps each query's own scope, so every value here has latest's scope.
  combine: (_query, values, latest) => latest.scope === "table"
    ? { ...latest, facts: { capturedAt: latest.facts.capturedAt,
      metrics: values.flatMap(value => value.scope === "table" ? value.facts.metrics : []),
      fields: values.flatMap(value => value.scope === "table" ? value.facts.fields ?? [] : []),
      identities: values.find((value): value is ResourceFactsMonitorSnapshot => value.scope === "table"
        && value.facts.identities !== undefined)?.facts.identities ?? [] } }
    : { ...latest, breakdown: { ...latest.breakdown,
      bars: values.flatMap(value => value.scope === "bars" ? value.breakdown.bars ?? [] : []) } }
};

// Details have their own sampling contract, never borrowed from bars or table facts.
function detailKey(kind: "metric" | "software" | "residual", metricId: string, scaleMode: string, softwareId: string | null = null): string {
  return JSON.stringify([kind, metricId, scaleMode.toLowerCase(), softwareId]);
}

function detailMetricKeys(query: ResourceBreakdownQuery, keys: ReadonlySet<string>): [string, string][] {
  return query.bars === null ? [...keys].filter(key => key !== "physical-memory-residual")
    .map(key => JSON.parse(key) as [string, string, string]).filter(([kind]) => kind === "metric")
    .map(([, metricId, scale]) => [metricId, scale])
    : query.bars.map(bar => [bar.source || bar.metricId, bar.scaleMode]);
}

export const resourceDetailsCurrentItems: PushValueItemPolicy<ResourceBreakdownQuery, ResourceProcessDetailsSnapshot> = {
  absentMeansEmpty: () => true,
  includes: (query, key) => {
    if (key === "physical-memory-residual") return query.residualDetailMetricIds.includes("memory.usage");
    const [kind, metricId, scale, softwareId] = JSON.parse(key) as [string, string, string, string];
    return includesBar(query, barKey(metricId, scale)) && (kind === "metric"
      || (kind === "software" && query.processDetailSoftwareIds.includes(softwareId))
      || (kind === "residual" && (query.residualDetailMetricIds.includes(metricId)
        || query.residualDetailMetricIds.includes(softwareId))));
  },
  isPending: (query, keys, readItem) => detailMetricKeys(query, keys).some(([id, scale]) => {
    const header = detailKey("metric", id, scale);
    if (!keys.has(header)) return true;
    const totalMetricId = readItem(header)?.metrics[0]?.totalMetricId ?? null;
    return query.processDetailSoftwareIds.some(softwareId => !keys.has(detailKey("software", id, scale, softwareId)))
      || ((query.residualDetailMetricIds.includes(id) || (totalMetricId !== null && query.residualDetailMetricIds.includes(totalMetricId)))
        && !keys.has(detailKey("residual", id, scale, totalMetricId)));
  }),
  split: (query, value) => {
    const items: PushValueItem<ResourceProcessDetailsSnapshot>[] = [];
    for (const metric of value.metrics) {
      const scale = requestedBar(query, metric.metricId)?.scaleMode ?? "capacity";
      const capturedAt = metric.capturedAt ?? value.capturedAt ?? null;
      items.push({ key: detailKey("metric", metric.metricId, scale), capturedAt,
        value: { capturedAt: value.capturedAt, metrics: [{ ...metric, software: [] }] } });
      for (const softwareId of query.processDetailSoftwareIds) {
        const software = metric.software.find(item => item.softwareId === softwareId);
        items.push({ key: detailKey("software", metric.metricId, scale, softwareId), capturedAt,
          value: { capturedAt: value.capturedAt, metrics: software ? [{ ...metric, software: [software] }] : [] } });
      }
      if (query.residualDetailMetricIds.includes(metric.metricId)
        || (metric.totalMetricId !== null && query.residualDetailMetricIds.includes(metric.totalMetricId))) {
        const software = metric.software.filter(item => !query.processDetailSoftwareIds.includes(item.softwareId));
        items.push({ key: detailKey("residual", metric.metricId, scale, metric.totalMetricId), capturedAt,
          value: { capturedAt: value.capturedAt, metrics: software.length ? [{ ...metric, software }] : [] } });
      }
    }
    for (const bar of query.bars ?? []) {
      const id = bar.source || bar.metricId;
      if (value.metrics.some(metric => metric.metricId === id)) continue;
      const keys = [detailKey("metric", id, bar.scaleMode),
        ...query.processDetailSoftwareIds.map(softwareId => detailKey("software", id, bar.scaleMode, softwareId)),
        ...(query.residualDetailMetricIds.includes(id) ? [detailKey("residual", id, bar.scaleMode)] : [])];
      for (const key of keys) items.push({ key, capturedAt: value.capturedAt ?? null,
        value: { capturedAt: value.capturedAt, metrics: [] } });
    }
    if (value.physicalMemoryResidual) items.push({ key: "physical-memory-residual", capturedAt: value.physicalMemoryResidual.capturedAt,
      value: { capturedAt: value.capturedAt, physicalMemoryResidual: value.physicalMemoryResidual, metrics: [] } });
    return items;
  },
  combine: (_query, values, latest) => {
    const metrics = new Map<string, ResourceProcessDetailsSnapshot["metrics"][number]>();
    for (const value of values) for (const metric of value.metrics) {
      if (metric.software.length === 0) metrics.set(metric.metricId, { ...metric, software: [] });
    }
    for (const value of values) for (const metric of value.metrics) {
      const previous = metrics.get(metric.metricId);
      if (!previous || metric.software.length === 0) continue;
      metrics.set(metric.metricId, { ...previous,
        software: [...previous?.software ?? [], ...metric.software] });
    }
    return { capturedAt: latest.capturedAt, metrics: [...metrics.values()],
      physicalMemoryResidual: values.find(value => value.physicalMemoryResidual)?.physicalMemoryResidual };
  }
};
