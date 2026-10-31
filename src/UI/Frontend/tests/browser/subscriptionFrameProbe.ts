type RequestTelemetry = Record<string, { publications: number }>;
type ProbeWindow = typeof window & { __subscriptionFrameProbe: { counts: Record<string, number>; streams: number; error: string | null } };
import type { Page } from "playwright-core";
import assert from "node:assert/strict";
import { waitForCondition, waitForStableRendering } from "./journeyWaits.ts";

// Side-observe the exact chunks returned to BackendSubscriptionChannel's reader.
// Its read continuation synchronously decodes complete lines and publishes them;
// after the counter fence, crossing stable render frames also crosses that commit.
// No second reader, tee, payload mutation, retained frame history, or product hook.
export async function observeSubscriptionFrames(page: Page, baseUrl: string) {
  const baseline: RequestTelemetry = await (await page.request.get(`${baseUrl}/__test/requests`)).json();
  await installSubscriptionFrameProbe(page);
  return {
    async drain(paths: readonly string[], selectors: string | readonly string[], timeoutMs = 6_500) {
      const telemetry: RequestTelemetry = await (await page.request.get(`${baseUrl}/__test/requests`)).json();
      const targets = Object.fromEntries(paths.map(path => {
        const start = baseline[path]?.publications ?? 0;
        const published = telemetry[path]?.publications ?? 0;
        assert(published >= start, `Frame probe cannot span telemetry reset for ${path}`);
        return [path, published - start];
      }));
      await waitForCondition(page, async () => page.evaluate(targets => {
        const probe = (window as ProbeWindow).__subscriptionFrameProbe;
        if (probe?.error) throw new Error(probe.error);
        return probe && Object.entries(targets).every(([path, count]) => (probe.counts[path] ?? 0) >= count);
      }, targets), { timeoutMs, description: `subscription frames consumed ${JSON.stringify(targets)}` });
      await waitForStableRendering(page, selectors, { timeoutMs });
    }
  };
}

export interface SubscriptionFrameInstallerPage {
  addInitScript(script: () => void): Promise<unknown>;
}

export async function installSubscriptionFrameProbe(page: SubscriptionFrameInstallerPage): Promise<void> {
  await page.addInitScript(() => {
    const probe: ProbeWindow["__subscriptionFrameProbe"] = { counts: {}, streams: 0, error: null };
    (window as ProbeWindow).__subscriptionFrameProbe = probe;
    const originalFetch = window.fetch;
    window.fetch = async function(this: Window, input: RequestInfo | URL, init?: RequestInit) {
      const response = await originalFetch.apply(this, arguments as unknown as Parameters<typeof fetch>);
      const url = new URL(input instanceof Request ? input.url : String(input), location.href);
      if (url.pathname !== "/api/subscriptions/stream" || !response.body) return response;
      const subscriptions: { id: string; path: string }[] = JSON.parse((init?.body as string | undefined) ?? "{}").subscriptions ?? [];
      if (subscriptions.length > 256) { probe.error = "Subscription frame probe exceeded 256 stream items"; return response; }
      // Each stream owns its id map and partial decoder buffer. Reopened streams
      // never reuse a retired stream's mapping; path totals cover all streams.
      const paths = new Map(subscriptions.map(item => [item.id, new URL(item.path, location.href).pathname]));
      probe.streams++;
      const stream = response.body;
      const getReader = stream.getReader.bind(stream) as () => ReadableStreamDefaultReader<Uint8Array<ArrayBuffer>>;
      stream.getReader = ((...args: []) => {
        const reader = getReader(...args);
        const read = reader.read.bind(reader);
        const decoder = new TextDecoder();
        let pending = "";
        reader.read = async (...readArgs: []) => {
          const chunk = await read(...readArgs);
          if (probe.error) return chunk;
          try {
            pending += decoder.decode(chunk.value, { stream: !chunk.done });
            // Real catalog publications exceed 1 MiB. Keep the same bounded
            // frame capacity as the raw recorder without retaining frame history.
            if (pending.length > 32 * 1024 * 1024) throw new Error("Subscription frame probe exceeded 32 MiB partial buffer");
            const lines = pending.split("\n");
            pending = lines.pop()!;
            if (chunk.done && pending.trim()) { lines.push(pending); pending = ""; }
            for (const line of lines) {
              if (!line.trim()) continue;
              const path = paths.get(JSON.parse(line).subscriptionId);
              if (!path) throw new Error("Subscription frame probe received an unmapped id");
              if (!(path in probe.counts) && Object.keys(probe.counts).length >= 256)
                throw new Error("Subscription frame probe exceeded 256 paths");
              probe.counts[path] = (probe.counts[path] ?? 0) + 1;
              if (probe.counts[path] > 1_000_000) throw new Error("Subscription frame probe exceeded its frame limit");
            }
          } catch (error) { pending = ""; probe.error = String(error); }
          return chunk;
        };
        return reader;
      }) as typeof stream.getReader;
      return response;
    };
  });
}
