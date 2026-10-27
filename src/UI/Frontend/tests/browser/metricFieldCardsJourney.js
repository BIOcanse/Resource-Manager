import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { NOT_GHOST } from "./liveLocator.js";
import { uiStyles } from "../../src/presentation/uiStyles.ts";
import {
  checkFieldCardDisplay,
  enableMetricFields,
  metricFieldDevices,
  npuBusyCardId,
  readingSourceDevices,
  readingSourceIds,
  addCardSlot,
  readDashboardCache,
  seedFieldCards,
  setDashboardSettings
} from "./metricFieldsProbe.js";

/**
 * 监视控制台卡片上的设备字段（简体中文）：
 * 选择器按设备分组列出规范字段 → 选中并保存 → 保存内容带 device-field 绑定 → 重新加载后读回 →
 * 设备从目录里消失时卡片写「设备不在」、设置保留。设备字段不进原有指标订阅。
 */
export default async function metricFieldCardsJourney(page, baseUrl) {
  await enableMetricFields(baseUrl);
  const app = await loadAppCopy("zh-CN");
  const label = app.fieldLabel.npu_all_engine_busy_percent.label;
  const group = app.dashboard.namedDevice(app.dashboard.deviceKind.npu, "Fixture NPU");
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation").getByRole("button", { name: app.page.monitor, exact: true }).click();

  await page.getByRole("button", { name: app.monitorPage.editLayoutLabel, exact: true }).click();
  await addCardSlot(page, app, 2);
  const picker = page.locator("#metricModal");
  // 字段目录 5 秒推一次，首个推送最长等一个周期。
  const option = picker.locator(`[data-metric-id="${npuBusyCardId}"]`);
  await option.waitFor({ timeout: 15_000 });
  // 设备字段组标题带「设备字段」，和原有同名资源组分开。
  await picker.getByRole("group", { name: `${group} · ${app.metricPicker.deviceFields}`, exact: true }).waitFor();
  const cpuDeviceGroup = picker.getByRole("group", { name: `${app.dashboard.deviceKind.cpu} · ${app.metricPicker.deviceFields}`, exact: true });
  await cpuDeviceGroup.locator('[data-metric-id="cpu_all_usage_percent@cpu|fixture"]').waitFor();
  if (await picker.getByRole("group", { name: app.status.metricGroup.cpu, exact: true }).locator('[data-metric-id*="@"]').count()) {
    throw new Error("device fields were merged into the legacy CPU group");
  }
  await picker.getByRole("combobox").fill("Fixture NPU");
  if (!await option.isVisible()) throw new Error("searching by device name does not find the device field");
  if ((await option.locator(".metric-option-label").textContent())?.trim() !== label) throw new Error("picker option does not show the localized field name");
  if ((await option.locator(".metric-option-aside").textContent())?.trim() !== "%") throw new Error("picker option does not show the field's unit");
  await option.click();
  await picker.getByRole("button", { name: app.metricPicker.confirm, exact: true }).click();
  await page.getByRole("button", { name: app.monitorPage.saveLayoutLabel, exact: true }).click();
  await page.getByRole("button", { name: app.monitorPage.editLayoutLabel, exact: true }).waitFor();

  const saved = await readDashboardCache(page);
  const memory = saved?.cards?.find((card) => card.id === "memory");
  const added = memory?.slots?.at(-1);
  if (added?.metricId !== npuBusyCardId || added.size !== "small"
    || added.binding?.scopeKind !== "device-field"
    || added.binding?.scopeKey !== "npu|fixture") {
    throw new Error(`saved card lacks the device-field binding: ${JSON.stringify(memory)}`);
  }

  const row = () => page.locator(`.metric-card${NOT_GHOST}`).filter({ hasText: label })
    .locator(`.metric-small-row${NOT_GHOST}`).filter({ hasText: label });
  await row().locator(".metric-value").filter({ hasText: "137.5%" }).waitFor({ timeout: 15_000 });
  await row().locator(".metric-device").filter({ hasText: group }).waitFor();

  await page.reload({ waitUntil: "domcontentloaded" });
  await page.getByRole("navigation").getByRole("button", { name: app.page.monitor, exact: true }).click();
  await row().locator(".metric-value").filter({ hasText: "137.5%" }).waitFor({ timeout: 15_000 });

  const telemetry = await (await fetch(`${baseUrl}/__test/requests`)).json();
  const legacyPaths = (telemetry["/api/subscriptions/stream"]?.subscriptionSets ?? []).flat()
    .filter((path) => typeof path === "string" && path.startsWith("/api/metrics/subscribe?") && path.includes("ids="));
  if (legacyPaths.some((path) => decodeURIComponent(path).includes("@"))) {
    throw new Error(`a device field leaked into the legacy metric subscription: ${legacyPaths.join(" ")}`);
  }
  const fieldPaths = (telemetry["/api/subscriptions/stream"]?.subscriptionSets ?? []).flat()
    .filter((path) => typeof path === "string" && path.startsWith("/api/metrics/subscribe?") && path.includes("fields="));
  if (!fieldPaths.some((path) => decodeURIComponent(path).includes("npu_all_engine_busy_percent"))) {
    throw new Error("the card did not subscribe its field by name");
  }

  // 设备消失：卡片写「设备不在」，设置不删。
  const response = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ metricFieldDevices: metricFieldDevices.filter((device) => device.deviceIdentity !== "npu|fixture") })
  });
  if (!response.ok) throw new Error("Unable to remove the NPU fixture");
  await row().locator(".metric-value").filter({ hasText: app.dashboard.deviceAbsent }).waitFor({ timeout: 15_000 });
  if (!(await readDashboardCache(page))?.cards?.find((card) => card.id === "memory")?.slots?.some((slot) => slot.metricId === npuBusyCardId)) {
    throw new Error("the absent device's card setting was dropped");
  }

  // 恢复完整设备值后再放一张「一在一不在」的卡片。
  await enableMetricFields(baseUrl);
  await seedFieldCards(page);
  await page.reload({ waitUntil: "domcontentloaded" });
  const display = await checkFieldCardDisplay(page, app, "zh-CN");
  const readingSources = await checkReadingSources(page, baseUrl, app);
  return { picker: { group, label }, savedBinding: added.binding, legacyPaths: legacyPaths.length, display, readingSources };
}

