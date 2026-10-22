/*
 * 规范字段新显示的浏览器检查：CPU 拓扑的 NUMA 行、核心详情的有效频率、设备详情里的
 * NPU／AMD NPU 两节，以及监视控制台上的设备字段卡片。同一份设备值供单语言旅程和九语旅程共用。
 */

const fixtureDeviceId = "DISPLAY\\FIXTURE_HDMI_1";
const field = (name, unit, valueKind, extra) => ({
  name, unit, valueKind, numericValue: null, integerValue: null, status: 1,
  observedAt: "2026-10-22T00:00:00Z", sourceId: "fixture", ...extra
});

export const metricFieldDevices = [
  { deviceIdentity: "numa|windows-numa-node|0", device: { kind: "numa_node", address: { kind: "numa_node", nodeNumber: 0 }, displayName: null, ordinal: 0 }, fields: [
    field("memory_all_numa_node_capacity_mib", "MiB", "unsigned64", { integerValue: "32768" }),
    field("memory_all_numa_node_available_bytes", "B", "unsigned64", { integerValue: "5", status: 3 })
  ] },
  { deviceIdentity: "intel-cpu|t0", device: { kind: "cpu_logical_processor", address: { kind: "cpu_logical_processor", processorGroup: 0, groupRelativeIndex: 0 }, displayName: null, ordinal: 0 }, fields: [
    field("cpu_intel_effective_frequency_mhz", "MHz", "float64", { numericValue: 4812.4 })
  ] },
  { deviceIdentity: "intel-cpu|t1", device: { kind: "cpu_logical_processor", address: { kind: "cpu_logical_processor", processorGroup: 0, groupRelativeIndex: 1 }, displayName: null, ordinal: 1 }, fields: [
    field("cpu_intel_effective_frequency_mhz", "MHz", "float64", { status: 7 })
  ] },
  // 拓扑夹具里只有一个端口，NPU 和 AMD 显卡的地址都指向它，只用来检查渲染。
  { deviceIdentity: "npu|fixture", device: { kind: "npu", address: { kind: "npu", pnpInstanceId: fixtureDeviceId }, displayName: "Fixture NPU", ordinal: 0 }, fields: [
    field("npu_all_engine_busy_percent", "%", "float64", { numericValue: 137.54 })
  ] },
  { deviceIdentity: "gpu|fixture", device: { kind: "gpu_adapter", address: { kind: "gpu_adapter", pnpInstanceId: fixtureDeviceId }, displayName: "Fixture GPU", ordinal: 0 }, fields: [
    field("gpu_amd_npu_activity_percent", "%", "float64", { numericValue: 0 }),
    field("gpu_amd_npu_frequency_mhz", "MHz", "float64", { numericValue: 1800.4 })
  ] }
];

export async function enableMetricFields(baseUrl) {
  const response = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ metricFieldDevices })
  });
  if (!response.ok) throw new Error("Unable to enable the metric-field fixture");
}

/** 详细信息页 → CPU 拓扑：NUMA 行；核心详情：有效频率。调用前须已在详细信息页。 */
export async function checkCpuMetricFields(page, app, language) {
  await page.getByRole("tab", { name: app.misc.detailsTab.cpu, exact: true }).click();
  const cpu = app.cpuTopology;
  const numa = cpu.numaNode(0, [cpu.numaCapacity("32.0 GiB"), cpu.numaAvailable(cpu.fieldUnavailable)].join(" · "));
  // CPU 拓扑沿原 60 秒订阅，首个对齐回调最长要等一分钟。
  await page.locator(".cpu-topology-meta strong").waitFor({ timeout: 65_000 });
  const numaLine = page.locator(".cpu-topology-numa").filter({ hasText: numa });
  await numaLine.waitFor();
  if (await numaLine.getAttribute("title") !== cpu.numaHint) throw new Error(`${language}: NUMA hint missing`);
  await page.locator(".cpu-topology-actions").getByRole("button", { name: cpu.details, exact: true }).click();
  const dialog = page.locator(".user-details-modal:visible");
  await dialog.getByText(cpu.coreDetailTitle, { exact: true }).waitFor();
  const frequency = dialog.locator(".user-details-row").filter({ hasText: cpu.effectiveFrequency });
  await frequency.locator("dd").filter({
    hasText: [cpu.logicalFrequency(0, "4812 MHz"), cpu.logicalFrequency(1, cpu.fieldUnavailable)].join(" · ")
  }).waitFor();
  if (await frequency.locator("dt").getAttribute("title") !== cpu.effectiveFrequencyHint)
    throw new Error(`${language}: effective-frequency hint missing`);
  await page.keyboard.press("Escape");
  await page.waitForFunction(() => document.querySelectorAll(".user-details-modal").length === 0
    || [...document.querySelectorAll(".user-details-modal")].every((element) => element.offsetParent === null));
}

