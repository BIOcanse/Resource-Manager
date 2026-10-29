import assert from "node:assert/strict";
import { exactProcessActionResult } from "../src/processes/processActionResult.ts";
import { ApiRequestError } from "../src/api/httpTransport.ts";
import { uiText, applyLanguage } from "../src/text.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { systemProcessResultDetail, systemProcessResultSummary } from "../src/presentation/systemProcessResult.ts";
import type { SystemProcessOperationResult } from "../src/types";

const oldWindow = Object.getOwnPropertyDescriptor(globalThis, "window");
const oldDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
// 切换语言时会通知外壳，这里没有外壳：给一个空的 window。
Object.defineProperty(globalThis, "window", { configurable: true, value: {} });
Object.defineProperty(globalThis, "document", { configurable: true, value: { documentElement: { lang: "", dir: "" } } });
const targets = [{ processId: 71, processStartKey: "132537599910000000" }];
const result: SystemProcessOperationResult = { action: "Terminate", directoryPath: null, items: [
  { processId: 71, processStartKey: targets[0].processStartKey, processName: "fixture", state: "Succeeded",
    outcome: "Terminated", path: null, nativeErrorCode: null }
] };
try {
  for (const option of languageOptions) {
    if (option.id === "system") continue;
    const [copy] = await Promise.all([loadAppCopy(option.id), loadSettingsText(option.id)]);
    await applyLanguage(option.id);
    const accept = (value: unknown) => exactProcessActionResult("terminate", targets, value, uiText.apiError.terminateFailed);
    const completed = accept(result);
    assert.deepEqual(completed, result);
    assert.equal(systemProcessResultSummary(completed).message, copy.managementFeedback.processTerminateSummary(1, 1));
    const protectedItem = { ...result.items[0], state: "Skipped", outcome: "CriticalProcessProtected" as const };
    assert.equal(systemProcessResultDetail(protectedItem), `fixture: ${copy.softwareActions.criticalProcessProtected}`);
    assert.equal(systemProcessResultSummary({ action: "Terminate", items: [protectedItem] }).complete, false);
    // 结果必须是这次请求的那几个进程：换了启动键（进程号被复用）就不是同一个进程。
    assert.throws(() => accept({ ...result, items: [{ ...result.items[0], processStartKey: "132537599910000001" }] }),
      (error: unknown) => error instanceof ApiRequestError && error.kind === "invalid-response"
        && error.userMessage === copy.apiError.terminateFailed && !error.retryable);
    for (const invalid of [
      { ...result, action: "Dump" },
      { ...result, items: [] },
      { ...result, items: [result.items[0], result.items[0]] },
      { ...result, items: [{ ...result.items[0], outcome: "DumpCreated" }] },
      { ...result, items: [{ ...result.items[0], outcome: "Raw prose" }] },
      { ...result, items: [{ ...result.items[0], state: "Failed" }] },
      { ...result, items: [{ ...result.items[0], processStartKey: 132537599910000000 }] }
    ]) assert.throws(() => accept(invalid), (error: unknown) => error instanceof ApiRequestError && error.kind === "invalid-response");
    const failedItem = { ...result.items[0], state: "Failed", outcome: "NativeFailure" as const, nativeErrorCode: 87 };
    assert.equal(systemProcessResultDetail(failedItem), `fixture: ${copy.managementFeedback.processNativeFailure}`);
    assert.equal(systemProcessResultSummary({ action: "Dump", items: [{ ...result.items[0], outcome: "DumpCreated" }] }).message,
      copy.managementFeedback.processDumpSummary(1, 1));
  }
  const dumpResult: SystemProcessOperationResult = { ...result, action: "Dump", items: [{ ...result.items[0], outcome: "DumpCreated" }] };
  assert.deepEqual(exactProcessActionResult("dump", targets, dumpResult, "fallback"), dumpResult);
} finally {
  if (oldWindow) Object.defineProperty(globalThis, "window", oldWindow); else Reflect.deleteProperty(globalThis, "window");
  if (oldDocument) Object.defineProperty(globalThis, "document", oldDocument); else Reflect.deleteProperty(globalThis, "document");
}
console.log("process action results: 9 language journeys passed");
