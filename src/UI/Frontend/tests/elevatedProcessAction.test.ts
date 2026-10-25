import assert from "node:assert/strict";
import { withProcessActionElevation } from "../src/processes/elevatedProcessAction.ts";
import { ApiRequestError } from "../src/api/httpTransport.ts";
import { uiText, applyLanguage } from "../src/text.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { systemProcessResultDetail, systemProcessResultSummary } from "../src/presentation/systemProcessResult.ts";
import type { SystemProcessOperationResult } from "../src/types";

const oldWindow = Object.getOwnPropertyDescriptor(globalThis, "window");
const oldDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
const listeners = new Set<(event: { data: unknown }) => void>();
const sent: Array<{ type?: string; requestId?: string; batch?: unknown }> = [];
let nextReply: Record<string, unknown> = {};
Object.defineProperty(globalThis, "document", { configurable: true, value: { documentElement: { lang: "", dir: "" } } });
Object.defineProperty(globalThis, "window", { configurable: true, value: { chrome: { webview: {
  postMessage(message: { type?: string; requestId?: string; batch?: unknown }) {
    if (message.type !== "shell.processAction") return;
    sent.push(message);
    for (const listener of listeners) listener({ data: { type: "shell.processAction.result", requestId: "unrelated", canceled: true } });
    for (const listener of [...listeners]) listener({ data: { type: "shell.processAction.result", requestId: message.requestId, ...nextReply } });
  },
  addEventListener(_type: string, listener: (event: { data: unknown }) => void) { listeners.add(listener); },
  removeEventListener(_type: string, listener: (event: { data: unknown }) => void) { listeners.delete(listener); }
} } } });
const targets = [{ processId: 71, processStartKey: "132537599910000000" }];
const result: SystemProcessOperationResult = { action: "Terminate", directoryPath: null, items: [
  { processId: 71, processStartKey: targets[0].processStartKey, processName: "fixture", state: "Succeeded",
    outcome: "Terminated", path: null, nativeErrorCode: null }
] };
const denied = () => new ApiRequestError("raw backend error", {
  kind: "http", status: 403, payload: { code: "ProcessActionRequiresElevation" }, retryable: false
});
try {
  for (const option of languageOptions) {
    if (option.id === "system") continue;
    const [copy] = await Promise.all([loadAppCopy(option.id), loadSettingsText(option.id)]);
    applyLanguage(option.id);
    await Promise.resolve();
    const call = async (reply: Record<string, unknown>) => {
      nextReply = reply;
      let requests = 0;
      try {
        return await withProcessActionElevation("terminate", targets, async () => { requests++; throw denied(); }, uiText.apiError.terminateFailed);
      } finally { assert.equal(requests, 1); assert.equal(listeners.size, 0); }
    };
    const completed = await call({ result });
    assert.deepEqual(completed, result);
    assert.equal(systemProcessResultSummary(completed).message, copy.managementFeedback.processTerminateSummary(1, 1));
    const protectedItem = { ...result.items[0], state: "Skipped", outcome: "CriticalProcessProtected" as const };
    assert.equal(systemProcessResultDetail(protectedItem), `fixture: ${copy.softwareActions.criticalProcessProtected}`);
    assert.equal(systemProcessResultSummary({ action: "Terminate", items: [protectedItem] }).complete, false);
    assert.deepEqual(sent.at(-1)?.batch, { action: "terminate", targets });
    await assert.rejects(call({ canceled: true }), (error: unknown) => error instanceof ApiRequestError
      && error.kind === "aborted" && error.userMessage === copy.misc.transportCanceled && !error.retryable);
    await assert.rejects(call({ timedOut: true }), (error: unknown) => error instanceof ApiRequestError
      && error.kind === "timeout" && error.userMessage === copy.misc.transportTimeout && !error.retryable);
    await assert.rejects(call({ result: { ...result, items: [{ ...result.items[0], processStartKey: "132537599910000001" }] } }),
      (error: unknown) => error instanceof ApiRequestError && error.kind === "invalid-response"
        && error.userMessage === copy.apiError.terminateFailed && !error.retryable);
    for (const invalid of [
      { ...result, action: "Dump" },
      { ...result, items: [{ ...result.items[0], outcome: "DumpCreated" }] },
      { ...result, items: [{ ...result.items[0], outcome: "Raw prose" }] },
      { ...result, items: [{ ...result.items[0], state: "Failed" }] },
      { ...result, items: [{ ...result.items[0], processStartKey: 132537599910000000 }] }
    ]) await assert.rejects(call({ result: invalid }), (error: unknown) => error instanceof ApiRequestError && error.kind === "invalid-response");
    const failedItem = { ...result.items[0], state: "Failed", outcome: "NativeFailure" as const, nativeErrorCode: 87 };
    assert.equal(systemProcessResultDetail(failedItem), `fixture: ${copy.managementFeedback.processNativeFailure}`);
    assert.equal(systemProcessResultSummary({ action: "Dump", items: [{ ...result.items[0], outcome: "DumpCreated" }] }).message,
      copy.managementFeedback.processDumpSummary(1, 1));
    await assert.rejects(call({ error: "process-action-unavailable" }),
      (error: unknown) => error instanceof ApiRequestError && error.userMessage === copy.apiError.terminateFailed);
  }
  const before = sent.length;
  const dumpResult: SystemProcessOperationResult = { ...result, action: "Dump", items: [{ ...result.items[0], outcome: "DumpCreated" }] };
  assert.deepEqual(await withProcessActionElevation("dump", targets, async () => dumpResult, "fallback"), dumpResult);
  const unrelated = new ApiRequestError("denied", { kind: "http", status: 403, payload: { code: "Other" }, retryable: false });
  await assert.rejects(withProcessActionElevation("dump", targets, async () => { throw unrelated; }, "fallback"), error => error === unrelated);
  assert.equal(sent.length, before);
  Object.defineProperty(globalThis, "window", { configurable: true, value: {} });
  const noHost = denied();
  await assert.rejects(withProcessActionElevation("dump", targets, async () => { throw noHost; }, "fallback"), error => error === noHost);
} finally {
  if (oldWindow) Object.defineProperty(globalThis, "window", oldWindow); else Reflect.deleteProperty(globalThis, "window");
  if (oldDocument) Object.defineProperty(globalThis, "document", oldDocument); else Reflect.deleteProperty(globalThis, "document");
}
console.log("elevated process actions: 9 language journeys passed");
