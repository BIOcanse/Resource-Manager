import assert from "node:assert/strict";

export default async function monitorCurrentValueRetentionJourney(page, baseUrl) {
  const paths = ["/api/metrics/subscribe", "/api/resource-monitor/subscribe"];
  const errors = [];
  page.on("pageerror", (error) => errors.push(String(error)));
  await page.setViewportSize({ width: 1067, height: 800 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() =>
    document.querySelector(".metric-main .metric-value")?.textContent?.includes("%")
    && document.querySelectorAll(".resource-breakdown-item").length > 0
    && document.querySelectorAll(".resource-table-row[data-resource-row-id]").length > 0);
  await patch({ pausedSubscriptionPaths: paths });
  // Let any already-written frame finish before establishing the retained value.
  await page.waitForTimeout(100);
  const before = await values();
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
    return current["/api/metrics/subscribe"]?.logicalInFlight === 1
      && current["/api/resource-monitor/subscribe"]?.logicalInFlight === 2;
  });
  const after = await values();
  const resumed = await telemetry();
  assert.deepEqual(after, before, "Releasing and reacquiring unchanged queries must not erase their values");
  for (const path of paths) {
    assert.equal(resumed[path].publications, paused[path].publications,
      "Retained values must not depend on another callback");
  }
  for (const path of ["/api/metrics/snapshot", "/api/resource-monitor/snapshot"]) {
    assert.equal(resumed[path]?.started ?? 0, 0, "Frontend must not fill the gap with a direct GET");
  }

  const rowCount = await page.locator(".resource-table-row[data-resource-row-id]").count();
  await page.locator('.resource-table-sort-button').filter({ hasText: /^CPU$/ }).click();
  assert.equal(await page.locator(".resource-table-row[data-resource-row-id]").count(), rowCount,
    "Sorting must not erase the current rows while waiting for the new callback");
  const track = page.locator('.resource-bar-track[data-metric-id="memory.usage"]');
  const bounds = await track.boundingBox();
  assert(bounds);
  const barCount = await page.locator('.resource-breakdown-item').count();
  await track.click({ position: { x: bounds.width / 16, y: bounds.height / 2 } });
  assert.equal(await page.locator('.resource-breakdown-item').count(), barCount,
    "Selecting a software segment must not erase the current bars");
  await page.getByRole('button', { name: '编辑资源占用条目', exact: true }).click();
  assert.equal(await page.locator('.resource-breakdown-item').count(), barCount);
  await page.getByRole('button', { name: '取消资源占用条目编辑', exact: true }).click();
  assert.equal(await page.locator('.resource-breakdown-item').count(), barCount);
  await page.locator(".resource-table-mode-switch").getByRole("radio", { name: "进程级", exact: true }).click();
  assert.equal(await page.locator(".resource-table-row[data-resource-row-id]").count(), rowCount,
    "Query replacement must wait for a value, not manufacture an empty value");
  await patch({ pausedSubscriptionPaths: [] });
  await page.waitForFunction(() => document.querySelectorAll(".resource-table-row[data-resource-row-id]").length > 0);
  assert.deepEqual(errors, []);
  await patch({ resourceTableVariant: 'empty' });
  await page.waitForFunction(() => document.querySelectorAll('.resource-table-row[data-resource-row-id]').length === 0);
  return { before, after, unchangedPublications: true, interactionsRetainCurrentValue: true, callbacksResumed: true, actualEmptyValueApplied: true };

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
