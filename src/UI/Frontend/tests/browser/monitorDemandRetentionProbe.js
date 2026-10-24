import assert from "node:assert/strict";

// Pause publications so a fast first response cannot hide a transient empty frame.
export default async function monitorDemandRetentionProbe(page, baseUrl) {
  const path = "/api/metrics/subscribe";
  const dashboard = page.locator('[data-frontend-visibility-surface="monitor.metrics"]');
  const table = page.locator('[data-frontend-visibility-surface="monitor.table"]');
  await dashboard.scrollIntoViewIfNeeded();
  await page.waitForFunction(() => document.querySelector(".metric-main .metric-value")?.textContent?.includes("%"));
  await patch({ pausedSubscriptionPaths: [path] });
  const before = (await telemetry())[path].publications;
  await page.evaluate(() => {
    window.__monitorRetentionProbe = { failures: [], frames: 0, running: true };
    void (async () => {
      const probe = window.__monitorRetentionProbe;
      while (probe.running) {
        await new Promise(requestAnimationFrame);
        if (!probe.running) break;
        const region = document.querySelector('[data-frontend-visibility-surface="monitor.metrics"]');
        const rect = region?.getBoundingClientRect();
        if (rect && rect.bottom > 0 && rect.top < innerHeight && rect.width > 0) {
          probe.frames++;
          const value = region.querySelector(".metric-main .metric-value")?.textContent;
          if (!value?.includes("%")) probe.failures.push({ value, text: region.textContent?.slice(0, 200) });
        }
      }
    })();
  });
  try {
    await table.scrollIntoViewIfNeeded();
    await waitForDemand(false);
    // Demand is verified released, then remains off for longer than its debounce.
    await page.waitForTimeout(350);
    await dashboard.scrollIntoViewIfNeeded();
    await waitForDemand(true);
    await page.waitForTimeout(200);
    const navigation = page.getByRole("navigation", { name: "页面切换" });
    await navigation.getByRole("button", { name: "组件与软件", exact: true }).click();
    await waitForDemand(false);
    await page.waitForTimeout(350);
    await navigation.getByRole("button", { name: "监视控制台", exact: true }).click();
    await dashboard.scrollIntoViewIfNeeded();
    await waitForDemand(true);
    await page.waitForTimeout(200);
    const result = await page.evaluate(() => {
      window.__monitorRetentionProbe.running = false;
      return window.__monitorRetentionProbe;
    });
    assert(result.frames > 5, "The frame probe must observe visible return frames");
    assert.deepEqual(result.failures, [], "Every visible frame must display the accepted metric, including after page remount");
    assert.equal((await telemetry())[path].publications, before, "Retention must work without a new backend publication");
    return { visibleFrames: result.frames, scrollDemandReleased: true, pageDemandReleased: true };
  } finally {
    await page.evaluate(() => { window.__monitorRetentionProbe.running = false; });
    await patch({ pausedSubscriptionPaths: [] });
  }

  async function telemetry() { return (await page.request.get(`${baseUrl}/__test/requests`)).json(); }
  async function patch(data) { assert((await page.request.post(`${baseUrl}/__test/scenario`, { data })).ok()); }
  async function waitForDemand(active) {
    await page.waitForFunction(async ({ path, active }) => {
      const state = await (await fetch("/__test/requests")).json();
      return active ? state[path]?.logicalInFlight > 0 : state[path]?.logicalInFlight === 0;
    }, { path, active });
  }
}

// Caller pauses the resource-monitor path before navigation to guarantee a first read.
export async function monitorFirstTableValueProbe(page, baseUrl) {
  const table = page.locator('[data-frontend-visibility-surface="monitor.table"]');
  await table.waitFor({ state: "attached" });
  await page.waitForFunction(() => document.querySelector(".metric-main .metric-value")?.textContent?.includes("%"));
  const before = await (await page.request.get(`${baseUrl}/__test/requests`)).json();
  assert(Object.entries(before["/api/resource-monitor/subscribe"]?.publicationsByQuery ?? {})
    .every(([query, count]) => !query.includes("scope=table") || count === 0), "No table value may have been published before this first-load probe");
  assert.equal(await table.locator("#resourceTableStatus").textContent(), "--");
  assert.equal(await table.locator(".content-state.empty").count(), 0, "Offscreen unread table must not claim sampling completed");
  await page.evaluate(() => {
    window.__tableFirstValueProbe = { frames: 0, failures: [], done: false, running: true };
    void (async () => {
      const probe = window.__tableFirstValueProbe;
      while (probe.running && !probe.done) {
        await new Promise(requestAnimationFrame);
        const panel = document.querySelector(".resource-table-panel");
        if (panel?.querySelector(".resource-table-row[data-resource-row-id]")) {
          probe.done = true;
          break;
        }
        const rect = panel?.getBoundingClientRect();
        if (rect && rect.top < innerHeight && rect.bottom > 0) {
          probe.frames++;
          const text = panel.textContent ?? "";
          const count = panel.querySelector("#resourceTableStatus")?.textContent;
          if (text.includes("采样已经完成") || panel.querySelector(".content-state.empty") || count !== "--") {
            probe.failures.push({ text: text.slice(-240), count });
          }
        }
      }
    })();
  });
  try {
    await table.scrollIntoViewIfNeeded();
    await table.locator(".content-state.waiting").waitFor({ state: "visible" });
    await page.waitForTimeout(180);
    const pendingHeight = await table.locator(".resource-table-panel").evaluate(node => node.getBoundingClientRect().height);
    assert.equal((await page.request.post(`${baseUrl}/__test/scenario`, { data: { pausedSubscriptionPaths: [] } })).ok(), true);
    await table.locator(".resource-table-row[data-resource-row-id]").first().waitFor({ state: "visible" });
    const result = await page.evaluate(() => { window.__tableFirstValueProbe.running = false; return window.__tableFirstValueProbe; });
    assert(result.frames >= 5, "The first-load probe must cover multiple pending frames");
    assert.deepEqual(result.failures, [], "No visible frame before first rows may report sampled-empty or a zero count");
    const readyHeight = await table.locator(".resource-table-panel").evaluate(node => node.getBoundingClientRect().height);
    assert(Math.abs(readyHeight - pendingHeight) <= 1, `First table value changed reserved height: ${pendingHeight} -> ${readyHeight}`);
    return { pendingFrames: result.frames, pendingHeight, readyHeight };
  } finally {
    await page.evaluate(() => { window.__tableFirstValueProbe.running = false; });
    await page.request.post(`${baseUrl}/__test/scenario`, { data: { pausedSubscriptionPaths: [] } });
  }
}
