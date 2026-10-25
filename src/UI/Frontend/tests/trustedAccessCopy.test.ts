import assert from "node:assert/strict";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import type { ConcreteAppLanguageMode } from "../src/i18n/settingsTypes.ts";
import type { SourceSnapshot, SourceStatus } from "../src/frontendRuntime/source/SourceSnapshot.ts";
import {
  accessRequestListView,
  accessRequestNoticeText,
  accessScopeLabel,
  type AccessRequestListNotice
} from "../src/features/trustedAccess/trustedAccessPresentation.ts";
import { knownAccessScopes, type AccessRequestPendingSnapshot } from "../src/features/trustedAccess/trustedAccessTypes.ts";
import { flattenCopy, inspectTraditional } from "./localizationCoverage.ts";

// 待处理请求订阅的每个状态都有确定的显示：不可用、失败、过时都有提示；没有可显示的数据时不沿用旧列表。
const request = {
  requestId: "request-1", name: "Telemetry helper", requestedScopes: ["read"],
  observedExecutablePath: "C:\\Tools\\telemetry.exe", observedProcessId: 4321,
  submittedAt: "2026-10-25T12:00:00Z", expiresAt: "2026-10-25T12:05:00Z"
};
const snapshot = (status: SourceStatus, data: AccessRequestPendingSnapshot | null): SourceSnapshot<AccessRequestPendingSnapshot> => ({
  key: "access-requests.pending", status, data, error: null, backendEpoch: null, revision: 1,
  acceptedAttempt: null, domainRevision: null, capturedAt: null
});
const withRequest = { requests: [request] };
const cases: Array<[SourceSnapshot<AccessRequestPendingSnapshot> | null, AccessRequestListNotice | null, number, boolean]> = [
  [null, "loading", 0, false],
  [snapshot("idle", null), "loading", 0, false],
  [snapshot("loading", null), "loading", 0, false],
  [snapshot("ready", withRequest), null, 1, false],
  [snapshot("ready", { requests: [] }), null, 0, true],
  [snapshot("refreshing", withRequest), null, 1, false],
  [snapshot("refreshing", null), "loading", 0, false],
  [snapshot("stale", withRequest), "stale", 1, false],
  [snapshot("unavailable", null), "unavailable", 0, false],
  [snapshot("unavailable", withRequest), "unavailable", 0, false],
  [snapshot("disposed", null), "unavailable", 0, false],
  [snapshot("error", null), "error", 0, false]
];
for (const [input, notice, count, empty] of cases) {
  const view = accessRequestListView(input);
  const label = input?.status ?? "null";
  assert.equal(view.notice, notice, `${label}: notice`);
  assert.equal(view.requests.length, count, `${label}: rendered requests`);
  assert.equal(view.empty, empty, `${label}: empty state`);
}

// 九种语言：可信访问、登录启动和叠加层注入提示的每一条文案都存在、非空、已翻译；模板带上参数。
const languages = languageOptions.map((option) => option.id)
  .filter((id): id is ConcreteAppLanguageMode => id !== "system");
assert.equal(languages.length, 9);
const sampleName = "Tool-Ω7";
const sampleResult = "RESULT-Ω7";
const sampleCount = 37;
const templateArguments: Record<string, readonly unknown[]> = {
  requestCount: [sampleCount],
  requestOutcome: [sampleName, sampleResult],
  requestDeniedWithReason: [sampleResult]
};
/** 语言无关、各语言相同的值：PID 是进程编号的通用缩写；结果行只是“名称：结果”，用该语言的冒号。 */
const languageNeutral = new Set(["trustedAccess.observedProcess", "trustedAccess.requestOutcome"]);
/** 拼写与英语相同的正确译文。 */
const sameSpelling = new Set(["de-DE:trustedAccess.nameLabel"]);

function rendered(surface: string, copy: unknown): Map<string, { text: string; parameters: readonly string[] }> {
  const result = new Map<string, { text: string; parameters: readonly string[] }>();
  for (const [path, value] of flattenCopy(copy)) {
    const key = `${surface}.${path}`;
    if (typeof value === "function") {
      const leaf = path.split(".").at(-1)!;
      const args = templateArguments[leaf] ?? [sampleName];
      result.set(key, { text: String((value as (...args: unknown[]) => unknown)(...args)), parameters: args.map(String) });
    } else {
      result.set(key, { text: value as string, parameters: [] });
    }
  }
  return result;
}

async function featureText(language: ConcreteAppLanguageMode) {
  const [settings, app] = await Promise.all([loadSettingsText(language), loadAppCopy(language)]);
  const integration = settings.systemIntegration;
  const loginStart = Object.fromEntries(Object.entries(integration).filter(([key]) => key.startsWith("loginStart")));
  const overlay = {
    externalModeModulesPresent: app.performanceOverlay.externalModeModulesPresent,
    injectedModulesPartial: app.performanceOverlay.injectedModulesPartial,
    modulesSavedPreferencesFailed: app.performanceOverlay.modulesSavedPreferencesFailed
  };
  return {
    copy: integration.trustedAccess,
    leaves: new Map([
      ...rendered("trustedAccess", integration.trustedAccess),
      ...rendered("loginStart", loginStart),
      ...rendered("overlay", overlay)
    ])
  };
}

const english = await featureText("en-US");
assert.ok(english.leaves.size > 90, "the feature copy was enumerated");
for (const language of languages) {
  const { copy, leaves } = await featureText(language);
  assert.deepEqual([...leaves.keys()].sort(), [...english.leaves.keys()].sort(), `${language}: same copy keys as English`);
  for (const [path, { text, parameters }] of leaves) {
    assert.ok(text.trim(), `${language}: ${path} is empty`);
    for (const parameter of parameters) {
      assert.ok(text.includes(parameter), `${language}: ${path} drops its parameter`);
    }
    if (language !== "en-US" && !languageNeutral.has(path) && !sameSpelling.has(`${language}:${path}`)) {
      assert.notEqual(text, english.leaves.get(path)!.text, `${language}: ${path} is untranslated English`);
    }
    if (!/^(?:zh|ja)-/.test(language)) {
      assert.doesNotMatch(text, /\p{Script=Han}/u, `${language}: ${path} contains Han characters`);
    }
    if (language === "zh-TW") {
      assert.deepEqual(inspectTraditional(path, text), [], `zh-TW: ${path} uses Simplified glyphs`);
    }
  }
  for (const scope of knownAccessScopes) {
    assert.equal(accessScopeLabel(copy, scope), copy.scopeLabels[scope], `${language}: ${scope}`);
  }
  assert.equal(new Set(knownAccessScopes.map((scope) => copy.scopeLabels[scope])).size, knownAccessScopes.length,
    `${language}: scope labels are distinct`);
  const unknown = accessScopeLabel(copy, "future.scope");
  assert.equal(unknown, copy.unknownScope, `${language}: unknown scope label`);
  assert.doesNotMatch(unknown, /future\.scope/, `${language}: the raw scope ID is not shown`);
  const notices = (["loading", "unavailable", "error", "stale"] as const).map((notice) => accessRequestNoticeText(copy, notice));
  assert.equal(new Set(notices).size, 4, `${language}: each request-list state has its own text`);
  assert.equal(new Set(Object.values(copy.failureReasons)).size, 3, `${language}: each failure reason has its own text`);
}
console.log(`Trusted-access request states and ${languages.length}-language feature copy passed.`);
