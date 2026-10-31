import { mkdir, realpath, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import type { ProductionReadPlan } from "./productionReadPlan.ts";
import type { Slice, SliceEvent, SliceReadiness } from "./sliceFormat.ts";
import { deriveProductionReadPlan, assertAllowedRequest } from "./productionReadPlan.ts";
import { assertRawSlicePath } from "./sliceFormat.ts";
import { metricCatalogPushDecoder, type MetricCatalogPush } from "../../src/data/monitor/metricCatalogPush.ts";

export type RecorderFetch = (url: URL, options: RequestInit) => Promise<Response>;
export interface RecorderOptions {
  baseUrl: string;
  durationMs: number;
  readinessTimeoutMs?: number;
  key?: string;
  keyless?: boolean;
  fetchImpl?: RecorderFetch;
  output?: string;
}
type UntimedSliceEvent = SliceEvent extends infer Event
  ? Event extends SliceEvent ? Omit<Event, "atMs"> : never
  : never;
type AppendEvent = (event: UntimedSliceEvent) => void;
type RecorderRequest = (plan: ProductionReadPlan, method: string, path: string, body?: unknown) => Promise<Response>;
type FrameHandler = (path: string, body: unknown) => boolean;
type CapturedSlice = Slice & { readiness: SliceReadiness };
type TopologyState = { state: string };

const maximumStreamBufferLength = 32 * 1024 * 1024;

export async function recordBackendSlice(options: RecorderOptions & { output: string }) {
  validateOptions(options);
  const outputPath = resolve(options.output);
  assertRawSlicePath(outputPath);
  await mkdir(dirname(outputPath), { recursive: true });
  assertRawSlicePath(resolve(await realpath(dirname(outputPath)), "slice.json"));
  const { slice, failure } = await captureBackendSlice(options);
  await writeFile(outputPath, JSON.stringify(slice), { flag: "wx" });
  if (failure) throw failure;
  return { events: slice.events.length, output: outputPath, readinessWaitedMs: slice.readiness.waitedMs };
}

// The capture core has no filesystem side effects; tests supply recorded responses
// through fetchImpl and exercise the same requests, readiness and reconnect loop.
export async function captureBackendSlice(options: RecorderOptions): Promise<{ slice: CapturedSlice; failure: unknown }> {
  const { durationMs, key, fetchImpl = fetch, readinessTimeoutMs = 120000 } = options;
  const base = validateOptions(options);
  const initialPlan = await deriveProductionReadPlan();
  const waitStartedAt = new Date().toISOString(), waitStarted = performance.now();
  const readiness: SliceReadiness = { startedAt: waitStartedAt, timeoutMs: readinessTimeoutMs, waitedMs: 0, ready: false, events: [] };
  const events: SliceEvent[] = [], responseBodies = new Map<string | undefined, unknown>();
  let recordingStarted: number | null = null, startedAt = waitStartedAt, failure: unknown = null;
  let controller = new AbortController();
  let timer = setTimeout(() => controller.abort(), readinessTimeoutMs);
  const append: AppendEvent = event => {
    const target = recordingStarted === null ? readiness.events : events;
    if (target.length >= 200000) throw new Error("Slice frame limit exceeded");
    target.push({ atMs: Math.max(0, Math.min(recordingStarted === null ? readinessTimeoutMs : durationMs,
      performance.now() - (recordingStarted ?? waitStarted))), ...event });
  };
  const request: RecorderRequest = async (plan, method, path, body) => {
    assertAllowedRequest(plan, method, path, body);
    return fetchImpl(new URL(path, base), {
      method, cache: "no-store", redirect: "error", signal: controller.signal,
      headers: { ...(key ? { "X-Resource-Manager-Key": key } : {}),
        ...(body ? { "Content-Type": "application/json", Accept: "application/x-ndjson" } : { Accept: "application/json" }) },
      ...(body ? { body: JSON.stringify(body) } : {})
    });
  };
  try {
    const reads = await Promise.allSettled(initialPlan.reads.map(async path => {
      const response = await request(initialPlan, "GET", path);
      const contentType = response.headers.get("content-type") ?? "";
      const { body, encoding } = parseResponseBody(await response.text(), contentType);
      append({ kind: "response", path, status: response.status, contentType, encoding, body });
      if (!response.ok) throw new Error(`Read failed: ${path} HTTP ${response.status}`);
      responseBodies.set(path, body);
    }));
    const readFailure = reads.find(result => result.status === "rejected");
    if (readFailure) throw readFailure.reason;
    if (!initialPlan.topologyPath) throw new Error("Production device-topology subscription was not discovered");
    let catalog: MetricCatalogPush | null = null, topology: TopologyState | null = null;
    await consumeUntilStopped(initialPlan, request, append, controller.signal, (path, body) => {
      if (path === initialPlan.discoveryPath) catalog = metricCatalogPushDecoder.decode(body);
      if (path === initialPlan.topologyPath && typeof (body as { state?: unknown } | null)?.state === "string") topology = body as TopologyState;
      return catalog !== null && topology !== null && (topology as TopologyState | null)!.state !== "warming";
    });
    if (controller.signal.aborted || !catalog || !topology || (topology as TopologyState | null)!.state === "warming") {
      throw new Error(`Readiness timed out after ${readinessTimeoutMs} ms (field catalog=${Boolean(catalog)}, topology=${(topology as TopologyState | null)?.state ?? "missing"})`);
    }
    readiness.ready = true;
    readiness.waitedMs = performance.now() - waitStarted;
    readiness.topologyState = (topology as TopologyState | null)!.state;
    clearTimeout(timer); controller.abort();
    const fullPlan = await deriveProductionReadPlan(1000, { metrics: (catalog as MetricCatalogPush | null)!.metrics, metricFields: (catalog as MetricCatalogPush | null)!.fields,
      processFields: responseBodies.get(initialPlan.catalogReads.processFields) });
    controller = new AbortController();
    recordingStarted = performance.now(); startedAt = new Date().toISOString();
    // Current values establish the replay baseline without including warm-up rhythm.
    const current = new Map<string, SliceEvent>(readiness.events.filter(event => event.kind === "response").map(event => [event.path, event]));
    for (const event of readiness.events) if (event.kind === "frame") current.set(event.path, event);
    for (const event of current.values()) events.push({ ...event, atMs: 0 });
    timer = setTimeout(() => controller.abort(), durationMs);
    const stats = await consumeUntilStopped(fullPlan, request, append, controller.signal);
    if (stats.frames === 0) throw new Error("Timed recording received no subscription frames");
  } catch (error) {
    failure = error;
  } finally {
    clearTimeout(timer); controller.abort();
    if (!readiness.ready) readiness.waitedMs = performance.now() - waitStarted;
  }
  return { slice: { schemaVersion: 1, startedAt, durationMs, complete: failure === null, readiness,
    events: events.toSorted((a, b) => a.atMs - b.atMs) }, failure };
}

function validateOptions({ baseUrl, durationMs, readinessTimeoutMs = 120000, key, keyless = false }: RecorderOptions) {
  const base = validateBaseUrl(baseUrl);
  if ((!key && !keyless) || (key && keyless)) throw new Error("Supply an authorized key via --key-env, or explicitly select --keyless");
  if (!Number.isFinite(durationMs) || durationMs < 1000 || durationMs > 3600000) throw new Error("Duration must be 1..3600 seconds");
  if (!Number.isFinite(readinessTimeoutMs) || readinessTimeoutMs < 1 || readinessTimeoutMs > 600000) throw new Error("Readiness timeout must be 1..600000 ms");
  return base;
}
function validateBaseUrl(baseUrl: string) {
  let base;
  try { base = new URL(baseUrl); } catch { throw new Error("An explicit loopback base URL is required"); }
  if (typeof baseUrl !== "string" || !["http:", "https:"].includes(base.protocol)
    || !["127.0.0.1", "localhost", "[::1]"].includes(base.hostname)
    || base.username || base.password || base.pathname !== "/" || base.search || base.hash) throw new Error("An explicit loopback base URL is required");
  return base;
}
function parseResponseBody(text: string, contentType: string): { body: unknown; encoding: "json" | "text" } {
  if (!contentType.toLowerCase().includes("json")) return { body: text, encoding: "text" };
  try { return { body: JSON.parse(text), encoding: "json" }; } catch { return { body: text, encoding: "text" }; }
}

async function consumeUntilStopped(plan: ProductionReadPlan, request: RecorderRequest, append: AppendEvent, signal: AbortSignal, stopWhen: FrameHandler = () => false) {
  let delayMs = 1000, frames = 0;
  while (!signal.aborted) {
    let delivered = false;
    try {
      const result = await consumeSubscriptionStream(plan, request, append, signal, (path, value) => {
        delivered = true; frames++;
        return stopWhen(path, value);
      });
      if (result.stopped) return { frames };
    } catch (error) {
      if ((error as { status: number }).status >= 400 && (error as { status: number }).status < 500) throw error;
      if (!signal.aborted) append({ kind: "end", path: plan.streamPath, status: 0, body: { error: String((error as { message: unknown }).message) }, reason: "transport-error" });
    }
    if (signal.aborted) break;
    // BackendSubscriptionChannel.consumeUntilStopped: reset after delivery;
    // otherwise exponential backoff, starting at 1s and capped at 30s.
    if (delivered) delayMs = 1000;
    await waitForReconnect(delayMs, signal);
    delayMs = Math.min(30000, delayMs * 2);
  }
  return { frames };
}

async function consumeSubscriptionStream(plan: ProductionReadPlan, request: RecorderRequest, append: AppendEvent, signal: AbortSignal, onFrame: FrameHandler) {
  const body = { version: 1, subscriptions: plan.subscriptions.map((path, index) => ({ id: `slice:${index}`, path })) };
  const paths = new Map(body.subscriptions.map(item => [item.id, item.path]));
  const response = await request(plan, "POST", plan.streamPath, body);
  if (!response.ok) {
    const parsed = parseResponseBody(await response.text(), response.headers.get("content-type") ?? "");
    append({ kind: "end", path: plan.streamPath, status: response.status, body: parsed.body, reason: "http-refusal" });
    throw Object.assign(new Error(`Subscription HTTP ${response.status}: ${JSON.stringify(parsed.body)}`), { status: response.status });
  }
  if (!response.body) {
    append({ kind: "end", path: plan.streamPath, status: response.status, body: null, reason: "eof" });
    return { stopped: false };
  }
  const reader = response.body.getReader(), decoder = new TextDecoder();
  let pending = "", ended = false;
  const consumeLines = (text: string): boolean => {
    for (const line of text.split("\n")) {
      if (!line.trim()) continue;
      const frame: { subscriptionId: string; value: unknown } = JSON.parse(line), path = paths.get(frame.subscriptionId);
      if (!path || !Object.hasOwn(frame, "value")) throw new Error("Unknown subscription frame");
      if (Number.isInteger((frame.value as { statusCode?: unknown } | null)?.statusCode) && (frame.value as { statusCode: number }).statusCode >= 400) {
        append({ kind: "end", path, status: (frame.value as { statusCode: number }).statusCode, body: frame.value, reason: "selector-refusal" });
        throw Object.assign(new Error(`Subscription selector refused: ${path} HTTP ${(frame.value as { statusCode: number }).statusCode}: ${JSON.stringify(frame.value)}`), { status: (frame.value as { statusCode: number }).statusCode });
      }
      append({ kind: "frame", path, body: frame.value });
      if (onFrame(path, frame.value)) return true;
    }
    return false;
  };
  try {
    while (!signal.aborted) {
      const chunk = await reader.read();
      if (chunk.done) {
        pending += decoder.decode(); ended = true;
        const stopped = consumeLines(pending);
        append({ kind: "end", path: plan.streamPath, status: response.status, body: null, reason: "eof" });
        return { stopped };
      }
      pending += decoder.decode(chunk.value, { stream: true });
      if (pending.length > maximumStreamBufferLength) throw new Error("NDJSON frame limit exceeded");
      const boundary = pending.lastIndexOf("\n");
      if (boundary >= 0) {
        const stopped = consumeLines(pending.slice(0, boundary)); pending = pending.slice(boundary + 1);
        if (stopped) {
          append({ kind: "end", path: plan.streamPath, status: response.status, body: null, reason: "readiness-complete" });
          return { stopped: true };
        }
      }
    }
    return { stopped: false };
  } finally {
    if (signal.aborted) append({ kind: "end", path: plan.streamPath, status: response.status, body: null, reason: "aborted" });
    if (!ended) await reader.cancel().catch(() => undefined);
    reader.releaseLock();
  }
}
function waitForReconnect(delayMs: number, signal: AbortSignal): Promise<void> {
  if (signal.aborted) return Promise.resolve();
  return new Promise<void>(resolveWait => {
    const finish = () => { clearTimeout(timer); signal.removeEventListener("abort", finish); resolveWait(); };
    const timer = setTimeout(finish, delayMs);
    signal.addEventListener("abort", finish, { once: true });
  });
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const args = process.argv.slice(2), value = (flag: string) => args[args.indexOf(flag) + 1];
  if (args.includes("--plan")) console.log(JSON.stringify(await deriveProductionReadPlan(), null, 2));
  else {
    const keyEnv = args.includes("--key-env") ? value("--key-env") : null;
    console.log(JSON.stringify(await recordBackendSlice({ baseUrl: value("--base-url"), durationMs: Number(value("--seconds")) * 1000,
      readinessTimeoutMs: args.includes("--readiness-timeout-seconds") ? Number(value("--readiness-timeout-seconds")) * 1000 : 120000,
      output: value("--output"), key: keyEnv ? process.env[keyEnv] : undefined, keyless: args.includes("--keyless") })));
  }
}
