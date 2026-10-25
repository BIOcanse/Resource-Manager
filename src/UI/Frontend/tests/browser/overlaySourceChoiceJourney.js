import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { metricFieldDevices, readingSourceDevices } from "./metricFieldsProbe.js";
import { frontendPreferencesKey, writeFrontendPreferences } from "./frontendPreferencesFixture.js";

const languages = ["zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES", "ru-RU"];
const address = "gpu.5.temperature";
const alternate = { fieldName: "gpu_amd_edge_temperature_celsius", deviceIdentity: "gpu|reading" };

/**
 * 浮窗一项先选项目、再选来源（docs/metric_item_source_choice_2026-10-25.md，简体中文）：
 * 浮窗目录里有核显温度的部件地址 gpu.5.temperature，后端给它列了三个候选来源、当前选中 ADLX 次选。
 * 把它加进浮窗后，这一项下面摆“来源”（统一分段样式）：第一项自动选择，写出后端现在选的 ADLX；
 * 其后是后端候选（按后端顺序，本机首选标出）。只有一个来源的 target.* 不摆。
 * 选 ADLX 次选并保存：来源和项目一起存在前端偏好缓存里，发给后端的任何请求（启动模块登记等）都不带来源；
 * 重新打开仍选着它。改回自动选择保存后缓存里没有这一项的来源；移除这一项时它的来源一起去掉。
 * 最后在九种语言下核对来源名称、自动选择和首选标记的文字。
 * 原生壳按 fields= 订阅、按设备取值的部分由 OverlaySubscriptionConsumerTests / PerformanceOverlayNativeUiTests 核对。
 */