/**
 * 读数来源：项目列表只列首选和待确认，不列次选、跨设备别名和不支持的来源。
 * 全部来源和首选标记由第二步来源列表单独核对（metricSourceChoiceJourney）。
 * 卡片上已放的非首选字段一直显示它自己的值。最后核对待确认标记在每种风格的各配色下都看得清。
 */
async function checkReadingSources(page, baseUrl, app) {
  const response = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ metricFieldDevices: [...metricFieldDevices, ...readingSourceDevices] })
  });
  if (!response.ok) throw new Error("Unable to enable the reading-source fixture");
  const binding = (id) => ({ scopeKind: "device-field", scopeKey: id.slice(id.indexOf("@") + 1) });
  await setDashboardSettings(page, [{
    id: "sources", main: readingSourceIds.preferred, mainBinding: binding(readingSourceIds.preferred),
    small: [readingSourceIds.alternate, readingSourceIds.pending],
    smallBindings: [binding(readingSourceIds.alternate), binding(readingSourceIds.pending)]
  }]);
  await page.reload({ waitUntil: "domcontentloaded" });
  const marks = app.dashboard.readingSource;
  const card = page.locator(".metric-card").filter({ hasText: app.fieldLabel.gpu_amd_apu_temperature_reported_celsius.label }).first();
  const main = card.locator(".metric-main-content");
  const row = (fieldName) => card.locator(".metric-small-row").filter({ hasText: app.fieldLabel[fieldName].label });
  const markText = async (locator) => (await locator.locator(".reading-source-mark").allTextContents()).map((text) => text.trim());
  const expectCard = async (expected, phase) => {
    await main.locator(".metric-value").filter({ hasText: "51.0 °C" }).waitFor({ timeout: 15_000 });
    // 卡片上已放的次选来源照常显示它自己的值，不换成首选、不隐藏。
    await row("gpu_amd_edge_temperature_celsius").locator(".metric-value").filter({ hasText: "50.0 °C" }).waitFor();
    await row("storage_all_nvme_composite_temperature_celsius").locator(".metric-value").filter({ hasText: "40.0 °C" }).waitFor();
    // 标记跟着字段目录来：设备名出现说明目录到了。
    await main.locator(".metric-device").waitFor({ timeout: 15_000 });
    if (expected.main.length > 0) await main.locator(".reading-source-mark").waitFor();
    const actual = {
      main: await markText(main),
      alternate: await markText(row("gpu_amd_edge_temperature_celsius")),
      pending: await markText(row("storage_all_nvme_composite_temperature_celsius"))
    };
    if (JSON.stringify(actual) !== JSON.stringify(expected)) {
      throw new Error(`${phase}: card marks ${JSON.stringify(actual)}, expected ${JSON.stringify(expected)}`);
    }
  };
  const picker = page.locator("#metricModal");
  const option = (id) => picker.locator(`[data-metric-id="${id}"]`);
  const openPicker = async () => {
    await page.getByRole("button", { name: app.monitorPage.editLayoutLabel, exact: true }).click();
    await addCardSlot(page, app, 1);
    // 字段目录 5 秒推一次。
    await option(readingSourceIds.preferred).waitFor({ timeout: 15_000 });
  };
  const closePicker = async () => {
    await picker.getByRole("button", { name: app.metricPicker.cancel, exact: true }).click();
    await page.getByRole("button", { name: app.monitorPage.cancelEditLabel, exact: true }).click();
  };
  const expectPicker = async (expected, phase) => {
    const actual = {};
    for (const [kind, id] of Object.entries(readingSourceIds)) {
      actual[kind] = await option(id).count() === 0 ? null : await markText(option(id));
    }
    if (JSON.stringify(actual) !== JSON.stringify(expected)) {
      throw new Error(`${phase}: picker ${JSON.stringify(actual)}, expected ${JSON.stringify(expected)}`);
    }
  };

  await page.getByRole("navigation").getByRole("button", { name: app.page.monitor, exact: true }).click();
  await expectCard({ main: [], alternate: [], pending: [marks.pending] }, "default");
  await openPicker();
  await expectPicker({ preferred: [], alternate: null, alias: null, pending: [marks.pending], unsupported: null }, "default");
  const titles = await picker.locator(".reading-source-mark").evaluateAll((elements) => [...new Set(elements.map((element) => element.getAttribute("data-full-content") ?? ""))]);
  if (!titles.includes(marks.pendingHint)) {
    throw new Error(`Reading-source marks lack their hints: ${JSON.stringify(titles)}`);
  }
  const contrast = await checkMarkContrast(page, picker);
  await closePicker();
  return { contrast };
}

