import assert from "node:assert/strict";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname } from "node:path";
import { fileURLToPath } from "node:url";
import type { AppCopy } from "../src/i18n/copy/index.ts";
import type { ConcreteAppLanguageMode, SettingsTextBundle } from "../src/i18n/settingsTypes.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { applyLanguage, currentLanguage, currentSettingsText, uiText } from "../src/i18n/appTextStore.ts";
import { controlCapabilityLabel, controlDisplayText } from "../src/features/control/controlPresentation.ts";
import { flattenCopy, inspectCopy, inspectParameters, inspectText, inspectTraditional } from "./localizationCoverage.ts";
import type { LocalizationIssue } from "./localizationCoverage.ts";

interface FunctionCase<T> {
  path: string;
  render: (copy: T) => string;
  parameters: readonly string[];
  requiresTranslation: boolean;
}
const appCases: FunctionCase<AppCopy>[] = [
  { path: "control.needsComponent", render: copy => copy.control.needsComponent("NVAPI"), parameters: ["NVAPI"], requiresTranslation: true },
  { path: "control.instances.firstSeen", render: copy => copy.control.instances.firstSeen("2030-01-02"), parameters: ["2030-01-02"], requiresTranslation: true },
  { path: "control.accessLevel.confirmTitle", render: copy => copy.control.accessLevel.confirmTitle("root"), parameters: ["root"], requiresTranslation: true },
  { path: "control.takeoverCoversOthers", render: copy => copy.control.takeoverCoversOthers("Fan-A"), parameters: ["Fan-A"], requiresTranslation: true },
  { path: "control.presentation.fanName", render: copy => copy.control.presentation.fanName("92"), parameters: ["92"], requiresTranslation: true },
  { path: "control.presentation.gpuFanControllerName", render: copy => copy.control.presentation.gpuFanControllerName("14"), parameters: ["14"], requiresTranslation: true },
  { path: "diskUsage.freeOfTotal", render: copy => copy.diskUsage.freeOfTotal("31 GiB", "512 GiB"), parameters: ["31 GiB", "512 GiB"], requiresTranslation: true },
  { path: "diskUsage.scanTotals", render: copy => copy.diskUsage.scanTotals("29 GiB", 13, 7), parameters: ["29 GiB", "13", "7"], requiresTranslation: true },
  { path: "shell.documentTitle", render: copy => copy.shell.documentTitle("Test-page", "Resource Manager"), parameters: ["Test-page", "Resource Manager"], requiresTranslation: false }
];
for (const count of [0, 1, 37]) {
  appCases.push(
    { path: `diskUsage.fileCount(${count})`, render: copy => copy.diskUsage.fileCount(count), parameters: [String(count)], requiresTranslation: true },
    { path: `diskUsage.omitted(${count})`, render: copy => copy.diskUsage.omitted(count), parameters: [String(count)], requiresTranslation: true },
    { path: `shell.taskCenterActive(${count})`, render: copy => copy.shell.taskCenterActive(count), parameters: [String(count)], requiresTranslation: true }
  );
}
const settingsCases: FunctionCase<SettingsTextBundle>[] = [
  { path: "performance.smartMonitoringDescription(17)", render: copy => copy.performance.smartMonitoringDescription(17), parameters: ["17"], requiresTranslation: true },
  { path: "updates.confirmPrepare", render: copy => copy.updates.confirmPrepare("4.7.9-beta.8"), parameters: ["4.7.9-beta.8"], requiresTranslation: true }
];

const englishApp = await loadAppCopy("en-US");
const englishSettings = await loadSettingsText("en-US");
const englishAppLeaves = flattenCopy(englishApp);
const englishSettingsLeaves = flattenCopy(englishSettings);
const issues: LocalizationIssue[] = [];
const languageResults: Array<{ language: string; appLeaves: number; settingsLeaves: number; functionCases: number; issues: number }> = [];
const languages = languageOptions.map(option => option.id).filter((id): id is ConcreteAppLanguageMode => id !== "system");
assert(languages.length > 0, "No selectable languages were checked.");
assert.equal(new Set(languages).size, languages.length, "Duplicate language option.");

// Check the real document-language and native-host notifications without opening
// the installed application or changing the user's persisted settings.
const documentState = { lang: "", dir: "" };
const hostMessages: unknown[] = [];
const oldDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
const oldWindow = Object.getOwnPropertyDescriptor(globalThis, "window");
Object.defineProperty(globalThis, "document", { configurable: true, value: { documentElement: documentState } });
Object.defineProperty(globalThis, "window", { configurable: true, value: { chrome: { webview: { postMessage: (message: unknown) => hostMessages.push(message) } } } });

