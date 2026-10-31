import type { Page } from "playwright-core";
interface RequestTelemetry {
  started: number; completed: number; aborted: number; inFlight: number; maxInFlight: number;
  logicalStarted: number; logicalInFlight: number; logicalRetired: number; publications: number;
  physicalStarted: number; physicalInFlight: number; physicalCompleted: number; physicalAborted: number; physicalMaxInFlight: number;
  queries: string[]; subscriptionSets: string[][]; inFlightByQuery: Record<string, number>;
}
type Telemetry = Record<string, RequestTelemetry>;

import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import assert from "node:assert/strict";
import { frontendRefreshIntervals } from "../fixtures/frontendRefreshIntervalFixture.ts";

export default async function frontendRefreshIntervalsJourney(page: Page, baseUrl: string) {
  const errors: string[] = [];
  const controlWrites: string[] = [];
  page.on("pageerror", error => errors.push(String(error)));
  page.on("request", request => {
    if (new URL(request.url()).pathname.startsWith("/api/control/") && !["GET", "HEAD"].includes(request.method())) controlWrites.push(request.url());
  });
  await patch({ gpuPressureFixture: true, optimizationRuntime: true });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const navigation = page.getByRole("navigation", { name: "页面切换" });
  const checks: string[] = [];
  await check("monitor", ["/api/metrics/subscribe", "/api/resource-monitor/subscribe"]);
  await navigation.getByRole("button", { name: "详细信息", exact: true }).click();
  await page.getByRole("tab", { name: "CPU 调度模型", exact: true }).click();
  await check("cpu", ["/api/cpu/topology/subscribe", "/api/cpu/residency/subscribe", "/api/metrics/subscribe"]);
  await page.getByRole("tab", { name: "GPU 调度模型", exact: true }).click();
  await check("gpu", ["/api/metrics/subscribe", "/api/resource-monitor/subscribe", "/api/metrics/gpu-specialized/subscribe"]);
  await page.getByRole("tab", { name: "设备拓扑", exact: true }).click();
  await check("topology", ["/api/device-topology/state/subscribe", "/api/metrics/subscribe"]);
  await page.getByRole("tab", { name: "智能调度报告", exact: true }).click();
  await check("smart-report", ["/api/optimization/smart/state/subscribe"]);
  await navigation.getByRole("button", { name: "性能优化", exact: true }).click();
  await check("optimization", ["/api/optimization/smart/status/subscribe"]);
  await navigation.getByRole("button", { name: "控制面", exact: true }).click();
  await check("control", ["/api/control/actual/subscribe"]);
  // Explicit custom choices arrive as a new committed publication and keep
  // their meaning whether or not the window has focus.
  await navigation.getByRole("button", { name: "设置", exact: true }).click();
  const text = await loadSettingsText("zh-CN");
  await page.getByRole("button", { name: text.sections.appearance, exact: true }).click();
  const custom = text.performance.presetNumericModeOptions.find(option => option.id === "custom")!.label;
  for (const [key, value] of [["monitor", 1700], ["resourceTable", 2300], ["localSystem", 4100]] as const) {
    const label = text.performance.refreshCadenceItems[key].label;
    await page.getByRole("radiogroup", { name: label, exact: true }).getByRole("radio", { name: custom, exact: true }).click();
    const input = page.getByRole("textbox", { name: `${label} ${text.performance.refreshCadenceCustomLabel}`, exact: true });
    await input.fill(String(value)); await input.press("Enter");
  }
  await page.getByRole("button", { name: "保存", exact: true }).click();
  await page.waitForFunction(() => (JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1") ?? "null") as { refresh: { monitor: { customValue: number } } } | null)?.refresh.monitor.customValue === 1700);
  // This check needs cards, bars and the table to own visible demand together.
  // A short viewport legitimately retires the offscreen table subscription.
  await page.setViewportSize({ width: 1280, height: 1600 });
  await navigation.getByRole("button", { name: "监视控制台", exact: true }).click();
  for (const foreground of [false, true]) {
    await page.evaluate(value => window.dispatchEvent(new Event(value ? "focus" : "blur")), foreground);
    await wait(telemetry => activeAt(telemetry["/api/metrics/subscribe"], 1700)
      && activeAt(telemetry["/api/local-system/status/subscribe"], 4100)
      && activeAt(telemetry["/api/resource-monitor/subscribe"], 1700)
      && activeAt(telemetry["/api/resource-facts/subscribe"], 2300));
  }
  checks.push("committed-custom-change");
  assert.deepEqual(controlWrites, [], "Interval changes never apply control drafts or write hardware");
  assert.deepEqual(errors, []);
  return { checks, intervalChangesOnlyReconfigureReads: true };

  // The read intervals follow only the manual settings (the automatic mode is the automatic foreground preset):
  // focus and blur change nothing.
  async function check(name: string, paths: string[]) {
    const expected = frontendRefreshIntervals.resolved;
    for (const foreground of [true, false, true]) {
      await page.evaluate(value => window.dispatchEvent(new Event(value ? "focus" : "blur")), foreground);
      await wait(telemetry => paths.every(path => activeAt(telemetry[path], expected.monitor))
        && activeAt(telemetry["/api/local-system/status/subscribe"], expected.localSystem));
    }
    checks.push(name);
  }
  function activeAt(telemetry: RequestTelemetry | undefined, interval: number) {
    const active = Object.entries(telemetry?.inFlightByQuery ?? {}).filter(([, count]) => count > 0);
    return active.length > 0 && active.every(([query]) => new URLSearchParams(query.slice(1)).get("intervalMs") === String(interval));
  }
  async function wait(predicate: (telemetry: Telemetry) => boolean) {
    const deadline = Date.now() + 12000;
    let telemetry: Telemetry | undefined;
    while (Date.now() < deadline) {
      telemetry = await (await fetch(`${baseUrl}/__test/requests`)).json() as Telemetry;
      if (predicate(telemetry)) return;
      await new Promise(resolve => setTimeout(resolve, 50));
    }
    throw new Error(`Published intervals not requested: ${JSON.stringify(telemetry)}`);
  }
  async function patch(value: Record<string, unknown>) {
    assert.equal((await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(value) })).ok, true);
  }
}