/** 每种风格的每个配色（跟随系统再分浅、深两种）下，待确认标记的文字与底色的对比度。 */
async function checkMarkContrast(page, picker) {
  const pending = picker.locator(`[data-metric-id="${readingSourceIds.pending}"] .reading-source-mark`);
  const results = [];
  for (const style of uiStyles) {
    for (const palette of style.palettes) {
      for (const scheme of palette === "system" ? ["light", "dark"] : [null]) {
        await page.emulateMedia({ colorScheme: scheme });
        await page.evaluate(({ uiStyle, theme }) => {
          document.documentElement.dataset.uiStyle = uiStyle;
          document.documentElement.dataset.theme = theme;
        }, { uiStyle: style.id, theme: palette });
        const ratio = await pending.evaluate(contrastRatio);
        results.push({ style: style.id, palette, scheme, kind: "pending", ratio: Math.round(ratio * 100) / 100 });
      }
    }
  }
  await page.emulateMedia({ colorScheme: null });
  const weak = results.filter((result) => result.ratio < minimumMarkContrast);
  if (weak.length > 0) throw new Error(`Reading-source marks are hard to read: ${JSON.stringify(weak)}`);
  return { minimum: Math.min(...results.map((result) => result.ratio)), checked: results.length };
}

// 11 px 粗体小字按 WCAG AA 正文的 4.5:1。
const minimumMarkContrast = 4.5;

/** 在页面里算文字与它实际底色（逐层合成到第一个不透明背景）的 WCAG 对比度。 */
function contrastRatio(element) {
  const canvas = document.createElement("canvas");
  canvas.width = 1;
  canvas.height = 1;
  const context = canvas.getContext("2d", { willReadFrequently: true });
  const rgba = (color) => {
    context.clearRect(0, 0, 1, 1);
    context.fillStyle = color;
    context.fillRect(0, 0, 1, 1);
    const [r, g, b, a] = context.getImageData(0, 0, 1, 1).data;
    return [r, g, b, a / 255];
  };
  const over = (top, bottom) => [0, 1, 2].map((index) => top[index] * top[3] + bottom[index] * (1 - top[3])).concat(1);
  const layers = [];
  let node = element;
  let depth = 0;
  while (node && depth < 64) {
    const layer = rgba(getComputedStyle(node).backgroundColor);
    if (layer[3] > 0) layers.push(layer);
    if (layer[3] >= 1) break;
    node = node.parentElement;
    depth += 1;
  }
  let background = [255, 255, 255, 1];
  for (let index = layers.length - 1; index >= 0; index -= 1) background = over(layers[index], background);
  const text = over(rgba(getComputedStyle(element).color), background);
  const luminance = ([r, g, b]) => {
    const channel = (value) => {
      const scaled = value / 255;
      return scaled <= 0.04045 ? scaled / 12.92 : ((scaled + 0.055) / 1.055) ** 2.4;
    };
    return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
  };
  const [light, dark] = [luminance(text), luminance(background)].sort((left, right) => right - left);
  return (light + 0.05) / (dark + 0.05);
}
