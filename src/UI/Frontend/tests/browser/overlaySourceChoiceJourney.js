import { NOT_GHOST } from "./liveLocator.js";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { frontendPreferencesKey, writeFrontendPreferences } from "./frontendPreferencesFixture.js";

const languages = ["zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES", "ru-RU"];
const item = { kind: "metric", id: "cpu.temperature" };
// 模拟后端项目录里 cpu.temperature 的第二个候选来源（mockFrontendServer.mjs targetedRecordingCatalog）。
const alternate = "cpu.temperature@board";

/**
 * 浮层的每一项都能选来源、监控间隔和同步精度（docs/targeted_recording_views_2026-10-30.md §3.9，用户原话
 * 「具体每一项也都能选择监控间隔」「监控的三档精度，每个监控项的来源等等」）：
 * 浮层设置用共用的项选择器（用途 overlay，最多 64 项）；只给浮层用的帧率、1% 低帧、0.1% 低帧在这里是已知项。
 * 加入整机 CPU 温度，选第二个来源、间隔 2500 ms、精度高并保存：整项存在前端偏好缓存的 items 里，
 * 发给后端的写请求（启动模块登记等）都不带它；重新打开仍是这些选择。移除这一项时它的选择一起去掉。
 * 最后在九种语言下核对设置列表里这一项的摘要（来源、间隔、精度）文字。
 * 原生壳按软件订阅、把 items 放进请求体的部分由 OverlaySubscriptionConsumerTests / PerformanceOverlayNativeUiTests 核对。
 */
