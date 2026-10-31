import type { ConcreteAppLanguageMode, LanguageOption } from "../../src/i18n/settingsTypes.ts";
import type { ReportRuleField, ReportRuleOverride } from "../../src/types.ts";
import type { Page } from "playwright-core";
import assert from "node:assert/strict";
import { reportRules } from "../fixtures/reportRuleSettings.ts";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";

export default async function reportRuleSettingsJourney(page: Page, baseUrl: string) {
  const patches: { changes: { reports: { ruleOverrides: ReportRuleOverride[] } } }[] = [];
  const errors: string[] = [];
  let reject = false;
  page.on("pageerror", error => errors.push(String(error)));
  await page.route("**/api/settings/report-rules", route => route.fulfill({ json: { rules: reportRules } }));
  await page.route("**/api/settings/app", async route => {
    if (route.request().method() === "PATCH") {
      patches.push(route.request().postDataJSON());
      if (reject) return route.fulfill({ status: 409, json: {
        runtimeApplicationDisposition: "rejectedBeforeCommit",
        runtimeFailureCode: "report-rule-invalid:cpu_throttle_frequency:clearThreshold:clear-on-wrong-side"
      } });
    }
    await route.continue();
  });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  let app = await loadAppCopy("zh-CN");
  let settings = await loadSettingsText("zh-CN");
  await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
  await page.getByRole("button", { name: settings.sections.reportRules, exact: true }).click();
  const panel = () => page.getByRole("region", { name: app.reportRules.title, exact: true });
  const input = (rule: keyof typeof app.reportRules.ruleLabels, field: ReportRuleField) => panel().getByRole("textbox", {
    name: `${app.reportRules.ruleLabels[rule]} · ${app.reportRules.fields[field]}`, exact: true
  });
  await panel().waitFor();
  for (const [rule, value, unit] of [
    ["background_memory_bytes", "4", "GiB"], ["stray_process_memory", "50", "MiB"], ["stray_process_idle", "15", "分钟"]
  ] as const) {
    assert.equal(await input(rule, "activationThreshold").inputValue(), value);
    await panel().getByText(`${app.reportRules.defaultValue} ${value} ${unit}`, { exact: true }).waitFor();
  }
  assert.equal(await input("stray_process_idle", "zeroActivityThreshold").inputValue(), "0");
  assert.equal(await panel().getByRole("textbox").count(), reportRules.reduce((sum, rule) => sum + rule.editableFields.length, 0),
    "Only fields declared by the endpoint should render");

  const edit = async (rule: keyof typeof app.reportRules.ruleLabels, field: ReportRuleField, value: string) => {
    const box = input(rule, field);
    await box.fill(value);
    await box.blur();
  };
  await edit("cpu_throttle_temperature", "activationThreshold", "92");
  await edit("background_memory_bytes", "activationThreshold", "5");
  await edit("stray_process_idle", "activationThreshold", "16");
  await edit("stray_process_idle", "zeroActivityThreshold", "0.5");
  // The frequency rule compares at-or-below: a lower clear threshold is bounded up to activation.
  await edit("cpu_throttle_frequency", "clearThreshold", "70");
  assert.equal(await input("cpu_throttle_frequency", "clearThreshold").inputValue(), "85");
  // Temperature compares at-or-above: a higher clear threshold is bounded down to activation.
  await edit("cpu_throttle_temperature", "clearThreshold", "100");
  assert.equal(await input("cpu_throttle_temperature", "clearThreshold").inputValue(), "92");
  await edit("stray_process_idle", "executableNames", " node \n\n pwsh\n NODE ");
  await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
  await page.getByText(settings.saveState.saved, { exact: true }).waitFor();
  const overrides = patches.at(-1)!.changes.reports.ruleOverrides;
  const rule = (name: string) => overrides.find(entry => entry.rule === name)!;
  assert.equal(rule("cpu_throttle_temperature").activationThreshold, 92);
  assert.equal(rule("background_memory_bytes").activationThreshold, 5368709120);
  assert.equal(rule("stray_process_idle").activationThreshold, 960000);
  assert.equal(rule("stray_process_idle").zeroActivityThreshold, 0.5);
  assert.deepEqual(rule("stray_process_idle").executableNames, ["NODE", "pwsh"]);

  const reset = async (rule: keyof typeof app.reportRules.ruleLabels, field: ReportRuleField) => panel().getByRole("button", {
    name: `${app.reportRules.ruleLabels[rule]} · ${app.reportRules.fields[field]} · ${app.reportRules.reset}`, exact: true
  }).click();
  await reset("cpu_throttle_temperature", "activationThreshold");
  assert.equal(await input("cpu_throttle_temperature", "activationThreshold").inputValue(), "90");
  await reset("cpu_throttle_temperature", "clearThreshold");
  await edit("stray_process_idle", "executableNames", " \n ");
  await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
  await page.getByText(settings.saveState.saved, { exact: true }).waitFor();
  const resetOverrides = patches.at(-1)!.changes.reports.ruleOverrides;
  assert.equal(resetOverrides.some(entry => entry.rule === "cpu_throttle_temperature"), false);
  assert.equal(resetOverrides.find(entry => entry.rule === "background_memory_bytes")!.activationThreshold, 5368709120);
  assert.deepEqual(resetOverrides.find(entry => entry.rule === "stray_process_idle")!.executableNames, []);
  await reset("stray_process_idle", "executableNames");
  assert.equal(await input("stray_process_idle", "executableNames").inputValue(), "cmd\npowershell\nnode");

  reject = true;
  await edit("cpu_throttle_frequency", "clearThreshold", "91");
  await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
  const frequencyRule = panel().getByRole("region", { name: app.reportRules.ruleLabels.cpu_throttle_frequency, exact: true });
  await frequencyRule.getByRole("alert").getByText(app.reportRules.reasons["clear-on-wrong-side"], { exact: true }).waitFor();
  assert.equal(await input("cpu_throttle_frequency", "clearThreshold").inputValue(), "91", "Rejection keeps the draft");
  await edit("cpu_throttle_frequency", "clearThreshold", "92");
  assert.equal(await frequencyRule.getByRole("alert").count(), 0, "Editing clears the previous rejection");
  reject = false;
  await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
  await page.getByText(settings.saveState.saved, { exact: true }).waitFor();

  const languages: string[] = [];
  for (const option of languageOptions.filter((option): option is LanguageOption & { id: ConcreteAppLanguageMode } => option.id !== "system")) {
    await page.getByRole("button", { name: settings.sections.appearance, exact: true }).click();
    await page.getByRole("button", { name: settings.appearance.languageSelectLabel, exact: true }).click();
    await page.getByRole("option", { name: `${option.nativeLabel} · ${option.label}`, exact: true }).click();
    if (option.id !== "zh-CN") await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
    await page.waitForFunction(language => document.documentElement.lang === language, option.id);
    app = await loadAppCopy(option.id);
    settings = await loadSettingsText(option.id);
    await page.getByRole("button", { name: settings.sections.reportRules, exact: true }).click();
    await panel().getByText(app.reportRules.note, { exact: true }).waitFor();
    await input("stray_process_memory", "activationThreshold").waitFor();
    await panel().getByRole("heading", { name: app.reportRules.strayProcessTitle, exact: true }).waitFor();
    // A backend rejection uses the current locale at the actual field consumer.
    reject = true;
    await edit("cpu_throttle_frequency", "clearThreshold", "93");
    await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
    await panel().getByRole("alert").getByText(app.reportRules.reasons["clear-on-wrong-side"], { exact: true }).waitFor();
    reject = false;
    await edit("cpu_throttle_frequency", "clearThreshold", "92");
    languages.push(option.id);
  }
  assert.deepEqual(errors, []);
  return { languages, defaultDisplayValues: ["4 GiB", "50 MiB", "15 分钟"], patches: patches.length };
}
