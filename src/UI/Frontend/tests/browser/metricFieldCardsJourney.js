import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import {
  checkFieldCardDisplay,
  enableMetricFields,
  metricFieldDevices,
  npuBusyCardId,
  readScenario,
  seedFieldCards
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
  await page.getByRole("button", { name: app.dashboard.addMetric(app.dashboard.detailSlot(2, 2)), exact: true }).click();
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

  const saved = (await readScenario(baseUrl)).dashboardSettings;
  const memory = saved?.cards?.find((card) => card.id === "memory");
  if (memory?.small?.[1] !== npuBusyCardId
    || memory.smallBindings?.[1]?.scopeKind !== "device-field"
    || memory.smallBindings?.[1]?.scopeKey !== "npu|fixture") {
    throw new Error(`saved card lacks the device-field binding: ${JSON.stringify(memory)}`);
  }

  const row = () => page.locator(".metric-card").filter({ hasText: label }).locator(".metric-small-row").filter({ hasText: label });
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
  if ((await readScenario(baseUrl)).dashboardSettings?.cards?.find((card) => card.id === "memory")?.small?.[1] !== npuBusyCardId) {
    throw new Error("the absent device's card setting was dropped");
  }

  // 恢复完整设备值后再放一张「一在一不在」的卡片。
  await enableMetricFields(baseUrl);
  await seedFieldCards(baseUrl);
  await page.reload({ waitUntil: "domcontentloaded" });
  const display = await checkFieldCardDisplay(page, app, "zh-CN");
  return { picker: { group, label }, savedBinding: memory.smallBindings[1], legacyPaths: legacyPaths.length, display };
}
