import { readSlice, selectorKey, matchSelector, resolveSliceFilePath, type SliceEvent, type SliceResponseEvent } from "./sliceFormat.ts";
import { deriveProductionReadPlan, type ObservationReadPlan, type Catalogs } from "./productionReadPlan.ts";
import { stat } from "node:fs/promises";

export type ReplayEvent = Pick<SliceEvent, "kind" | "atMs" | "path" | "body">
  & Partial<Pick<SliceResponseEvent, "status" | "contentType" | "encoding">>;
export type ReplayTimer = ReturnType<typeof setTimeout> | number;
export interface ReplayInput { events: ReplayEvent[]; durationMs?: number }
export interface SliceReplay {
  response(path: string): ReplayEvent | null;
  frames(path: string): ReplayEvent[];
  subscribe(path: string, publish: (body: unknown) => void,
    schedule?: (callback: () => void, delay: number) => ReplayTimer,
    cancel?: (timer: ReplayTimer) => void): () => void;
}

// A selector is covered only by the same query (ignoring requested cadence).
// Known list selectors can consume a recorded superset; payload stays untouched.
export function coversSelector(recorded: string, requested: string) {
  return matchSelector(recorded, requested) !== null;
}

function coversFieldPayload(event: ReplayEvent, path: string) {
  const recorded = new URL(event.path, "http://localhost"), requested = new URL(path, "http://localhost");
  if (recorded.pathname !== "/api/metrics/subscribe" || requested.pathname !== recorded.pathname
    || !recorded.searchParams.has("fields") || !requested.searchParams.has("fields") || !Array.isArray(event.body)) return false;
  if ([...requested.searchParams.keys()].some(key => !["fields", "device", "intervalMs"].includes(key))) return false;
  const devices = requested.searchParams.getAll("device"), groups = requested.searchParams.getAll("fields");
  if (devices.length > 1 && devices.length !== groups.length) return false;
  const selections = devices.length > 1 ? devices.map((device, index) => ({ device, fields: groups[index] }))
    : [{ device: devices[0] ?? null, fields: groups.join(",") }];
  return selections.every(selection => {
    const fields = selection.fields.split(",").filter(Boolean);
    return fields.length > 0 && fields.every(name => (event.body as { deviceIdentity?: unknown; fields?: { name?: unknown }[] }[]).some(device =>
      (selection.device === null || device.deviceIdentity === selection.device) && device.fields?.some(field => field.name === name)));
  });
}

export function createSliceReplay(slice: ReplayInput, clock: () => number = () => performance.now(), observation: ObservationReadPlan | null = null): SliceReplay {
  const started = clock();
  const events = slice.events.toSorted((a, b) => a.atMs - b.atMs);
  const routes = observation ? new Set([...observation.reads, ...observation.subscriptions].map(path => new URL(path, "http://localhost").pathname)) : null;
  const groups = new Map<string, ReplayEvent[]>();
  for (const event of events) {
    if (routes && !routes.has(new URL(event.path, "http://localhost").pathname)) continue;
    const key = `${event.kind}:${event.path}`;
    const group = groups.get(key) ?? [];
    group.push(event); groups.set(key, group);
  }
  const selectedQueries = new Map<string, ReplayEvent[]>();
  const select = (kind: ReplayEvent["kind"], path: string): ReplayEvent[] => {
    const cacheKey = `${kind}:${path}`;
    if (selectedQueries.has(cacheKey)) return selectedQueries.get(cacheKey)!;
    const matching = [...groups.values()].filter(group => group[0].kind === kind && (coversSelector(group[0].path, path)
      || (kind === "frame" && group.some(event => coversFieldPayload(event, path)))));
    if (!matching.length) return [];
    // Prefer an exact query; never mix frames from several recorded subscriptions.
    const selected = matching.find(group => selectorKey(group[0].path) === selectorKey(path)) ?? matching[0];
    selectedQueries.set(cacheKey, selected);
    return selected;
  };
  return {
    response(path) {
      const matching = select("response", path);
      if (!matching.length) return null;
      const elapsed = clock() - started;
      return matching.findLast(event => event.atMs <= elapsed) ?? matching[0];
    },
    frames(path) { return select("frame", path); },
    subscribe(path, publish, schedule = setTimeout, cancel = clearTimeout) {
      const frames = select("frame", path);
      const elapsed = clock() - started;
      const current = frames.findLast(frame => frame.atMs <= elapsed);
      if (current) publish(current.body);
      const timers = frames.filter(frame => frame.atMs > elapsed)
        .map(frame => schedule(() => publish(frame.body), frame.atMs - elapsed));
      return () => { for (const timer of timers) cancel(timer); };
    }
  };
}

let cachedInput: { key: string; value: Promise<{ slice: ReplayInput; observation: ObservationReadPlan }> } | undefined;
export async function loadSliceReplay(path: string | undefined | null) {
  if (!path) return null;
  const file = resolveSliceFilePath(path), info = await stat(file);
  const key = `${file}:${info.size}:${info.mtimeMs}`;
  if (cachedInput?.key !== key) cachedInput = { key, value: (async () => {
    const slice = await readSlice(file);
    type CatalogFrame = { catalogRevision?: unknown; metrics: NonNullable<Catalogs["metrics"]>; fields?: unknown };
    const catalog = slice.events.find(event => event.kind === "frame" && (event.body as CatalogFrame | null)?.catalogRevision && Array.isArray((event.body as CatalogFrame).metrics))?.body as CatalogFrame | undefined;
    const plan = await deriveProductionReadPlan(1000, catalog ? { metrics: catalog.metrics, metricFields: catalog.fields } : null);
    const observation = plan.categories.observation;
    const routes = new Set([...observation.reads, ...observation.subscriptions].map(path => new URL(path, "http://localhost").pathname));
    // Warm-up and journey-owned payloads are not replay inputs. Retain one parsed
    // observation dataset per worker, rather than parsing 275 MB on every reset.
    return { slice: { durationMs: slice.durationMs, events: slice.events.filter(event => routes.has(new URL(event.path, "http://localhost").pathname)) }, observation };
  })() };
  const { slice, observation } = await cachedInput.value;
  return createSliceReplay(slice, () => performance.now(), observation);
}
