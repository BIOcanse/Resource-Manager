import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { metricFieldDevices, readingSourceDevices, readingSourceIds } from "./metricFieldsProbe.js";

/**
 * 先选项目、再选来源（docs/metric_item_source_choice_2026-10-25.md，简体中文）：
 * 选中核显温度后，列表下面出现“来源”，列出同一读取项的全部来源（不支持的不列），本机首选在前并标出；
 * 改选次选来源后，列表里这一项仍标为选中，卡片上换成次选来源自己的名字和数值。
 * 只有一个来源的项目不摆“来源”。不保存布局，只核对弹窗和卡片草稿。
 */
export default async function metricSourceChoiceJourney(page, baseUrl) {
  const response = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ metricFieldDevices: [...metricFieldDevices, ...readingSourceDevices] })
  });
  if (!response.ok) throw new Error("Unable to enable the reading-source fixture");
  const app = await loadAppCopy("zh-CN");
  const text = app.metricPicker;
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation").getByRole("button", { name: app.page.monitor, exact: true }).click();
  await page.getByRole("button", { name: app.monitorPage.editLayoutLabel, exact: true }).click();
  await page.getByRole("button", { name: app.dashboard.addMetric(app.dashboard.detailSlot(1, 3)), exact: true }).click();

  const picker = page.locator("#metricModal");
  const option = (id) => picker.locator(`[data-metric-id="${id}"]`);
  // 字段目录 5 秒推一次。
  await option(readingSourceIds.preferred).waitFor({ timeout: 15_000 });
  const sources = picker.getByRole("radiogroup", { name: text.sourceLabel, exact: true });

  // 只有一个来源：不摆“来源”。
  await option("cpu_all_usage_percent@cpu|fixture").click();
  if (await sources.count() !== 0) throw new Error("A single-source item offered a source choice");

  await option(readingSourceIds.preferred).click();
  await sources.waitFor();
  await picker.getByText(text.sourceHint, { exact: true }).waitFor();
  const radios = sources.getByRole("radio");
  const labels = (await radios.allTextContents()).map((label) => label.trim());
  const preferredName = app.fieldLabel.gpu_amd_apu_temperature_reported_celsius.label;
  const alternateName = app.fieldLabel.gpu_amd_edge_temperature_celsius.label;
  const aliasName = app.fieldLabel.cpu_amd_apu_graphics_temperature_celsius.label;
  // 首选和跨设备别名同名次，首选在前；次选在后；不支持的 NVMe 传感器、别的读取项都不在。
  const expected = [preferredName, aliasName, alternateName];
  if (labels.length !== 3 || !expected.every((name, index) => labels[index].startsWith(name))) {
    throw new Error(`Source choices ${JSON.stringify(labels)}, expected ${JSON.stringify(expected)}`);
  }
  if (!labels[0].endsWith(text.sourcePreferred) || labels.slice(1).some((label) => label.endsWith(text.sourcePreferred))) {
    throw new Error(`Only the preferred source carries the mark: ${JSON.stringify(labels)}`);
  }
  if (await radios.nth(0).getAttribute("aria-checked") !== "true") throw new Error("The chosen field is not the checked source");

  await radios.nth(2).click();
  if (await radios.nth(2).getAttribute("aria-checked") !== "true" || await radios.nth(0).getAttribute("aria-checked") !== "false") {
    throw new Error("Choosing a source did not move the check");
  }
  if (!await option(readingSourceIds.preferred).evaluate((element) => element.classList.contains("selected"))) {
    throw new Error("The item stopped looking selected after choosing another of its sources");
  }
  await picker.getByRole("button", { name: text.confirm, exact: true }).click();

  const row = page.locator(".metric-card").first().locator(".metric-small-row").filter({ hasText: alternateName });
  await row.locator(".metric-value").filter({ hasText: "50.0 °C" }).waitFor({ timeout: 15_000 });
  await page.getByRole("button", { name: app.monitorPage.cancelEditLabel, exact: true }).click();
  return { sources: labels, chosen: readingSourceIds.alternate };
}
