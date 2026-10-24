import assert from "node:assert/strict";

export default async function frontendVisibilityDemandJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  await page.setViewportSize({ width: 800, height: 500 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const dashboard = page.locator('.monitor-work-region[aria-label="实时指标"]');
  const table = page.locator('.monitor-work-region[aria-label="资源列表"]');
  await dashboard.waitFor({ state: "visible" });
  await table.waitFor({ state: "attached" });
  assert.equal(await page.locator('.monitor-work-region[data-frontend-visibility-surface]').count(), 3);
  await wait(t => t["/api/metrics/subscribe"]?.logicalInFlight === 1);
  await table.scrollIntoViewIfNeeded();
  await wait(t => t["/api/metrics/subscribe"]?.logicalInFlight === 0
      && Object.entries(t["/api/resource-monitor/subscribe"]?.inFlightByQuery ?? {})
        .some(([query, count]) => count > 0 && query.includes("scope=table")));
  await page.locator(".resource-table-mode-switch").getByRole("radio", { name: "性能", exact: true }).click();
  await wait(t => {
    const resource = t["/api/resource-monitor/subscribe"];
    return t["/api/metrics/subscribe"]?.logicalInFlight === 1
      && t["/api/metrics/fields/catalog/subscribe"]?.logicalInFlight === 0
      && Object.entries(resource?.inFlightByQuery ?? {}).every(([query, count]) =>
        count === 0 || !query.includes("scope=table"));
  });
  await dashboard.scrollIntoViewIfNeeded();
  await wait(t => t["/api/metrics/subscribe"]?.logicalInFlight === 1);
  const telemetry = await (await page.request.get(`${baseUrl}/__test/requests`)).json();
  assert.equal(telemetry["/api/metrics/snapshot"]?.started ?? 0, 0);
  assert.equal(telemetry["/api/resource-monitor/snapshot"]?.started ?? 0, 0);
  assert.equal(telemetry["/api/adapters/resource-manager/visible-regions"]?.started ?? 0, 0);
  assert.deepEqual(errors, []);
  return { offscreenMetricsReleased: true, performanceTableReleased: true, visibleMetricsReacquired: true };

  async function wait(predicate) {
    const deadline = Date.now() + 10000;
    while (Date.now() < deadline) {
      const telemetry = await (await fetch(`${baseUrl}/__test/requests`)).json();
      if (predicate(telemetry)) return;
      await new Promise(resolve => setTimeout(resolve, 50));
    }
    throw new Error("Monitor telemetry did not reach expected demand");
  }
}
