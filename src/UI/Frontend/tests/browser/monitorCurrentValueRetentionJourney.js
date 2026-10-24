import assert from "node:assert/strict";

export default async function monitorCurrentValueRetentionJourney(page, baseUrl) {
  const paths = ["/api/metrics/subscribe", "/api/resource-monitor/subscribe"];
  const errors = [];
  page.on("pageerror", (error) => errors.push(String(error)));
  await page.setViewportSize({ width: 1067, height: 800 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await waitForValues();
  const before = await values();
  assert(before.rows.length > 0);

  await patch({ pausedSubscriptionPaths: paths });
  await page.waitForTimeout(100);
  // Changing the query binds the current source immediately. Old rows cannot stand
  // in for the new sort or for a different table mode while its response is pending.
  await page.locator('.resource-table-sort-button').filter({ hasText: /^CPU$/ }).click();
  await page.waitForFunction(() => document.querySelectorAll(".resource-table-row[data-resource-row-id]").length === 0);
  await patch({ pausedSubscriptionPaths: [] });
  await waitForValues();
  await patch({ pausedSubscriptionPaths: paths });
  await page.waitForTimeout(100);
  await page.locator(".resource-table-mode-switch").getByRole("radio", { name: "进程级", exact: true }).click();
  await page.waitForFunction(() => document.querySelectorAll(".resource-table-row[data-resource-row-id]").length === 0);
  await patch({ pausedSubscriptionPaths: [] });
  await waitForValues();

  await patch({ pausedSubscriptionPaths: paths });
  await page.waitForTimeout(100);
  const paused = await telemetry();
  const navigation = page.getByRole("navigation", { name: "页面切换" });
  await navigation.getByRole("button", { name: "组件与软件", exact: true }).click();
  await page.waitForFunction(async (paths) => {
    const current = await (await fetch("/__test/requests")).json();
    return paths.every((path) => current[path]?.logicalInFlight === 0);
  }, paths);
  await navigation.getByRole("button", { name: "监视控制台", exact: true }).click();
  await page.waitForFunction(async () => {
    const current = await (await fetch("/__test/requests")).json();
    return current["/api/metrics/subscribe"]?.logicalInFlight > 0
      && current["/api/resource-monitor/subscribe"]?.logicalInFlight > 0;
  });
  const after = await values();
  assert.deepEqual(after.rows, [], "Zero demand releases the source; reacquisition cannot replay old rows");
  assert.deepEqual(after.bars, [], "Reacquisition cannot replay old resource bars");
  assert(after.metrics.every((text) => !text.includes("%")), "Reacquisition cannot replay old metric values");
  const resumed = await telemetry();
  for (const path of paths) {
    assert.equal(resumed[path].publications, paused[path].publications,
      "Empty current values must not depend on another backend callback");
  }
  for (const path of ["/api/metrics/snapshot", "/api/resource-monitor/snapshot"]) {
    assert.equal(resumed[path]?.started ?? 0, 0, "Frontend must not fill the gap with a direct GET");
  }
  await patch({ pausedSubscriptionPaths: [] });
  await waitForValues();
  await patch({ resourceTableVariant: "empty" });
  await page.waitForFunction(() => document.querySelectorAll(".resource-table-row[data-resource-row-id]").length === 0);
  assert.deepEqual(errors, []);
  return { before, after, releasedValuesCleared: true, incompatibleQueryCleared: true, callbacksResumed: true, actualEmptyValueApplied: true };

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