function inspectFunctions<T>(language: string, surface: string, copy: T, english: T, cases: FunctionCase<T>[]) {
  for (const fixture of cases) {
    const path = `${surface}.${fixture.path}`;
    const actual = fixture.render(copy);
    issues.push(...inspectText(language, path, actual, fixture.render(english), fixture.requiresTranslation));
    issues.push(...inspectParameters(language, path, actual, fixture.parameters));
  }
}

try {
  for (const language of languages) {
    const before = issues.length;
    const [app, settings] = await Promise.all([loadAppCopy(language), loadSettingsText(language)]);
    assert.equal(settings.language, language, `${language}: settings loader silently returned another language.`);
    applyLanguage(language);
    // Bundles are already cached; one microtask settles the production store.
    await Promise.resolve();
    assert.equal(currentLanguage(), language);
    assert.equal(currentSettingsText().language, language);
    assert.deepEqual(flattenCopy(uiText), flattenCopy(app), `${language}: active UI differs from the loaded bundle.`);
    assert.deepEqual(flattenCopy(currentSettingsText()), flattenCopy(settings));
    assert.equal(documentState.lang, language);
    assert.equal(documentState.dir, /^(?:ar|he)-/.test(language) ? "rtl" : "ltr");
    assert.deepEqual(hostMessages.at(-1), { type: "shell.language", language });
    issues.push(...inspectCopy(language, "app", uiText, englishApp));
    issues.push(...inspectCopy(language, "settings", currentSettingsText(), englishSettings, true));
    inspectFunctions(language, "app", uiText, englishApp, appCases);
    inspectFunctions(language, "settings", currentSettingsText(), englishSettings, settingsCases);

    if (language === "zh-TW") {
      const samples = [
        ["app.page.settings", uiText.page.settings],
        ["app.page.monitor", uiText.page.monitor],
        ["app.control.intro", uiText.control.intro],
        ["settings.loadState.loading", currentSettingsText().loadState.loading],
        ["settings.saveState.partial", currentSettingsText().saveState.partial]
      ] as const;
      for (const [path, value] of samples) issues.push(...inspectTraditional(path, value));
    }
    // These are built-in product labels/reasons, not arbitrary vendor names.
    // The actual presentation helpers must translate them independently of
    // whether a locale file happens to be present.
    const displays = [
      ["consumer.controlCapabilityLabel(fan.curve)", controlCapabilityLabel({ id: "fan.curve", label: "转速曲线" }), "转速曲线"],
      ["consumer.controlDisplayText(reason)", controlDisplayText("转速为监测值。"), "转速为监测值。"],
      ["consumer.controlDisplayText(fan)", controlDisplayText("风扇 2"), "风扇 2"]
    ] as const;
    for (const [path, actual, chinese] of displays) {
      if (language !== "zh-CN" && actual === chinese) {
        issues.push({ language, path, kind: "consumer", actual });
      }
      if (path.endsWith("(fan)")) issues.push(...inspectParameters(language, path, actual, ["2"]));
    }
    languageResults.push({ language, appLeaves: flattenCopy(app).size, settingsLeaves: flattenCopy(settings).size, functionCases: appCases.length + settingsCases.length, issues: issues.length - before });
  }
} finally {
  if (oldDocument) Object.defineProperty(globalThis, "document", oldDocument);
  else Reflect.deleteProperty(globalThis, "document");
  if (oldWindow) Object.defineProperty(globalThis, "window", oldWindow);
  else Reflect.deleteProperty(globalThis, "window");
}

const reportPath = fileURLToPath(new URL("../../../../.local/test-results/localization-coverage.json", import.meta.url));
mkdirSync(dirname(reportPath), { recursive: true });
writeFileSync(reportPath, JSON.stringify({
  scope: "Selectable frontend languages: production loaders, active stores, host/document language, representative function outputs and control display helpers. Does not certify linguistic quality, rendered UI or other EXEs.",
  functionCoverage: { appFunctions: [...englishAppLeaves.values()].filter(value => typeof value === "function").length, appCases: appCases.map(value => value.path), settingsFunctions: [...englishSettingsLeaves.values()].filter(value => typeof value === "function").length, settingsCases: settingsCases.map(value => value.path) },
  languageResults,
  issues
}, null, 2));
for (const result of languageResults) console.log(`${result.issues ? "FAIL" : "PASS"} ${result.language}: ${result.issues} coverage findings`);
console.log(`Full language/path/text report: ${reportPath}`);
if (issues.length > 0) {
  // Print one example per defect class/language; keep every finding in JSON.
  const printed = new Set<string>();
  for (const issue of issues) {
    const group = `${issue.language}:${issue.kind}`;
    if (printed.has(group)) continue;
    printed.add(group);
    console.error(`${group} ${issue.path}: ${JSON.stringify(issue.actual)}`);
  }
  process.exitCode = 1;
} else {
  console.log("Frontend localization coverage passed within the recorded scope.");
}