/** 详细信息页 → 设备：打开夹具端口的详情弹窗，检查 NPU 一节（忙碌率不截断）和 AMD 报告值一节。 */
export async function checkDeviceMetricFields(page, app, language) {
  const text = app.deviceSpecialized.metricFields;
  await page.getByRole("tab", { name: app.misc.detailsTab.device, exact: true }).click();
  const topology = page.locator(".device-topology-panel");
  await topology.locator(".device-port-item").first().click();
  await topology.locator(".device-details-button").click();
  const dialog = page.locator(".user-details-modal:visible");
  const npu = dialog.locator(".user-details-section").filter({ has: page.getByRole("heading", { name: text.npuTitle, exact: true }) });
  await npu.locator(".user-details-row").filter({ hasText: app.fieldLabel.npu_all_engine_busy_percent.label }).filter({ hasText: "137.5%" }).waitFor();
  const amd = dialog.locator(".user-details-section").filter({ has: page.getByRole("heading", { name: text.amdNpuTitle, exact: true }) });
  await amd.getByText(text.amdNpuHint, { exact: true }).waitFor();
  await amd.locator(".user-details-row").filter({ hasText: app.fieldLabel.gpu_amd_npu_activity_percent.label }).filter({ hasText: "0.0%" }).waitFor();
  await amd.locator(".user-details-row").filter({ hasText: app.fieldLabel.gpu_amd_npu_frequency_mhz.label }).filter({ hasText: "1800 MHz" }).waitFor();
  const result = { language, npuRows: await npu.locator(".user-details-row").count(), amdRows: await amd.locator(".user-details-row").count() };
  await page.keyboard.press("Escape");
  await page.waitForFunction(() => [...document.querySelectorAll(".user-details-modal")].every((element) => element.offsetParent === null));
  return result;
}

export const npuBusyCardId = "npu_all_engine_busy_percent@npu|fixture";
const absentCardId = "npu_all_engine_busy_percent@npu|removed";

/** 已保存的卡片设置里放一张设备字段卡片：主指标的设备在，明细指标的设备不在。页面加载前调用。 */
export async function seedFieldCards(baseUrl) {
  await setDashboardSettings(baseUrl, [
    { id: "fields", main: npuBusyCardId, mainBinding: { scopeKind: "device-field", scopeKey: "npu|fixture" },
      small: [absentCardId], smallBindings: [{ scopeKind: "device-field", scopeKey: "npu|removed" }] }
  ]);
}

/** 监视控制台：检查 seedFieldCards 那张卡片上的字段名、设备名、数值和「设备不在」。 */
export async function checkFieldCardDisplay(page, app, language) {
  await page.getByRole("navigation").getByRole("button", { name: app.page.monitor, exact: true }).click();
  const card = page.locator(".metric-card").filter({ hasText: app.fieldLabel.npu_all_engine_busy_percent.label }).first();
  const main = card.locator(".metric-main-content");
  await main.locator(".metric-value").filter({ hasText: "137.5%" }).waitFor({ timeout: 15_000 });
  const device = app.dashboard.namedDevice(app.dashboard.deviceKind.npu, "Fixture NPU");
  await main.locator(".metric-device").filter({ hasText: device }).waitFor();
  const absent = card.locator(".metric-small-row");
  await absent.locator(".metric-value").filter({ hasText: app.dashboard.deviceAbsent }).waitFor({ timeout: 15_000 });
  if (await absent.locator(".metric-value").getAttribute("title") !== app.dashboard.deviceAbsentHint)
    throw new Error(`${language}: device-absent hint missing`);
  return { language, device, absent: app.dashboard.deviceAbsent };
}

export async function setDashboardSettings(baseUrl, cards) {
  const response = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ dashboardSettings: {
      version: 15, cards, resourceBars: [], resourceTableColumns: [], resourceTableProcessColumns: []
    } })
  });
  if (!response.ok) throw new Error("Unable to set dashboard settings");
}

export async function readScenario(baseUrl) {
  const response = await fetch(`${baseUrl}/__test/scenario`);
  if (!response.ok) throw new Error("Unable to read the scenario");
  return await response.json();
}
