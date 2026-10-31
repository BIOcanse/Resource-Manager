import type { Page } from "playwright-core";
import assert from "node:assert/strict";
import { mkdir } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { writeFrontendPreferences } from "./frontendPreferencesFixture.ts";
import { enableMetricFields, npuBusyCardId, setDashboardSettings } from "./metricFieldsProbe.ts";

/**
 * 设备字段那一路报错（mock 的 invalidMetricFields：推回解不开的设备值帧）：
 * - 实时指标区里出现一条设备字段读数的提示，写明只影响用设备字段的监控项；整块实时指标不报错；
 * - 用设备字段的槽位写「读取失败」（警示色、悬停说明），不写「无读数」、也不一直画等待条；设备不在的槽位照常写「设备不在」；
 * - 原有指标的卡片照常显示读数；
 * - 设备值恢复后提示消失、读数回来。
 * 简体中文和英文各走一遍，各截一张监控面板。
 */
export default async function metricFieldFailureJourney(page: Page, baseUrl: string) {
  const shots = process.env.RM_BROWSER_ARTIFACT_DIR
    ?? fileURLToPath(new URL("../../../../../.local/browser-gate/metric-field-failure/", import.meta.url));
  await mkdir(shots, { recursive: true });
  const errors: string[] = [];
  page.on("pageerror", error => errors.push(String(error)));
  const scenario = async (value: { invalidMetricFields: boolean }) => {
    const response = await fetch(`${baseUrl}/__test/scenario`, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(value)
    });
    assert.ok(response.ok, "Scenario update failed");
  };
  await enableMetricFields(baseUrl);
  await setDashboardSettings(page, [
    { id: "cpu", main: "cpu.usage", small: [] },
    { id: "fields", main: npuBusyCardId, mainBinding: { scopeKind: "device-field", scopeKey: "npu|fixture" },
      small: ["npu_all_engine_busy_percent@npu|removed"], smallBindings: [{ scopeKind: "device-field", scopeKey: "npu|removed" }] }
  ]);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const results: { language: string; notice: string; failed: string; screenshot: string }[] = [];
  for (const language of ["zh-CN", "en-US"] as const) {
    const app = await loadAppCopy(language);
    await scenario({ invalidMetricFields: false });
    await writeFrontendPreferences(page, (value) => { value.appearance.language = language; });
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.waitForFunction(expected => document.documentElement.lang === expected, language);
    await page.getByRole("navigation").getByRole("button", { name: app.page.monitor, exact: true }).click();
    const card = (id: string) => page.locator(`.metric-card[data-card-id="${id}"]`);
    const fieldValue = card("fields").locator(".metric-main-content .metric-value");
    const absentValue = card("fields").locator(".metric-small-row .metric-value");
    const legacyValue = card("cpu").locator(".metric-main-content .metric-value");
    const reading = `${(137.5).toLocaleString(language, { useGrouping: false })}%`;
    await fieldValue.filter({ hasText: reading }).waitFor({ timeout: 15_000 });
    await legacyValue.locator(".metric-number").waitFor({ timeout: 15_000 });
    const noticeTitle = app.observationNotice.unavailable(app.monitorPage.deviceFieldReadings);
    const notice = page.locator(".observation-state.error").filter({ hasText: noticeTitle });
    assert.equal(await notice.count(), 0, `${language}: the field notice shows while the fields stream is healthy`);

    await scenario({ invalidMetricFields: true });
    await notice.waitFor({ timeout: 15_000 });
    assert.equal(await notice.getAttribute("role"), "alert", `${language}: the field notice is not an alert`);
    assert.ok((await notice.textContent())!.includes(app.monitorPage.deviceFieldReadingsFailed), `${language}: the notice does not say which metrics are affected`);
    assert.equal(await notice.getByRole("button").count(), 0, `${language}: the push source has no manual refresh, so the notice must not offer one`);
    assert.equal(await page.locator(".observation-state.error").count(), 1, `${language}: the field failure spread to another notice`);
    await fieldValue.filter({ hasText: app.dashboard.readingFailed }).waitFor({ timeout: 15_000 });
    assert.equal(await fieldValue.getAttribute("data-full-content"), app.dashboard.readingFailedHint, `${language}: the failed slot lacks its hint`);
    assert.ok(await fieldValue.evaluate(element => element.classList.contains("failed") && !element.classList.contains("pending")),
      `${language}: the failed slot is not marked failed`);
    await absentValue.filter({ hasText: app.dashboard.deviceAbsent }).waitFor();
    // 读取失败和其他没有值的写法（设备不在、无读数）颜色不同。
    assert.notEqual(await fieldValue.evaluate(element => getComputedStyle(element).color),
      await absentValue.evaluate(element => getComputedStyle(element).color), `${language}: a failed slot looks like a slot without a value`);
    assert.notEqual((await fieldValue.textContent())!.trim(), app.dashboard.noReading, `${language}: a failed slot reads like no reading`);
    assert.ok(await legacyValue.locator(".metric-number").isVisible(), `${language}: the legacy metric card lost its reading`);
    assert.notEqual((await legacyValue.textContent())!.trim(), app.dashboard.readingFailed, `${language}: the legacy card was marked failed`);
    const shot = join(shots, `metric-field-failure-${language}.png`);
    await page.locator("#monitorPage").screenshot({ path: shot });

    await scenario({ invalidMetricFields: false });
    await notice.waitFor({ state: "detached", timeout: 15_000 });
    await fieldValue.filter({ hasText: reading }).waitFor({ timeout: 15_000 });
    results.push({ language, notice: noticeTitle, failed: app.dashboard.readingFailed, screenshot: shot });
  }
  assert.deepEqual(errors, []);
  return { languages: results };
}