export default async function overlaySourceChoiceJourney(page, baseUrl) {
  await page.setViewportSize({ width: 1200, height: 950 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const app = await loadAppCopy("zh-CN");
  const editor = page.locator(`.overlay-settings${NOT_GHOST}`);
  const open = async (copy) => {
    await page.getByRole("button", { name: copy.page.components, exact: true }).click();
    await page.getByRole("tablist", { name: copy.management.categoryNav }).getByRole("tab", { name: new RegExp(copy.softwareKind.other) }).click();
    await page.locator(".software-item").filter({ hasText: "示例软件" }).first().getByRole("button", { name: copy.management.softwareSettings }).click();
    await page.getByRole("tab", { name: copy.performanceOverlay.tab, exact: true }).click();
    await page.locator(`.overlay-settings-fields${NOT_GHOST}`).waitFor({ state: "visible" });
  };
  const close = async (copy) => {
    await page.getByRole("dialog", { name: "示例软件", exact: true }).getByRole("button", { name: copy.softwareDetail.close, exact: true }).first().click();
  };
  const openPicker = async (copy) => {
    await editor.getByRole("button", { name: copy.performanceOverlay.chooseItems, exact: true }).click();
    const dialog = page.getByRole("dialog", { name: copy.performanceOverlay.pickerTitle, exact: true });
    await dialog.locator(".item-picker").waitFor({ state: "visible" });
    return dialog;
  };
  const cachedItems = async () => {
    const cache = await page.evaluate((key) => JSON.parse(localStorage.getItem(key) ?? "null"), frontendPreferencesKey);
    // 这个旅程只编辑示例软件一个浮层。
    const overlays = Object.values(cache?.overlays ?? {});
    if (overlays.length > 1) throw new Error(`Expected one cached overlay, found ${overlays.length}`);
    return overlays[0]?.items ?? null;
  };
  const cachedItem = async () => (await cachedItems())?.find((entry) => entry.kind === item.kind && entry.id === item.id);
  // 浮层开关和方式存在后端的启动模块登记里（可能经提权通道发送）；项只在前端偏好缓存里，
  // 所以任何发给后端的写请求都不能带它们。
  const backendBodies = [];
  page.on("request", (request) => {
    if (request.method() !== "GET" && request.url().includes("/api/")) backendBodies.push(request.postData() ?? "");
  });
  const save = async (copy) => {
    const button = editor.getByRole("button", { name: copy.softwareDetail.save, exact: true });
    await button.click();
    await page.waitForFunction(() => !document.querySelector('.overlay-settings input[type="number"]')?.disabled);
    await page.waitForFunction((label) => [...document.querySelectorAll(".overlay-settings button")]
      .find((element) => element.innerText?.trim() === label)?.disabled === true, copy.softwareDetail.save);
    for (const body of backendBodies) {
      if (body.includes("syncPrecision") || body.includes(alternate)) {
        throw new Error(`An overlay item entered a backend request: ${body}`);
      }
    }
  };
  const expectedSummary = (copy) => copy.performanceOverlay.itemSummary(`${item.id} · board`,
    copy.targetedReport.reportInfo.intervalValue(2500), copy.targetedReport.itemPicker.precisions.high);
  const summaryOf = async () => (await editor.locator(".overlay-settings-metrics li").last()
    .locator(".overlay-settings-item-summary").textContent())?.trim();

  await open(app);
  let picker = await openPicker(app);
  // 默认的帧率、1% 低帧、帧时间是浮层项：浮层用途的目录认识它们，不报“项目录里没有”。
  if (await picker.locator(".item-picker-row .item-picker-note.warning").count() !== 0) {
    throw new Error("The overlay picker does not know the overlay-only frames items");
  }
  const before = await picker.locator(".item-picker-row").count();
  await picker.getByRole("searchbox", { name: app.performanceOverlay.picker.search, exact: true }).fill(item.id);
  await picker.locator(".item-picker-catalog .item-picker-group").filter({ has: page.locator(".item-picker-entry") }).last()
    .locator(".item-picker-entry button").first().click();
  if (await picker.locator(".item-picker-row").count() !== before + 1) throw new Error("Adding an overlay item did not add one row");
  const row = picker.locator(".item-picker-row").last();
  await row.getByRole("combobox").nth(0).click();
  await page.getByRole("option", { name: `${item.id} · board`, exact: true }).click();
  await row.locator("input[type='text']").fill("2500");
  await row.getByRole("combobox").nth(1).click();
  await page.getByRole("option", { name: app.targetedReport.itemPicker.precisions.high, exact: true }).click();
  await picker.getByRole("button", { name: app.metricPicker.confirm, exact: true }).click();
  await save(app);
  const chosen = await cachedItem();
  if (JSON.stringify(chosen) !== JSON.stringify({ ...item, source: alternate, intervalMs: 2500, syncPrecision: "high" })) {
    throw new Error(`The chosen overlay item was not cached: ${JSON.stringify(await cachedItems())}`);
  }
  if (await summaryOf() !== expectedSummary(app)) throw new Error(`zh-CN: overlay item summary ${await summaryOf()}`);
  await close(app);

  await open(app);
  picker = await openPicker(app);
  const reopened = picker.locator(".item-picker-row").last();
  if (!(await reopened.getByRole("combobox").nth(0).textContent())?.includes(`${item.id} · board`)
    || await reopened.locator("input[type='text']").inputValue() !== "2500") {
    throw new Error("Reopening lost the overlay item's source or interval");
  }
  await picker.getByRole("button", { name: app.metricPicker.cancel, exact: true }).click();

  // 移除这一项：它的来源、间隔和精度一起去掉。
  await editor.locator(".overlay-settings-metrics li").last()
    .getByRole("button", { name: app.performanceOverlay.removeItem, exact: true }).click();
  await save(app);
  if (await cachedItem() !== undefined) throw new Error(`Removing the item left it cached: ${JSON.stringify(await cachedItems())}`);
  await close(app);

  // 九种语言的文字：直接把这一项写回偏好缓存，再逐个语言打开。
  await writeFrontendPreferences(page, (value) => {
    const overlay = Object.values(value.overlays ?? {})[0];
    overlay.items = [...overlay.items, { ...item, source: alternate, intervalMs: 2500, syncPrecision: "high" }];
  });
  const localized = [];
  for (const language of languages) {
    const copy = await loadAppCopy(language);
    await writeFrontendPreferences(page, (value) => { value.appearance.language = language; });
    await page.reload({ waitUntil: "domcontentloaded" });
    await open(copy);
    const summary = await summaryOf();
    if (summary !== expectedSummary(copy)) throw new Error(`${language}: overlay item summary ${summary}, expected ${expectedSummary(copy)}`);
    localized.push({ language, summary });
    await close(copy);
  }
  await writeFrontendPreferences(page, (value) => { value.appearance.language = "system"; });
  return { chosen, localized };
}
