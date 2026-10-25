import assert from "node:assert/strict";
import { pointPreferences } from "../../src/preferences/frontendPreferences.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";

export default async function dashboardPreferencesJourney(page, baseUrl) {
  const errors = [];
  const dashboardRequests = [];
  page.on("pageerror", error => errors.push(String(error)));
  page.on("request", request => {
    if (new URL(request.url()).pathname === "/api/settings/dashboard") dashboardRequests.push(request.url());
  });
  const key = "resource-manager:frontend-preferences:v1";
  const initial = pointPreferences();
  initial.dashboard = { version: 16, cards: [
    { id: "visible-cpu", main: "cpu.usage", small: [] },
    { id: "offscreen-memory", main: "memory.usage", small: [] }
  ], resourceBars: [], resourceTableColumns: [{ id: "name", visible: true, width: 260 }],
  resourceTableProcessColumns: [{ id: "name", visible: true, width: 260 }] };
  await page.addInitScript(({ key, initial }) => {
    if (!sessionStorage.getItem("dashboard-journey-seeded")) {
      localStorage.setItem(key, JSON.stringify(initial));
      sessionStorage.setItem("dashboard-journey-seeded", "yes");
    }
  }, { key, initial });
  await page.setViewportSize({ width: 800, height: 500 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.addStyleTag({ content: ".dashboard{grid-template-columns:1fr!important}.metric-card{min-height:600px!important}" });
  const cards = page.locator(".metric-card");
  await cards.nth(1).waitFor({ state: "attached" });
  await cards.first().scrollIntoViewIfNeeded();
  await waitIds(["cpu.usage"]);
  assert.equal((await telemetry())["/api/resource-monitor/subscribe"]?.logicalInFlight ?? 0, 0,
    "An explicit empty resource-bar selection holds no subscription");
  await cards.nth(1).scrollIntoViewIfNeeded();
  await waitIds(["memory.usage"]);
  await page.getByRole("button", { name: "设置", exact: true }).click();
  await waitIds([]);
  await page.getByRole("button", { name: "监视控制台", exact: true }).click();
  await page.locator('[data-focus-key="monitor-dashboard-edit"]').click();
  await cards.first().locator(".slot-delete").first().click();
  await page.locator('[data-focus-key="monitor-dashboard-edit"]').click();
  await page.waitForFunction(key => JSON.parse(localStorage.getItem(key))?.dashboard?.cards?.length === 1, key);
  await page.reload({ waitUntil: "domcontentloaded" });
  assert.equal(await page.evaluate(key => JSON.parse(localStorage.getItem(key)).dashboard.cards[0].id, key), "offscreen-memory");
  // A names-only table retains identities without hidden numerical metric demand.
  await page.locator('.monitor-work-region[aria-label="资源列表"]').scrollIntoViewIfNeeded();
  await page.locator(".resource-table-row[data-resource-row-id]").first().waitFor();
  const facts = await telemetry();
  const queries = Object.entries(facts["/api/resource-facts/subscribe"]?.inFlightByQuery ?? {}).filter(([, n]) => n > 0);
  assert(queries.length > 0);
  for (const [query] of queries) {
    const ids = new URLSearchParams(query).getAll("items").flatMap(value => value.split(",")).sort();
    assert.deepEqual(ids, ["process.attribution", "process.inventory"]);
  }
  await page.evaluate(key => {
    const preferences = JSON.parse(localStorage.getItem(key));
    preferences.dashboard.resourceTableColumns = [];
    preferences.dashboard.resourceTableProcessColumns = [];
    localStorage.setItem(key, JSON.stringify(preferences));
  }, key);
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.locator('.monitor-work-region[aria-label="资源列表"]').scrollIntoViewIfNeeded();
  await page.waitForTimeout(500);
  assert.equal((await telemetry())["/api/resource-facts/subscribe"]?.logicalInFlight ?? 0, 0,
    "An explicit empty table selection holds no subscription");
  await page.getByRole("button", { name: "设置", exact: true }).click();
  await page.getByRole("button", { name: "外观", exact: true }).click();
  const text = await loadSettingsText("zh-CN");
  await page.getByRole("button", { name: text.appearance.resetCache, exact: true }).click();
  await page.waitForFunction(key => localStorage.getItem(key) === null, key);
  await page.getByRole("button", { name: "监视控制台", exact: true }).click();
  await page.waitForFunction(() => document.querySelectorAll(".metric-card").length === 2);
  assert.equal(await page.evaluate(key => localStorage.getItem(key), key), null);
  assert.deepEqual(dashboardRequests, []);
  assert.deepEqual(errors, []);
  return { perCardVisibility: true, hiddenDashboardReleased: true, restart: true, reset: true, namesOnlyFacts: true, noBackendLayout: true };

  async function telemetry() { return await (await fetch(`${baseUrl}/__test/requests`)).json(); }
  async function waitIds(expected) {
    const deadline = Date.now() + 15000;
    while (Date.now() < deadline) {
      const values = (await telemetry())["/api/metrics/subscribe"]?.inFlightByQuery ?? {};
      const ids = [...new Set(Object.entries(values).filter(([, n]) => n > 0)
        .flatMap(([query]) => (new URLSearchParams(query).get("ids") ?? "").split(",").filter(Boolean)))].sort();
      if (JSON.stringify(ids) === JSON.stringify(expected)) return;
      await page.waitForTimeout(50);
    }
    throw new Error(`Expected visible metrics ${JSON.stringify(expected)}`);
  }
}
