import type { Page } from "playwright-core";
import assert from "node:assert/strict";
import { mkdir } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { writeFrontendPreferences } from "./frontendPreferencesFixture.ts";

/**
 * 智能调度报告的记录列表：后端送来 12 条，默认列出最近 8 条，下方按钮写明全部条数；
 * 按下后 12 条全部列出（最新的在前），按钮改为只显示最近 8 条；再按回到 8 条。
 * 记录不多于 8 条时没有这个按钮。报告仍在详细信息的同一个分页里。简体中文和英文各截一张。
 */
export default async function smartDetailsAllRecordsJourney(page: Page, baseUrl: string) {
  const shots = process.env.RM_BROWSER_ARTIFACT_DIR
    ?? fileURLToPath(new URL("../../../../../.local/browser-gate/smart-details-all-records/", import.meta.url));
  await mkdir(shots, { recursive: true });
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(String(error)));
  const placements = (count: number) => Array.from({ length: count }, (_, index) => {
    const updatedAt = new Date(Date.UTC(2026, 9, 24, 12, index)).toISOString();
    return {
      targetId: `software:frontend-harness.software:${index}`,
      processName: `Harness ${index + 1}`, processId: 100 + index, processStartKey: `13370000000000${String(index).padStart(4, "0")}`,
      softwareId: "frontend-harness.software",
      resourceKind: index % 2 ? "gpu" : "cpu",
      records: [{ kind: index % 2 ? "gpu_preference" : "cpu_affinity", recordId: `placement-${index}` }],
      appliedAt: updatedAt,
      updatedAt
    };
  });
  const scenario = async (value: Record<string, unknown>) => {
    const response = await fetch(`${baseUrl}/__test/scenario`, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(value)
    });
    assert.equal(response.status, 200);
  };
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  const results: { language: string; toggle: string; collapse: string; screenshot: string }[] = [];
  for (const language of ["zh-CN", "en-US"] as const) {
    const app = await loadAppCopy(language);
    await scenario({ optimizationRuntime: true, smartPlacements: placements(12) });
    await writeFrontendPreferences(page, (value) => { value.appearance.language = language; });
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.waitForFunction(expected => document.documentElement.lang === expected, language);
    await page.getByRole("navigation").getByRole("button", { name: app.page.details, exact: true }).click();
    await page.getByRole("tab", { name: app.misc.detailsTab.report, exact: true }).click();
    const report = page.locator(".smart-details-report");
    const records = report.locator(".smart-details-record");
    const names = async () => (await records.locator(".smart-details-record-main strong").allTextContents()).map(text => text.trim());
    await records.first().waitFor();
    assert.equal(await records.count(), 8, `${language}: the report does not start with the latest eight records`);
    const toggle = report.getByRole("button", { name: app.smartReport.showAllRecords(12), exact: true });
    await toggle.waitFor();
    assert.equal(await toggle.getAttribute("aria-expanded"), "false");
    const latest = await names();
    assert.ok(/^Harness 12(?!\d)/.test(latest[0]) && /^Harness 5(?!\d)/.test(latest[7]), `${language}: the visible records are not the latest: ${latest}`);

    await toggle.click();
    await page.waitForFunction(() => document.querySelectorAll<HTMLElement>(".smart-details-report .smart-details-record").length === 12);
    const all = await names();
    assert.ok(/^Harness 1(?!\d)/.test(all[11]) && new Set(all).size === 12, `${language}: not every record is listed: ${all}`);
    const collapse = report.getByRole("button", { name: app.smartReport.showRecentRecords(8), exact: true });
    assert.equal(await collapse.getAttribute("aria-expanded"), "true");
    // 按钮在报告面板里，不溢出。
    const layout = await report.evaluate(element => ({ scroll: element.scrollWidth, client: element.clientWidth }));
    assert.ok(layout.scroll <= layout.client + 1, `${language}: the report overflows: ${JSON.stringify(layout)}`);
    const shot = join(shots, `smart-details-all-records-${language}.png`);
    await report.screenshot({ path: shot });
    await collapse.click();
    await page.waitForFunction(() => document.querySelectorAll<HTMLElement>(".smart-details-report .smart-details-record").length === 8);

    // 不多于 8 条：没有按钮。
    await scenario({ smartPlacements: placements(8) });
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.getByRole("navigation").getByRole("button", { name: app.page.details, exact: true }).click();
    await page.getByRole("tab", { name: app.misc.detailsTab.report, exact: true }).click();
    await records.first().waitFor();
    await page.waitForFunction(() => document.querySelectorAll<HTMLElement>(".smart-details-report .smart-details-record").length === 8);
    assert.equal(await report.locator(".smart-details-records-toggle").count(), 0, `${language}: eight records still offer a toggle`);
    results.push({ language, toggle: app.smartReport.showAllRecords(12), collapse: app.smartReport.showRecentRecords(8), screenshot: shot });
  }
  assert.deepEqual(errors, []);
  return { languages: results };
}