export default async function overlaySourceChoiceJourney(page, baseUrl) {
  const response = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ metricFieldDevices: [...metricFieldDevices, ...readingSourceDevices], metricCatalogAddresses: "reading-source" })
  });
  if (!response.ok) throw new Error("Unable to enable the reading-source fixture");
  await page.setViewportSize({ width: 1200, height: 950 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const app = await loadAppCopy("zh-CN");
  const editor = page.locator(".overlay-settings");
  const open = async (copy) => {
    await page.getByRole("button", { name: copy.page.components, exact: true }).click();
    await page.getByRole("navigation", { name: copy.management.categoryNav }).getByRole("button", { name: new RegExp(copy.softwareKind.other) }).click();
    await page.locator(".software-item").filter({ hasText: "示例软件" }).first().getByRole("button", { name: copy.management.softwareSettings }).click();
    await page.getByRole("tab", { name: copy.performanceOverlay.tab, exact: true }).click();
    await page.locator(".overlay-settings-fields").waitFor({ state: "visible" });
  };
  const close = async (copy) => {
    await page.getByRole("dialog", { name: "示例软件", exact: true }).getByRole("button", { name: copy.softwareDetail.close, exact: true }).first().click();
  };
  const sourcesOf = (copy) => editor.getByRole("radiogroup", { name: copy.resourceTable.columnSourceLabel(copy.metricLabel["gpu.{index}.temperature"]?.("5") ?? address), exact: true });
  const cachedSources = async () => {
    const cache = await page.evaluate((key) => JSON.parse(localStorage.getItem(key) ?? "null"), frontendPreferencesKey);
    // 这个旅程只编辑示例软件一个浮窗。
    const overlays = Object.values(cache?.overlays ?? {});
    if (overlays.length > 1) throw new Error(`Expected one cached overlay, found ${overlays.length}`);
    return overlays[0]?.metricSources ?? null;
  };
  // 浮窗开关和方式存在后端的启动模块登记里（可能经提权通道发送）；来源只在前端偏好缓存里，
  // 所以任何发给后端的写请求都不能带来源。
  const backendBodies = [];
  page.on("request", (request) => {
    if (request.method() !== "GET" && request.url().includes("/api/")) backendBodies.push(request.postData() ?? "");
  });
  const save = async (copy) => {
    const button = editor.getByRole("button", { name: copy.softwareDetail.save, exact: true });
    await button.click();
    await page.waitForFunction(() => !document.querySelector('.overlay-settings input[type="number"]')?.disabled);
    await page.waitForFunction((label) => [...document.querySelectorAll(".overlay-settings button")]
      .find((element) => element.textContent?.trim() === label)?.disabled === true, copy.softwareDetail.save);
    for (const body of backendBodies) {
      if (body.includes("metricSources") || body.includes(alternate.fieldName)) {
        throw new Error(`The overlay source entered a backend request: ${body}`);
      }
    }
  };
  const expectedLabels = (copy) => {
    const name = (field) => copy.fieldLabel[field].label;
    const automaticPrefix = copy.metricPicker.sourceAutomatic(name(alternate.fieldName)).split(name(alternate.fieldName))[0];
    return [`${automaticPrefix}${name(alternate.fieldName)}`, name("gpu_amd_apu_temperature_reported_celsius"),
      name("cpu_amd_apu_graphics_temperature_celsius"), name(alternate.fieldName)];
  };
  const checkLabels = async (copy, language) => {
    const radios = sourcesOf(copy).getByRole("radio");
    // 字段目录 5 秒推一次：候选字段到了才摆出来源。
    await sourcesOf(copy).waitFor({ timeout: 15_000 });
    const labels = (await radios.allTextContents()).map((label) => label.trim());
    const expected = expectedLabels(copy);
    if (labels.length !== 4 || !expected.every((label, index) => labels[index].startsWith(label))) {
      throw new Error(`${language}: overlay sources ${JSON.stringify(labels)}, expected ${JSON.stringify(expected)}`);
    }
    if (!labels[1].endsWith(copy.metricPicker.sourcePreferred) || [0, 2, 3].some((index) => labels[index].endsWith(copy.metricPicker.sourcePreferred))) {
      throw new Error(`${language}: only the preferred overlay source carries the mark: ${JSON.stringify(labels)}`);
    }
    return labels;
  };

  await open(app);
  // 把核显温度加进浮窗（共用的多选指标弹窗）。
  await editor.getByRole("button", { name: app.performanceOverlay.chooseMetrics }).click();
  const picker = page.locator("#metricModal");
  await picker.locator(`[data-metric-id="${address}"]`).click();
  await picker.getByRole("button", { name: app.metricPicker.confirm, exact: true }).click();
  const labels = await checkLabels(app, "zh-CN");
  if (await editor.locator(".overlay-settings-metric-source").count() !== 1) throw new Error("A single-source overlay item offered a source choice");
  const sources = sourcesOf(app);
  if (!await sources.evaluate((element) => element.classList.contains("segmented"))
    || !await sources.getByRole("radio").first().evaluate((element) => element.classList.contains("segment"))) {
    throw new Error("The overlay source choice does not use the shared segmented look");
  }
  if (await sources.getByRole("radio").nth(0).getAttribute("aria-checked") !== "true") throw new Error("Automatic is not the checked overlay source");

  // ADLX 次选。
  await sources.getByRole("radio").nth(3).click();
  await save(app);
  const chosen = await cachedSources();
  if (JSON.stringify(chosen?.[address]) !== JSON.stringify(alternate)) throw new Error(`The chosen overlay source was not cached: ${JSON.stringify(chosen)}`);
  await close(app);
  await open(app);
  await sourcesOf(app).waitFor({ timeout: 15_000 });
  if (await sourcesOf(app).getByRole("radio").nth(3).getAttribute("aria-checked") !== "true") throw new Error("Reopening lost the overlay source");

  // 自动选择：缓存里没有这一项的来源。
  await sourcesOf(app).getByRole("radio").nth(0).click();
  await save(app);
  if ((await cachedSources())?.[address] !== undefined) throw new Error("Automatic kept a cached overlay source");

  // 移除这一项：来源一起去掉。
  await sourcesOf(app).getByRole("radio").nth(3).click();
  const item = editor.locator(".overlay-settings-metrics li").filter({ has: page.locator(".overlay-settings-metric-source") });
  await item.getByRole("button", { name: app.performanceOverlay.removeMetric, exact: true }).click();
  await save(app);
  const removed = await cachedSources();
  if (removed === null || Object.keys(removed).length !== 0) throw new Error(`Removing the item left its source: ${JSON.stringify(removed)}`);
  await close(app);

  // 九种语言的文字：先把核显温度加回去，再逐个语言打开。
  await open(app);
  await editor.getByRole("button", { name: app.performanceOverlay.chooseMetrics }).click();
  await picker.locator(`[data-metric-id="${address}"]`).click();
  await picker.getByRole("button", { name: app.metricPicker.confirm, exact: true }).click();
  await save(app);
  await close(app);
  const localized = [];
  for (const language of languages) {
    const copy = await loadAppCopy(language);
    await writeFrontendPreferences(page, (value) => { value.appearance.language = language; });
    await page.reload({ waitUntil: "domcontentloaded" });
    await open(copy);
    localized.push({ language, labels: await checkLabels(copy, language) });
    await close(copy);
  }
  await writeFrontendPreferences(page, (value) => { value.appearance.language = "system"; });
  return { sources: labels, chosen: chosen?.[address], localized };
}
