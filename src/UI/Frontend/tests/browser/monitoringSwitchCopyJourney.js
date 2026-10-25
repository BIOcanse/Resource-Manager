import assert from "node:assert/strict";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { pointPreferences } from "../../src/preferences/frontendPreferences.ts";

export default async function monitoringSwitchCopyJourney(page, baseUrl) {
  const languages = languageOptions.filter(option => option.id !== "system");
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  for (const { id } of languages) {
    const cache = pointPreferences();
    cache.appearance.language = id;
    await page.evaluate(value => localStorage.setItem("resource-manager:frontend-preferences:v1", JSON.stringify(value)), cache);
    await page.reload({ waitUntil: "domcontentloaded" });
    const app = await loadAppCopy(id);
    const text = await loadSettingsText(id);
    await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
    await page.getByRole("button", { name: text.sections.performance, exact: true }).click();
    await page.getByText(text.performance.smartMonitoringDescription, { exact: true }).waitFor();
    const radios = page.getByRole("radiogroup", { name: text.performance.smartMonitoringTitle, exact: true }).getByRole("radio");
    assert.equal(await radios.count(), 2);
    for (const radio of await radios.all()) {
      assert(await radio.isDisabled(), `${id}: old monitoring switch must be disabled`);
      assert.equal(await radio.getAttribute("title"), text.performance.smartMonitoringDescription);
    }
  }
  assert.deepEqual(errors, []);
  return { languages: languages.map(option => option.id), disabledReasonRendered: true };
}
