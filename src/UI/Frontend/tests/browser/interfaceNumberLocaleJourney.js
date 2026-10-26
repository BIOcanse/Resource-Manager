import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { seedFrontendPreferences, writeFrontendPreferences, customRefresh } from "./frontendPreferencesFixture.js";

export default async function interfaceNumberLocaleJourney(page, baseUrl) {
  const checks = [];
  const scenario = await page.request.post(`${baseUrl}/__test/scenario`, { data: { metricCpuUsage: 88.3, gpuPressureFixture: true, optimizationRuntime: true, gpuDedicatedMemoryMetrics: true } });
  assert(scenario.ok());
  await seedFrontendPreferences(page, value => {
    value.appearance.language = "fr-FR";
    value.refresh.monitor = customRefresh(500);
  });
  for (const language of ["fr-FR", "de-DE"]) {
    if (language === "de-DE") {
      await writeFrontendPreferences(page, value => { value.appearance.language = language; });
    }
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    const app = await loadAppCopy(language);
    const metric = page.locator(".metric-card .metric-value").first();
    await metric.waitFor();
    await page.waitForFunction(() => /\d/.test(document.querySelector(".metric-card .metric-value")?.textContent ?? ""));
    const reading = await metric.textContent();
    checks.push({ language, reading });
    // GPU cards can push the virtual table out of the viewport and retire its demand.
    await page.locator(".resource-table-panel").scrollIntoViewIfNeeded();
    const tablePercent = page.locator('.resource-table-row[aria-hidden="false"] .resource-table-cell-text').filter({ hasText: /^\d+,\d+%$/ }).first();
    await tablePercent.waitFor({ timeout: 15000 });
    checks.at(-1).table = await tablePercent.textContent();
    await page.getByRole("navigation").getByRole("button", { name: app.page.details, exact: true }).click();
    await page.getByRole("tab", { name: app.misc.detailsTab.cpu, exact: true }).click();
    const usage = page.locator(".cpu-ccd-header small").first();
    await usage.waitFor();
    checks.at(-1).cpu = await usage.textContent();
    await page.getByText(`0,5 · ${app.cpuBaseline.defaultActive}`, { exact: true }).waitFor();
    await page.getByRole("tab", { name: app.misc.detailsTab.gpu, exact: true }).click();
    const gpu = page.locator(".gpu-pressure-grid .gpu-pressure-meter strong").first();
    await gpu.waitFor();
    checks.at(-1).gpu = await gpu.textContent();
  }
  for (const check of checks) {
    assert.match(check.reading, /\d,\d/, `${check.language} metric: ${check.reading}`);
    assert.match(check.table, /\d,\d/, `${check.language} resource table: ${check.table}`);
    assert.equal(check.cpu, "31,0%", `${check.language} CPU`);
    assert.equal(check.gpu, "96,0%", `${check.language} GPU`);
  }
  return checks;
}
