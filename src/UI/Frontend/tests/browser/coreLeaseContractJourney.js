import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";

export default async function coreLeaseContractJourney(page, baseUrl) {
  const app = await loadAppCopy("zh-CN");
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  const scenario = async patch => {
    const response = await fetch(`${baseUrl}/__test/scenario`, {
      method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(patch)
    });
    assert.equal(response.status, 200);
  };
  await scenario({ runtimeEffectOwners: true, gpuPlacementEnabled: true });
  // GET may observe absence after the topology subscription's last delivered value.
  await page.route("**/api/cpu/topology/exclusive-bindings", route => route.fulfill({
    json: { state: "unavailable", reasonCode: "cpu-topology-not-published" }
  }));
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "详细信息", exact: true }).click();
  await page.getByRole("tab", { name: "CPU 调度模型", exact: true }).click();
  const panel = page.getByLabel(app.cpuTopology.modelLabel);
  await panel.getByText("Frontend Harness CPU", { exact: true }).waitFor({ timeout: 65_000 });
  await panel.getByRole("button", { name: app.cpuTopology.details, exact: true }).click();
  const details = page.getByRole("dialog", { name: app.cpuTopology.coreDetailTitle, exact: true });
  const unavailable = app.resourceTable.unavailableReasons["cpu-topology-not-published"];
  await details.getByText(unavailable.label, { exact: true }).waitFor();
  assert.equal(await details.getByText(unavailable.label, { exact: true }).getAttribute("title"), unavailable.hint);
  assert.equal(await details.getByText(app.cpuTopology.noBindings, { exact: true }).count(), 0);
  assert.equal(await details.getByText(app.cpuTopology.loadingSoftwarePolicy, { exact: true }).count(), 0);
  await page.keyboard.press("Escape");
  const write = async action => {
    const response = page.waitForResponse(item => new URL(item.url()).pathname === "/api/cpu/topology/performance-overrides"
      && ["PUT", "DELETE"].includes(item.request().method()));
    await action();
    return response;
  };
  await panel.getByRole("button", { name: app.cpuTopology.edit, exact: true }).click();
  const saved = await write(() => panel.getByRole("button", { name: app.cpuTopology.save, exact: true }).click());
  assert.equal((await saved.json()).runtimeApplicationDisposition, "committedAndPublished");
  await panel.getByRole("button", { name: app.cpuTopology.edit, exact: true }).waitFor();
  assert.equal(await panel.getByText(app.softwareActions.processPolicySavedRuntimePending, { exact: true }).count(), 0);
  await scenario({ runtimePolicyDisposition: "committedWithDeliveryFailures" });
  await panel.getByRole("button", { name: app.cpuTopology.edit, exact: true }).click();
  const partial = await write(() => panel.getByRole("button", { name: app.cpuTopology.save, exact: true }).click());
  assert.equal(partial.status(), 202);
  await panel.getByText(app.softwareActions.processPolicySavedRuntimePending, { exact: true }).waitFor();
  await panel.getByRole("button", { name: app.softwareActions.retryRuntimePublication, exact: true }).click();
  await panel.getByRole("button", { name: app.cpuTopology.edit, exact: true }).waitFor();
  await scenario({ runtimePolicyDisposition: "committedAndPublished" });
  await panel.getByRole("button", { name: app.cpuTopology.edit, exact: true }).click();
  const reset = await write(() => panel.getByRole("button", { name: app.cpuTopology.reset, exact: true }).click());
  assert.equal((await reset.json()).runtimeApplicationDisposition, "committedAndPublished");
  await panel.getByRole("button", { name: app.cpuTopology.edit, exact: true }).waitFor();
  assert.deepEqual(errors, []);
  return { unavailable: "cpu-topology-not-published", save: "published", deliveryFailure: "pending", retry: "published", reset: "published" };
}
