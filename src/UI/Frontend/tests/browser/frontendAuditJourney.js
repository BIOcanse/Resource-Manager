import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";

export default async function frontendAuditJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  await patch({ operations: "mixed", runtimeEffectOwners: true, capabilities: "ready" });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "组件与软件", exact: true }).click();
  await wait(t => t["/api/components"]?.completed >= 1 && t["/api/operations/subscribe"]?.publications >= 1);
  const app = await loadAppCopy("zh-CN");
  await page.locator(".management-card-title").filter({ hasText: app.status.component.names.hardwareBridge }).waitFor();
  const componentResponse = await fetch(`${baseUrl}/api/components`);
  assert.equal(componentResponse.ok, true);
  const components = await componentResponse.json();
  assert.ok(components.length > 0, "The audit must exercise the current component decoder with actual facts");
  for (const component of components) {
    assert.equal("stateLabel" in component, false);
    for (const key of ["name", "category", "purpose", "installNote"])
      assert.equal(key in component.definition, false, `Component wire still contains ${key}`);
    for (const capability of component.definition.capabilities) assert.equal("label" in capability, false);
    for (const provider of component.providers) {
      assert.equal("name" in provider, false);
      assert.equal(typeof provider.message, "object");
    }
  }
  const before = await telemetry();
  await patch({ operationCanceled: true });
  await wait(t => t["/api/components"]?.completed > before["/api/components"].completed);
  const after = await telemetry();
  assert.equal(after["/api/software"]?.started ?? 0, before["/api/software"]?.started ?? 0,
    "A component completion must not reread unrelated software");
  await visible(false);
  await wait(t => t["/api/operations/subscribe"]?.logicalInFlight === 0
    && t["/api/subscriptions/stream"]?.physicalInFlight === 0);
  await visible(true);
  await wait(t => t["/api/operations/subscribe"]?.logicalInFlight === 1);
  assert.deepEqual(errors, []);
  return { componentFactsOnlyWire: true, localizedComponentFromFacts: true, componentCompletionRefreshesOnlyItsInventory: true, hiddenStreamReleased: true, visibleCurrentReadRestored: true };

  async function visible(value) {
    await page.evaluate(value => {
      Object.defineProperty(document, "visibilityState", { configurable: true, value: value ? "visible" : "hidden" });
      document.dispatchEvent(new Event("visibilitychange"));
    }, value);
  }
  async function telemetry() {
    return (await fetch(`${baseUrl}/__test/requests`)).json();
  }
  async function wait(predicate) {
    const deadline = Date.now() + 10000;
    while (Date.now() < deadline) {
      const value = await telemetry();
      if (predicate(value)) return;
      await new Promise(resolve => setTimeout(resolve, 50));
    }
    throw new Error("Audit telemetry did not reach expected state");
  }
  async function patch(data) {
    assert.equal((await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(data) })).ok, true);
  }
}
