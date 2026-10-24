import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";

export default async function realtimeActionBudgetSettingsJourney(page, baseUrl) {
  const failures = [];
  const patches = [];
  const assert = (condition, message) => { if (!condition) throw new Error(message); };
  page.on("pageerror", error => failures.push(String(error)));
  page.on("request", request => {
    if (request.method() === "PATCH" && new URL(request.url()).pathname === "/api/settings/app") patches.push(request.postDataJSON());
  });
  await page.setViewportSize({ width: 800, height: 500 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  let previous = "zh-CN";
  const checked = [];
  for (const option of languageOptions.filter(option => option.id !== "system")) {
    const oldApp = await loadAppCopy(previous);
    const oldSettings = await loadSettingsText(previous);
    await page.getByRole("navigation").getByRole("button", { name: oldApp.page.settings, exact: true }).click();
    await page.getByRole("button", { name: oldSettings.sections.appearance, exact: true }).click();
    await page.getByRole("button", { name: oldSettings.appearance.languageSelectLabel, exact: true }).click();
    await page.getByRole("option", { name: `${option.nativeLabel} · ${option.label}`, exact: true }).click();
    if (option.id !== previous) await page.getByRole("button", { name: oldSettings.actions.save, exact: true }).click();
    await page.waitForFunction(language => document.documentElement.lang === language, option.id);
    const settings = await loadSettingsText(option.id);
    await page.getByRole("button", { name: settings.sections.performance, exact: true }).click();
    await page.getByText(settings.performance.realtimeActionBudgetDescription, { exact: true }).waitFor();
    const group = page.getByRole("radiogroup", { name: settings.performance.realtimeActionBudgetTitle, exact: true });
    for (const mode of settings.performance.realtimeActionBudgetModeOptions) {
      const radio = group.getByRole("radio", { name: mode.label, exact: true });
      assert(await radio.isVisible(), `${option.id}: missing ${mode.id} mode`);
      assert(await radio.isChecked() === (mode.id === "fixed"), `${option.id}: wrong default mode`);
    }
    for (const [label, value, max] of [
      [settings.performance.realtimeActionBudgetCountLabel, "32", "8192"],
      [settings.performance.realtimeActionBudgetPercentLabel, "5", "100"]
    ]) {
      const input = page.getByRole("spinbutton", { name: label, exact: true });
      assert(await input.isEditable() && await input.inputValue() === value && await input.getAttribute("min") === "1"
        && await input.getAttribute("max") === max, `${option.id}: invalid budget control: ${label}`);
    }
    checked.push(option.id);
    previous = option.id;
  }
  const app = await loadAppCopy(previous);
  const settings = await loadSettingsText(previous);
  const count = page.getByRole("spinbutton", { name: settings.performance.realtimeActionBudgetCountLabel, exact: true });
  const percent = page.getByRole("spinbutton", { name: settings.performance.realtimeActionBudgetPercentLabel, exact: true });
  const mode = page.getByRole("radiogroup", { name: settings.performance.realtimeActionBudgetTitle, exact: true });
  const option = id => settings.performance.realtimeActionBudgetModeOptions.find(option => option.id === id).label;
  const save = async expected => {
    const saved = page.waitForResponse(response => response.request().method() === "PATCH" && new URL(response.url()).pathname === "/api/settings/app");
    await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
    assert((await saved).ok(), "Budget save failed");
    assert(JSON.stringify(patches.at(-1)?.changes?.performance) === JSON.stringify(expected), `Wrong budget patch: ${JSON.stringify(patches.at(-1))}`);
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
    await page.getByRole("button", { name: settings.sections.performance, exact: true }).click();
  };
  await count.fill("64"); await count.blur();
  await percent.fill("12"); await percent.blur();
  await save({ realtimeActionBudgetCount: 64, realtimeActionBudgetPercent: 12 });
  assert(await count.inputValue() === "64" && await percent.inputValue() === "12", "Both budget values did not survive reload");
  for (const id of ["percent", "fixed"]) {
    await mode.getByRole("radio", { name: option(id), exact: true }).click();
    await save({ realtimeActionBudgetMode: id });
    assert(await mode.getByRole("radio", { name: option(id), exact: true }).isChecked(), `Mode ${id} did not survive reload`);
    assert(await count.isEditable() && await percent.isEditable(), `Both budget values must remain editable in ${id} mode`);
    assert(await count.inputValue() === "64" && await percent.inputValue() === "12", "Mode switch lost inactive value");
  }
  await count.fill("8193"); await count.blur();
  await percent.fill("0"); await percent.blur();
  await save({ realtimeActionBudgetCount: 8192, realtimeActionBudgetPercent: 1 });
  assert(await count.inputValue() === "8192" && await percent.inputValue() === "1", "Clamped budget did not persist");
  assert(failures.length === 0, failures.join("\n"));
  return { languages: checked, defaults: ["fixed", 32, 5], savedValues: [64, 12], inactiveValuesRetained: true, bounds: [[1, 8192], [1, 100]] };
}
