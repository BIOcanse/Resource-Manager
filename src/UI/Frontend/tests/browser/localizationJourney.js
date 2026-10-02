import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import usabilityProbe from "./usabilityProbe.js";

export default async function localizationJourney(page, baseUrl) {
  const failures = [];
  page.on("pageerror", error => failures.push(String(error)));
  page.on("console", message => { if (message.type() === "error") failures.push(message.text()); });
  let stage = "idle";
  const history = [
    { choice: "latest", version: "0.3.9", channel: "preview", selectable: true },
    { choice: "latestStable", version: "0.3.8", channel: "stable", selectable: true },
    { choice: "version:0.3.9", version: "0.3.9", series: "0.3", channel: "preview", selectable: true },
    { choice: "version:0.3.8", version: "0.3.8", series: "0.3", channel: "stable", selectable: true },
    { choice: "version:0.3.1", version: "0.3.1", series: "0.3", channel: "stable", selectable: false,
      unavailableReason: "只能选择高于当前安装版本的发行版。" }
  ];
  await page.route("**/api/updates/product/versions", route => route.fulfill({ json: {
    installedVersion: "0.3.5", status: "loaded", complete: true, checkedAt: "2026-10-01T12:00:00Z", options: history
  } }));
  await page.route("**/api/updates/product/status", route => route.fulfill({ json: { stage } }));
  await page.setViewportSize({ width: 1180, height: 850 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  let previous = "zh-CN";
  const checked = [];
  const offered = [...languageOptions.filter(option => option.id !== "system" && option.id !== "en-US"),
    languageOptions.find(option => option.id === "en-US")];
  for (const option of offered) {
    const oldApp = await loadAppCopy(previous);
    const oldSettings = await loadSettingsText(previous);
    await page.getByRole("navigation").getByRole("button", { name: oldApp.page.settings, exact: true }).click();
    await page.getByRole("button", { name: oldSettings.sections.appearance, exact: true }).click();
    await page.getByRole("button", { name: oldSettings.appearance.languageSelectLabel, exact: true }).click();
    await page.getByRole("option", { name: `${option.nativeLabel} · ${option.label}`, exact: true }).click();
    const save = page.getByRole("button", { name: oldSettings.actions.save, exact: true });
    if (option.id !== previous) await save.click();
    await page.waitForFunction(language => document.documentElement.lang === language, option.id);
    const app = await loadAppCopy(option.id);
    const settings = await loadSettingsText(option.id);
    await page.getByText(settings.appearance.settingsLanguageDescription, { exact: true }).waitFor();
    await page.getByRole("button", { name: settings.appearance.languageSelectLabel, exact: true }).waitFor();

    await page.getByRole("navigation").getByRole("button", { name: app.page.control, exact: true }).click();
    await page.locator(".control-group h3").filter({ hasText: app.control.kind.gpu }).waitFor();
    await page.locator(".control-readings dt").filter({ hasText: app.control.presentation.labels["gpu.temperature-limit"] }).waitFor();
    await page.locator(".control-capability-actual").filter({ hasText: app.control.presentation.toggleOff }).first().waitFor();
    await page.getByRole("radio", { name: app.control.ownership.app, exact: true }).waitFor();

    await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
    stage = "idle";
    await page.getByRole("button", { name: settings.sections.updates, exact: true }).click();
    await page.getByRole("checkbox", { name: settings.updates.autoUpdateTitle, exact: true }).waitFor();
    await page.getByText(settings.updates.versionsDescription, { exact: true }).waitFor();
    await page.getByText(settings.updates.latest, { exact: true }).waitFor();
    await page.getByText(settings.updates.latestStable, { exact: true }).waitFor();
    const oldVersion = page.locator('input[value="version:0.3.1"]');
    if (await oldVersion.count() !== 0) throw new Error(`${option.id}: minor versions were not collapsed`);
    await page.locator(".version-selector-expand").click();
    if (!await oldVersion.isDisabled()) throw new Error(`${option.id}: an old version can be selected`);
    await page.getByText(settings.updates.unavailableReasons["只能选择高于当前安装版本的发行版。"], { exact: true }).waitFor();
    await page.locator('input[value="latest"]').check();
    const confirmation = page.waitForEvent("dialog");
    const click = page.getByRole("button", { name: settings.updates.prepare, exact: true }).click();
    const dialog = await confirmation;
    if (dialog.message() !== settings.updates.confirmPrepare("0.3.9")) throw new Error(`${option.id}: confirmation uses the wrong language`);
    await dialog.dismiss();
    await click;
    stage = "verifying";
    await page.getByRole("button", { name: settings.sections.appearance, exact: true }).click();
    await page.getByRole("button", { name: settings.sections.updates, exact: true }).click();
    await page.getByRole("status").filter({ hasText: settings.updates.stages.verifying }).waitFor();
    const layout = await usabilityProbe(page);
    if (layout.document.scrollWidth > layout.document.clientWidth
      || (layout.shell && layout.shell.scrollWidth > layout.shell.clientWidth)
      || layout.visibleUnnamedInputs.length > 0) {
      throw new Error(`${option.id}: translated UI overflows or contains unnamed visible controls`);
    }
    checked.push(option.id);
    previous = option.id;
  }
  if (failures.length) throw new Error(failures.join("\n"));
  return { languages: checked, actualSavedSwitching: true, translatedControlAndUpdateUi: true,
    foldedHistory: true, downgradeDisabled: true, localizedConsentCancelled: true };
}
