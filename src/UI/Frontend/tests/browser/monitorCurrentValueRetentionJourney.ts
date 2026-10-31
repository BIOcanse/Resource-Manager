import type { Page } from "playwright-core";
import { monitorFirstTableValueProbe } from "./monitorDemandRetentionProbe.ts";
import assert from "node:assert/strict";
import { observeSubscriptionFrames } from "./subscriptionFrameProbe.ts";
import { waitForContentSettled } from "./contentSettled.ts";

export default async function monitorCurrentValueRetentionJourney(page: Page, baseUrl: string) {
  // Exit-animation copies are inert siblings; only the live stage owns demand.
  const stage = page.locator("#pageStage");
  const paths = ["/api/metrics/subscribe", "/api/resource-monitor/subscribe", "/api/resource-facts/subscribe"];
  const errors : string[] = [];
  page.on("pageerror", (error) => errors.push(String(error)));
  const frameProbe = await observeSubscriptionFrames(page, baseUrl);
  await patch({ pausedSubscriptionPaths: ["/api/resource-facts/subscribe"] });
  await page.setViewportSize({ width: 800, height: 500 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const firstTableValue = await monitorFirstTableValueProbe(page, baseUrl);
  await page.setViewportSize({ width: 1067, height: 800 });
  await stage.locator(".resource-breakdown-panel").scrollIntoViewIfNeeded();
  await waitForValues();
  const before = await values();
  assert(before.rows.length > 0);

  await patch({ pausedSubscriptionPaths: paths });
  // Pause ACK stops future writes, but already-written frames must reach the
  // application's reader and commit before we capture the retained current value.
  await frameProbe.drain(paths, [".resource-table-panel", ".resource-breakdown-panel", ".dashboard"]);
  // Sorting and software/process projection consume the same accepted facts locally.
  // With publications paused, both must render immediately without clearing rows.
  const localProjectionBefore = await telemetry();
  await stage.locator('.resource-table-sort-button').filter({ hasText: /^CPU$/ }).click();
  assert.equal(await stage.getByRole("columnheader").filter({ hasText: /^CPU/ }).getAttribute("aria-sort"), "descending");
  assert((await values()).rows.length > 0, "Local sort retains accepted facts");
  await stage.locator(".resource-table-mode-switch").getByRole("radio", { name: "进程级", exact: true }).click();
  await stage.locator('.resource-table-row[data-resource-row-id]').filter({ hasText: "example-0.exe" }).waitFor();
  const localProjectionAfter = await telemetry();
  assert.equal(localProjectionAfter["/api/resource-facts/subscribe"].publications,
    localProjectionBefore["/api/resource-facts/subscribe"].publications, "Local projections need no new fact publication");
  await patch({ pausedSubscriptionPaths: [] });
  await waitForValues();

  await patch({ pausedSubscriptionPaths: paths });
  // Drain the exact pre-pause publication counts again before release/reacquire;
  // an already populated table alone cannot prove queued frames were consumed.
  await frameProbe.drain(paths, [".resource-table-panel", ".resource-breakdown-panel", ".dashboard"]);
  const paused = await telemetry();
  const retained = await values();
  const navigation = page.getByRole("navigation", { name: "页面切换" });
  await navigation.getByRole("button", { name: "组件与软件", exact: true }).click();
  await page.waitForFunction(async (paths) => {
    const current: Record<string, { logicalInFlight: number }> = await (await fetch("/__test/requests")).json();
    return paths.every((path) => current[path]?.logicalInFlight === 0);
  }, paths);
  await navigation.getByRole("button", { name: "监视控制台", exact: true }).click();
  // Retained values belong to the current page, while the departing page copy
  // temporarily keeps the same CSS classes. Publications remain paused here.
  await waitForContentSettled(page);
  await stage.locator(".resource-table-panel").scrollIntoViewIfNeeded();
  await page.waitForFunction(async () => {
    const current: Record<string, { logicalInFlight: number }> = await (await fetch("/__test/requests")).json();
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
  await stage.locator('.monitor-work-region[aria-label="资源列表"]').scrollIntoViewIfNeeded();
  await page.waitForFunction(async () => (await (await fetch("/__test/requests")).json())["/api/resource-facts/subscribe"]?.logicalInFlight > 0);
  await patch({ resourceTableVariant: "empty" });
  await page.waitForFunction(() => document.querySelectorAll<HTMLElement>("#pageStage .resource-table-retained-surface:not([hidden]) .resource-table-row[data-resource-row-id]").length === 0);
  await stage.locator(".resource-table-panel .content-state.empty").getByText("当前没有可显示的资源", { exact: true }).waitFor();
  assert.equal(await stage.locator("#resourceTableStatus").textContent(), "0", "A received zero-row value has a real zero count");
  assert.deepEqual(errors, []);
  return { firstTableValue, before, after, releasedValuesRetained: true, localProjectionsRetainFacts: true, callbacksResumed: true, actualEmptyValueApplied: true };

  async function waitForValues() {
    // Offscreen cards hold no demand. Their visible-return behavior is checked
    // by monitorDemandRetentionProbe, independently of the table publication.
    await page.waitForFunction(() =>
      document.querySelectorAll<HTMLElement>("#pageStage .resource-breakdown-item").length > 0
      && document.querySelectorAll<HTMLElement>("#pageStage .resource-table-row[data-resource-row-id]").length > 0);
  }
  async function values() {
    // Leaving animation copies are not the active page's accepted values.
    return page.evaluate(() => ({
      metrics: [...document.querySelectorAll<HTMLElement>("#pageStage .dashboard .metric-value")].map((node) => node.textContent),
      bars: [...document.querySelectorAll<HTMLElement>("#pageStage .resource-breakdown-header")].map((node) => node.textContent),
      rows: [...document.querySelectorAll<HTMLElement>("#pageStage .resource-table-row[data-resource-row-id]")].map((node) => node.textContent)
    }));
  }
  async function telemetry(): Promise<Record<string, { publications: number; started: number }>> {
    return (await page.request.get(`${baseUrl}/__test/requests`)).json();
  }
  async function patch(data: Record<string, unknown>) {
    assert.equal((await page.request.post(`${baseUrl}/__test/scenario`, { data })).ok(), true);
  }
}
