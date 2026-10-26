import { liveText } from "./liveLocator.js";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";

export default async function gpuOverflowSettingsJourney(page, baseUrl) {
  const failures = [];
  page.on("pageerror", error => failures.push(String(error)));
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
    await liveText(page, settings.performance.gpuOverflowTitle).waitFor();
    await liveText(page, settings.performance.gpuOverflowDescription).waitFor();
    for (const [label, value] of [
      [settings.performance.gpuOverflowUsageLabel, "95"],
      [settings.performance.gpuOverflowDedicatedMemoryLabel, "80"]
    ]) {
      const input = page.getByRole("spinbutton", { name: label, exact: true });
      if (await input.inputValue() !== value || await input.getAttribute("min") !== "1"
        || await input.getAttribute("max") !== "100") throw new Error(`${option.id}: invalid GPU overflow control: ${label}`);
    }
    checked.push(option.id);
    previous = option.id;
  }
  if (failures.length) throw new Error(failures.join("\n"));
  return { languages: checked, gpuOverflowControls: 2, defaults: [95, 80], bounds: [1, 100] };
}
