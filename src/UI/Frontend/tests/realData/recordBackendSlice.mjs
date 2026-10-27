import { mkdir, realpath, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { deriveProductionReadPlan, assertAllowedRequest } from "./productionReadPlan.mjs";
import { assertRawSlicePath } from "./sliceFormat.mjs";

const maximumStreamBufferLength = 32 * 1024 * 1024;

export async function recordBackendSlice({ baseUrl, durationMs, output, key, keyless = false, fetchImpl = fetch }) {
  const base = validateBaseUrl(baseUrl);
  if ((!key && !keyless) || (key && keyless)) {
    throw new Error("Supply an authorized key via --key-env, or explicitly select --keyless");
  }
  if (!Number.isFinite(durationMs) || durationMs < 1000 || durationMs > 3600000) {
    throw new Error("Duration must be 1..3600 seconds");
  }
  const outputPath = resolve(output);
  assertRawSlicePath(outputPath);
  const plan = await deriveProductionReadPlan();
  await mkdir(dirname(outputPath), { recursive: true });
  assertRawSlicePath(resolve(await realpath(dirname(outputPath)), "slice.json"));

  const events = [];
  const responseBodies = new Map();
  const startedAt = new Date().toISOString();
  const started = performance.now();
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), durationMs);
  let failure = null;

  const request = async (activePlan, method, path, body) => {
    assertAllowedRequest(activePlan, method, path, body);
    return fetchImpl(new URL(path, base), {
      method,
      cache: "no-store",
      redirect: "error",
      signal: controller.signal,
      headers: {
        ...(key ? { "X-Resource-Manager-Key": key } : {}),
        ...(body ? { "Content-Type": "application/json", Accept: "application/x-ndjson" } : { Accept: "application/json" })
      },
      ...(body ? { body: JSON.stringify(body) } : {})
    });
  };
  const append = event => {
    if (events.length >= 200000) throw new Error("Slice frame limit exceeded");
    events.push({ atMs: Math.min(durationMs, Math.max(0, performance.now() - started)), ...event });
  };

  try {
    const readOutcomes = await Promise.allSettled(plan.reads.map(async path => {
      const response = await request(plan, "GET", path);
      const contentType = response.headers.get("content-type") ?? "";
      const text = await response.text();
      const { body, encoding } = parseResponseBody(text, contentType);
      append({ kind: "response", path, status: response.status, contentType, encoding, body });
      if (!response.ok) throw new Error(`Read failed: ${path} HTTP ${response.status}`);
      responseBodies.set(path, body);
    }));
    failure = readOutcomes.find(outcome => outcome.status === "rejected")?.reason ?? null;

    if (!controller.signal.aborted) {
      const catalogs = {
        metrics: responseBodies.get(plan.catalogReads.metrics),
        processFields: responseBodies.get(plan.catalogReads.processFields)
      };
      if (!Array.isArray(catalogs.metrics) || !Array.isArray(catalogs.processFields)) {
        throw failure ?? new Error("The production metric and process-field catalogs were not captured");
      }

      const discovery = await consumeSubscriptionStream(plan, request, append, controller.signal, plan.discoveryPath);
      const catalogFrame = discovery.discovered;
      if (!catalogFrame || !Array.isArray(catalogFrame.fields)) {
        throw failure ?? new Error("The production metric-field catalog stream did not provide a usable frame");
      }
      const fullPlan = await deriveProductionReadPlan(1000, { ...catalogs, metricFields: catalogFrame.fields });
      if (!fullPlan.subscriptions.length) throw new Error("Catalogs produced no production subscriptions");
      const capture = await consumeSubscriptionStream(fullPlan, request, append, controller.signal, null);
      if (capture.frames === 0) throw new Error("Full-plan subscription ended before delivering a valid frame");
    }
  } catch (error) {
    failure ??= error;
  } finally {
    clearTimeout(timer);
    controller.abort();
    events.sort((left, right) => left.atMs - right.atMs);
    await writeFile(outputPath, JSON.stringify({ schemaVersion: 1, startedAt, durationMs, complete: !failure, events }), { flag: "wx" });
  }
  if (failure) throw failure;
  return { events: events.length, output: outputPath };
}

