import assert from "node:assert/strict";
import { waitForCondition, waitForStableRendering } from "./journeyWaits.js";

export default async function controlReadingsJourney(page, baseUrl) {
  const writes = [];
  page.on("request", request => {
    if (new URL(request.url()).pathname.startsWith("/api/control/")
      && !["GET", "HEAD"].includes(request.method())) writes.push(request.url());
  });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "控制面", exact: true }).click();
  await page.locator(".control-readings dd").filter({ hasText: "89" }).waitFor();
  const result = await page.evaluate(() => {
    const reading = document.querySelector(".control-readings");
    const ownership = document.querySelector(".control-ownership");
    const top = Boolean(reading.compareDocumentPosition(ownership) & Node.DOCUMENT_POSITION_FOLLOWING);
    const toggles = [...document.querySelectorAll(".control-capability-actual")].map(x => x.textContent);
    if (!top || reading.querySelector("h4")) throw new Error("Readings must precede controls without heading");
    if (!toggles.some(x => x.includes("关闭"))) throw new Error("Localized false toggle was dropped");
    if (reading.textContent.includes("Voltage")) throw new Error("Root-locked editor became read-only");
    return { top, toggles, reading: reading.textContent };
  });
  await page.getByRole("radio", { name: "软件管理", exact: true }).click();
  const boost = page.getByRole("radiogroup", { name: "Dynamic Boost 开关", exact: true });
  await boost.getByRole("radio", { name: "开启", exact: true }).waitFor();
  if (await page.getByRole("checkbox", { name: "Dynamic Boost 开关", exact: true }).count() !== 0)
    throw new Error("Toggle capability must not keep the row checkbox next to its three-way choice");
  // A changed catalog label proves this refresh was committed; the old checked/enabled
  // values alone could already be true before the response was consumed.
  let catalogRefresh = 0;
  await page.route("**/api/control/objects", async route => {
    const response = await route.fetch();
    const catalog = await response.json();
    catalogRefresh += 1;
    for (const object of catalog.objects) {
      object.reportedName = `${object.reportedName ?? object.id} [catalog refresh ${catalogRefresh}]`;
    }
    await route.fulfill({ response, json: catalog });
  });
  const refresh = page.waitForResponse(response => response.url().endsWith("/api/control/objects"), { timeout: 5000 });
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await refresh;
  await page.locator(".control-object").filter({ hasText: "[catalog refresh 1]" }).first().waitFor({ timeout: 5000 });
  await waitForStableRendering(page, [".control-object"]);
  if (await boost.getByRole("radio", { name: "开启", exact: true }).isDisabled())
    throw new Error("Catalog refresh lost software-management intent and relocked the control");
  await boost.getByRole("radio", { name: "开启", exact: true }).click();
  const editedRefresh = page.waitForResponse(response => response.url().endsWith("/api/control/objects"), { timeout: 5000 });
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  await editedRefresh;
  await page.locator(".control-object").filter({ hasText: "[catalog refresh 2]" }).first().waitFor({ timeout: 5000 });
  await waitForStableRendering(page, [".control-object"]);
  if (await boost.getByRole("radio", { name: "开启", exact: true }).getAttribute("aria-checked") !== "true")
    throw new Error("Catalog refresh discarded the edited setting");
  if (await page.getByRole("checkbox", { name: "Voltage", exact: true }).count() !== 0)
    throw new Error("Normal mode exposed Root-only setting");
  // Failed, hidden and resumed actual-state reads must never change the draft or write it.
  await patch({ controlActualValues: "invalid-values" });
  await page.waitForFunction(() => !document.querySelector(".control-readings"));
  assert.equal(await boost.getByRole("radio", { name: "开启", exact: true }).getAttribute("aria-checked"), "true");
  await patch({ controlActualValues: null });
  await page.locator(".control-readings dd").filter({ hasText: "89" }).waitFor();
  await visibility(false);
  await wait(t => t["/api/control/actual/subscribe"]?.logicalInFlight === 0);
  assert.equal(await page.locator(".control-readings").count(), 1, "Hidden demand keeps the current actual state");
  await patch({ controlActualValues: "invalid-values" });
  await visibility(true);
  await wait(t => t["/api/control/actual/subscribe"]?.logicalInFlight === 1);
  await page.waitForFunction(() => !document.querySelector(".control-readings"), null, { timeout: 5000 });
  assert.equal(await page.locator(".control-readings").count(), 0, "A newer failure replaces the retained actual state");
  assert.equal(await boost.getByRole("radio", { name: "开启", exact: true }).getAttribute("aria-checked"), "true");
  assert.deepEqual(writes, [], "Subscription changes must never apply a draft or write control state");
  await patch({ controlActualValues: null });
  await page.locator(".control-readings dd").filter({ hasText: "89" }).waitFor();
  await page.getByRole("radio", { name: "固件自动管理", exact: true }).click();
  if (!await boost.getByRole("radio", { name: "开启", exact: true }).isDisabled())
    throw new Error("Explicit firmware ownership did not lock controls");
  return { ...result, takeoverSurvivesRefresh: true, failedValueCleared: true, hiddenValueRetained: true, noSubscriptionWrites: true };

  async function patch(value) {
    assert.equal((await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(value) })).ok, true);
  }
  async function visibility(visible) {
    await page.evaluate(visible => {
      Object.defineProperty(document, "visibilityState", { configurable: true, value: visible ? "visible" : "hidden" });
      document.dispatchEvent(new Event("visibilitychange"));
    }, visible);
  }
  async function wait(predicate) {
    await waitForCondition(page, async () => predicate(await (await fetch(`${baseUrl}/__test/requests`)).json()), {
      timeoutMs: 10000, description: "Control subscription telemetry did not reach expected state"
    });
  }
}
