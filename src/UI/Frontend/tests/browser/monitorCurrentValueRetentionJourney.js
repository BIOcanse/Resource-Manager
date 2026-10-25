import { monitorFirstTableValueProbe } from "./monitorDemandRetentionProbe.js";
import assert from "node:assert/strict";

export default async function monitorCurrentValueRetentionJourney(page, baseUrl) {
  const paths = ["/api/metrics/subscribe", "/api/resource-monitor/subscribe", "/api/resource-facts/subscribe"];
  const errors = [];
  page.on("pageerror", (error) => errors.push(String(error)));
  await patch({ pausedSubscriptionPaths: ["/api/resource-facts/subscribe"] });
  await page.setViewportSize({ width: 800, height: 500 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const firstTableValue = await monitorFirstTableValueProbe(page, baseUrl);
  await page.setViewportSize({ width: 1067, height: 800 });
  await page.locator(".resource-breakdown-panel").scrollIntoViewIfNeeded();
  await waitForValues();
  const before = await values();
  assert(before.rows.length > 0);

  await patch({ pausedSubscriptionPaths: paths });
  await page.waitForTimeout(100);
  // Sorting and software/process projection consume the same accepted facts locally.
  // With publications paused, both must render immediately without clearing rows.
  const localProjectionBefore = await telemetry();
  await page.locator('.resource-table-sort-button').filter({ hasText: /^CPU$/ }).click();
  assert.equal(await page.getByRole("columnheader").filter({ hasText: /^CPU/ }).getAttribute("aria-sort"), "descending");
  assert((await values()).rows.length > 0, "Local sort retains accepted facts");
  await page.locator(".resource-table-mode-switch").getByRole("radio", { name: "进程级", exact: true }).click();
  await page.locator('.resource-table-row[data-resource-row-id]').filter({ hasText: "example-0.exe" }).waitFor();
  const localProjectionAfter = await telemetry();
  assert.equal(localProjectionAfter["/api/resource-facts/subscribe"].publications,
    localProjectionBefore["/api/resource-facts/subscribe"].publications, "Local projections need no new fact publication");
  await patch({ pausedSubscriptionPaths: [] });
  await waitForValues();

  await patch({ pausedSubscriptionPaths: paths });
  await page.waitForTimeout(100);
  const paused = await telemetry();
  const retained = await values();
  const navigation = page.getByRole("navigation", { name: "页面切换" });
  await navigation.getByRole("button", { name: "组件与软件", exact: true }).click();
  await page.waitForFunction(async (paths) => {
    const current = await (await fetch("/__test/requests")).json();
    return paths.every((path) => current[path]?.logicalInFlight === 0);
  }, paths);
  await navigation.getByRole("button", { name: "监视控制台", exact: true }).click();
  await page.locator(".resource-table-panel").scrollIntoViewIfNeeded();
  await page.waitForFunction(async () => {
    const current = await (await fetch("/__test/requests")).json();
    return current["/api/resource-facts/subscribe"]?.logicalInFlight > 0;
  });
  const after = await values();
  assert.deepEqual(after.rows, retained.rows, "Reacquisition immediately displays the latest rows");
  assert.deepEqual(after.bars, retained.bars, "Resource bars survive demand release");
  assert.deepEqual(after.metrics, retained.metrics, "Metrics survive demand release");
  const resumed = await telemetry();
  for (const path of paths) {
    assert.equal(resumed[path].publications, paused[path].publications,
      "Current values must not depend on another backend callback");
  }
  for (const path of ["/api/metrics/snapshot", "/api/resource-monitor/snapshot", "/api/resource-facts/snapshot"]) {
    assert.equal(resumed[path]?.started ?? 0, 0, "Frontend must not fill the gap with a direct GET");
  }
  await patch({ pausedSubscriptionPaths: [] });
  await waitForValues();
  await patch({ resourceTableVariant: "empty" });
  await page.waitForFunction(() => document.querySelectorAll(".resource-table-retained-surface:not([hidden]) .resource-table-row[data-resource-row-id]").length === 0);
  await page.locator(".resource-table-panel .content-state.empty").getByText("当前没有可显示的资源", { exact: true }).waitFor();
  assert.equal(await page.locator("#resourceTableStatus").textContent(), "0", "A received zero-row value has a real zero count");
  assert.deepEqual(errors, []);
  return { firstTableValue, before, after, releasedValuesRetained: true, localProjectionsRetainFacts: true, callbacksResumed: true, actualEmptyValueApplied: true };

  async function waitForValues() {
    await page.waitForFunction(() =>
      document.querySelector(".metric-main .metric-value")?.textContent?.includes("%")
      && document.querySelectorAll(".resource-breakdown-item").length > 0
      && document.querySelectorAll(".resource-table-row[data-resource-row-id]").length > 0);
  }
  async function values() {
    return page.evaluate(() => ({
      metrics: [...document.querySelectorAll(".dashboard .metric-value")].map((node) => node.textContent),
      bars: [...document.querySelectorAll(".resource-breakdown-header")].map((node) => node.textContent),
      rows: [...document.querySelectorAll(".resource-table-row[data-resource-row-id]")].map((node) => node.textContent)
    }));
  }
  async function telemetry() {
    return (await page.request.get(`${baseUrl}/__test/requests`)).json();
  }
  async function patch(data) {
    assert.equal((await page.request.post(`${baseUrl}/__test/scenario`, { data })).ok(), true);
  }
}