function validateBaseUrl(baseUrl) {
  let base;
  try {
    base = new URL(baseUrl);
  } catch {
    throw new Error("An explicit loopback base URL is required");
  }
  if (typeof baseUrl !== "string" || !["http:", "https:"].includes(base.protocol)
    || !["127.0.0.1", "localhost", "[::1]"].includes(base.hostname)
    || base.username || base.password || base.pathname !== "/" || base.search || base.hash) {
    throw new Error("An explicit loopback base URL is required");
  }
  return base;
}

function parseResponseBody(text, contentType) {
  if (!contentType.toLowerCase().includes("json")) return { body: text, encoding: "text" };
  try {
    return { body: JSON.parse(text), encoding: "json" };
  } catch {
    return { body: text, encoding: "text" };
  }
}

async function consumeSubscriptionStream(plan, request, append, signal, stopAfterPath) {
  const body = {
    version: 1,
    subscriptions: plan.subscriptions.map((path, index) => ({ id: `slice:${index}`, path }))
  };
  let response;
  try {
    response = await request(plan, "POST", plan.streamPath, body);
  } catch (error) {
    if (signal.aborted && error?.name === "AbortError") return { discovered: null, frames: 0 };
    throw error;
  }
  if (!response.ok || !response.body) throw new Error(`Subscription failed: HTTP ${response.status}`);

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let pending = "";
  let completed = false;
  const stats = { discovered: null, frames: 0 };
  try {
    while (!signal.aborted) {
      const chunk = await reader.read();
      if (chunk.done) {
        pending += decoder.decode();
        stats.discovered = consumeLines(pending, true, plan, append, stopAfterPath, stats.discovered, stats);
        completed = true;
        if (!signal.aborted) throw new Error("Subscription ended before the requested duration");
        return stats;
      }
      pending += decoder.decode(chunk.value, { stream: true });
      if (pending.length > maximumStreamBufferLength) throw new Error("NDJSON frame limit exceeded");
      stats.discovered = consumeLines(pending, false, plan, append, stopAfterPath, stats.discovered, stats);
      const lastNewline = pending.lastIndexOf("\n");
      pending = lastNewline >= 0 ? pending.slice(lastNewline + 1) : pending;
      if (stopAfterPath && stats.discovered) return stats;
    }
    return stats;
  } catch (error) {
    if (signal.aborted && error?.name === "AbortError") return stats;
    throw error;
  } finally {
    if (!completed) await reader.cancel().catch(() => undefined);
    reader.releaseLock();
  }
}

function consumeLines(text, includeTrailingLine, plan, append, stopAfterPath, previousDiscovery, stats) {
  const lines = text.split("\n");
  const lineCount = includeTrailingLine ? lines.length : Math.max(0, lines.length - 1);
  let discovered = previousDiscovery;
  for (let index = 0; index < lineCount; index += 1) {
    const line = lines[index].trim();
    if (!line) continue;
    const frame = JSON.parse(line);
    const subscription = plan.subscriptions.find((path, pathIndex) => frame.subscriptionId === `slice:${pathIndex}`
      && Object.hasOwn(frame, "value"));
    if (!subscription) throw new Error("Unknown subscription frame");
    append({ kind: "frame", path: subscription, body: frame.value });
    stats.frames += 1;
    if (stopAfterPath && subscription === stopAfterPath) discovered = frame.value;
  }
  return discovered;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const args = process.argv.slice(2);
  const value = flag => args[args.indexOf(flag) + 1];
  if (args.includes("--plan")) console.log(JSON.stringify(await deriveProductionReadPlan(), null, 2));
  else {
    const keyEnv = args.includes("--key-env") ? value("--key-env") : null;
    console.log(JSON.stringify(await recordBackendSlice({
      baseUrl: value("--base-url"),
      durationMs: Number(value("--seconds")) * 1000,
      output: value("--output"),
      key: keyEnv ? process.env[keyEnv] : undefined,
      keyless: args.includes("--keyless")
    })));
  }
}
