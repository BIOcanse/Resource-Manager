import assert from "node:assert/strict";
import { putElevatedBackendJson } from "../src/api/elevatedBackendRequest.ts";
import { ApiRequestError } from "../src/api/httpTransport.ts";
import { uiText, applyLanguage } from "../src/text.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";

const oldWindow = Object.getOwnPropertyDescriptor(globalThis, "window");
const oldDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
const oldFetch = globalThis.fetch;
const listeners = new Set<(event: { data: unknown }) => void>();
const sent: Array<{ type: string; requestId: string; method: string; path: string; body: string }> = [];
let reply: Record<string, unknown> = {};
let throwPost = false;
Object.defineProperty(globalThis, "document", { configurable: true, value: { documentElement: { lang: "", dir: "" } } });
Object.defineProperty(globalThis, "window", { configurable: true, value: { unboundWebView: {
  postMessage(message: typeof sent[number]) {
    if (message.type !== "backend.elevatedRequest") return;
    if (throwPost) throw new Error("native failure must not leak");
    sent.push(message);
    for (const listener of [...listeners]) listener({ data: { type: "backend.elevatedRequest.result", requestId: "unrelated", error: "canceled" } });
    for (const listener of [...listeners]) listener({ data: { type: "backend.elevatedRequest.result", requestId: message.requestId, ...reply } });
  },
  addEventListener(_type: string, listener: (event: { data: unknown }) => void) { listeners.add(listener); },
  removeEventListener(_type: string, listener: (event: { data: unknown }) => void) { listeners.delete(listener); }
} } });
globalThis.fetch = (async () => { throw new Error("Elevated mutations must never fetch with web/default credentials"); }) as typeof fetch;
const path = "/api/startup-modules/software/game%201";
const selection = { softwareId: "game 1", moduleIds: ["performance-overlay-preload", "performance-overlay-vulkan"] };
try {
  for (const option of languageOptions) {
    if (option.id === "system") continue;
    const [copy] = await Promise.all([loadAppCopy(option.id), loadSettingsText(option.id)]);
    applyLanguage(option.id);
    await Promise.resolve();
    const call = async (value: Record<string, unknown>) => {
      reply = value;
      try { return await putElevatedBackendJson(path, selection, uiText.apiError.saveGpuSoftwarePolicyFailed); }
      finally { assert.equal(listeners.size, 0, `${option.id}: listener cleanup`); }
    };
    assert.deepEqual(await call({ statusCode: 200, body: JSON.stringify(selection), error: null }), selection);
    const request = sent.at(-1)!;
    assert.deepEqual({ ...request, requestId: "correlated" }, {
      type: "backend.elevatedRequest", requestId: "correlated", method: "PUT", path, body: JSON.stringify(selection)
    });
    assert.equal(Object.keys(request).some(key => /key|token|secret|authorization/i.test(key)), false);
    await assert.rejects(call({ statusCode: null, body: null, error: "canceled" }), (error: unknown) => error instanceof ApiRequestError
      && error.kind === "aborted" && error.userMessage === copy.misc.transportCanceled && !error.retryable);
    for (const invalid of [
      { statusCode: null, body: null, error: "unavailable" },
      { statusCode: "200", body: JSON.stringify(selection), error: null },
      { statusCode: 200, body: "", error: null },
      { statusCode: 200, body: "not JSON", error: null },
      { statusCode: 200, body: " ".repeat(256 * 1024 + 1), error: null }
    ]) await assert.rejects(call(invalid), (error: unknown) => error instanceof ApiRequestError
      && error.kind === "invalid-response" && error.userMessage === copy.apiError.saveGpuSoftwarePolicyFailed && !error.retryable);
    await assert.rejects(call({ statusCode: 403, body: '{"error":"access-scope-required"}', error: null }),
      (error: unknown) => error instanceof ApiRequestError && error.kind === "http" && error.status === 403
        && error.userMessage === copy.apiError.saveGpuSoftwarePolicyFailed && !error.retryable);
    for (const statusCode of [400, 403]) {
      for (const body of ["", "not JSON"]) {
        await assert.rejects(call({ statusCode, body, error: null }),
          (error: unknown) => error instanceof ApiRequestError && error.kind === "http" && error.status === statusCode
            && error.payload === null && error.userMessage === copy.apiError.saveGpuSoftwarePolicyFailed && !error.retryable);
      }
    }
  }
  const count = sent.length;
  await assert.rejects(putElevatedBackendJson(path, { huge: "x".repeat(128 * 1024) }, "fallback"), ApiRequestError);
  assert.equal(sent.length, count);
  throwPost = true;
  await assert.rejects(putElevatedBackendJson(path, selection, "fallback"),
    (error: unknown) => error instanceof ApiRequestError && error.kind === "network" && error.userMessage === "fallback");
  assert.equal(listeners.size, 0);
  Object.defineProperty(globalThis, "window", { configurable: true, value: {} });
  await assert.rejects(putElevatedBackendJson(path, selection, "fallback"),
    (error: unknown) => error instanceof ApiRequestError && error.kind === "invalid-response" && error.userMessage === uiText.shellBridge.desktopOnly);
} finally {
  globalThis.fetch = oldFetch;
  if (oldWindow) Object.defineProperty(globalThis, "window", oldWindow); else Reflect.deleteProperty(globalThis, "window");
  if (oldDocument) Object.defineProperty(globalThis, "document", oldDocument); else Reflect.deleteProperty(globalThis, "document");
}
console.log("Elevated backend bridge: explicit native-only writes, nine-language errors, correlation, bounds and cleanup passed");
