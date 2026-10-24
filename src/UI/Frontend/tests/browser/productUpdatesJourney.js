import assert from "node:assert/strict";
import { productUpdateCopy } from "../../src/features/settings/productUpdatePresentation.ts";

export default async function productUpdatesJourney(page, baseUrl) {
  const copy = productUpdateCopy("zh-CN");
  let mode = "unavailable";
  let statusReads = 0;
  let applyCalls = 0;
  const patches = [];
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  page.on("dialog", dialog => dialog.accept());
  page.on("request", request => {
    if (request.method() === "PATCH" && new URL(request.url()).pathname === "/api/settings/app") patches.push(request.postDataJSON());
  });
  await page.route("**/api/updates/product/versions", route => route.fulfill({ json: {
    installedVersion: "1.0.0", status: "loaded", complete: true, checkedAt: new Date().toISOString(), options: [{ choice: "latest", version: "1.2.0", selectable: true, available: true }]
  } }));
  await page.route("**/api/updates/product/status", route => {
    statusReads++;
    return mode === "unavailable"
      ? route.fulfill({ status: 503, json: { errorCode: "update.statusUnavailable" } })
      : route.fulfill({ json: { stage: "pendingApply", targetVersion: "1.1.0", checkResult: "available" } });
  });
  await page.route("**/api/updates/product/apply", route => {
    assert.equal(route.request().method(), "POST");
    applyCalls++;
    return route.fulfill({ status: 202, json: { stage: "waitingForExit", targetVersion: "1.1.0" } });
  });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "设置", exact: true }).click();
  await page.getByRole("button", { name: "更新与版本", exact: true }).click();
  await page.getByRole("alert").filter({ hasText: copy.unavailable }).waitFor();
  assert.equal(await page.getByRole("button", { name: copy.apply, exact: true }).count(), 0);
  const initialReads = statusReads;
  mode = "ready";
  const apply = page.getByRole("button", { name: copy.apply, exact: true });
  await apply.waitFor({ timeout: 7000 });
  assert.ok(statusReads > initialReads, "Initial failure stopped status polling");
  await page.getByText("已选择目标版本：1.1.0", { exact: true }).waitFor();
  await page.locator('input[value="latest"]').check();
  assert.equal(await page.getByRole("button", { name: "准备所选更新", exact: true }).isDisabled(), true,
    "A pending package must prevent preparing a replacement while Apply now remains available");
  assert.equal(await apply.isEnabled(), true);
  mode = "unavailable";
  await page.getByRole("alert").filter({ hasText: copy.stale }).waitFor({ timeout: 7000 });
  assert.equal(await apply.count(), 0, "A stale package status still exposed Apply now");
  mode = "ready";
  await apply.waitFor({ timeout: 7000 });
  const interval = page.getByRole("spinbutton", { name: copy.interval });
  assert.equal(await interval.inputValue(), "6");
  for (const hours of [24, 6]) {
    await interval.fill(String(hours));
    await interval.blur();
    const response = page.waitForResponse(response => response.request().method() === "PATCH"
      && new URL(response.url()).pathname === "/api/settings/app");
    await page.getByRole("button", { name: "保存", exact: true }).click();
    assert.ok((await response).ok());
    assert.equal(patches.at(-1)?.changes?.update?.checkIntervalHours, hours);
  }
  await page.evaluate(() => {
    window.__updateHostMessages = [];
    window.unboundWebView = { postMessage: message => window.__updateHostMessages.push(message) };
  });
  await apply.click();
  await page.waitForFunction(() => window.__updateHostMessages.includes("update.applyExit"));
  assert.equal(applyCalls, 1);
  assert.deepEqual(errors, []);
  return { statusReads, applyCalls, intervalSaves: patches.map(patch => patch.changes.update.checkIntervalHours), recoveredInitialFailure: true, recoveredStaleStatus: true };
}
