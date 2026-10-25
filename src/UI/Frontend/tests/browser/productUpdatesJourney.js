import assert from "node:assert/strict";
import { productUpdateCopy } from "../../src/features/settings/productUpdatePresentation.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";

export default async function productUpdatesJourney(page, baseUrl) {
  const copy = productUpdateCopy("zh-CN");
  const text = (await loadSettingsText("zh-CN")).updates;
  let statusReads = 0;
  let applyCalls = 0;
  const patches = [];
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  page.on("dialog", dialog => dialog.accept());
  page.on("request", request => {
    const path = new URL(request.url()).pathname;
    if (request.method() === "PATCH" && path === "/api/settings/app") patches.push(request.postDataJSON());
    if (path === "/api/updates/product/status") statusReads++;
  });
  // 更新状态只来自统一订阅通道的推送；"invalid" 让推送帧不合合同，界面按状态不可用处理。
  const publish = async status => assert.ok((await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ productUpdateStatus: status }) })).ok);
  const ready = { stage: "pendingApply", targetVersion: "1.1.0", checkResult: "available", availableVersion: "1.1.0" };
  await page.route("**/api/updates/product/versions", route => route.fulfill({ json: {
    installedVersion: "1.0.0", status: "loaded", complete: true, checkedAt: new Date().toISOString(), options: [{ choice: "latest", version: "1.2.0", selectable: true, available: true }]
  } }));
  await page.route("**/api/updates/product/apply", route => {
    assert.equal(route.request().method(), "POST");
    applyCalls++;
    return route.fulfill({ status: 202, json: { stage: "waitingForExit", targetVersion: "1.1.0" } });
  });
  await publish("invalid");
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "设置", exact: true }).click();
  await page.getByRole("button", { name: "更新与版本", exact: true }).click();
  await page.getByRole("alert").filter({ hasText: copy.unavailable }).waitFor();
  // 更新区自己的“立即应用”；同一版本的界面内提示也带一个，由 update-notice 旅程覆盖。
  const region = page.locator(".settings-content");
  assert.equal(await region.getByRole("button", { name: copy.apply, exact: true }).count(), 0);
  await publish(ready);
  const apply = region.getByRole("button", { name: copy.apply, exact: true });
  await apply.waitFor({ timeout: 7000 });
  // 1.1.0 是新版本，界面内提示一直显示到用户关闭；它停在页面右上角，先关掉再操作设置页头部的保存。
  const notice = page.locator(".toast").filter({ hasText: text.noticeTitle });
  await notice.getByRole("button", { name: "关闭", exact: true }).click();
  await page.getByText("已选择目标版本：1.1.0", { exact: true }).waitFor();
  await page.locator('input[value="latest"]').check();
  assert.equal(await page.getByRole("button", { name: "准备所选更新", exact: true }).isDisabled(), true,
    "A pending package must prevent preparing a replacement while Apply now remains available");
  assert.equal(await apply.isEnabled(), true);
  await publish("invalid");
  await page.getByRole("alert").filter({ hasText: copy.stale }).waitFor({ timeout: 7000 });
  assert.equal(await apply.count(), 0, "A stale package status still exposed Apply now");
  await publish(ready);
  await apply.waitFor({ timeout: 7000 });
  const interval = page.getByRole("textbox", { name: text.intervalTitle, exact: true });
  assert.equal(await interval.inputValue(), "24");
  await page.getByRole("checkbox", { name: text.periodicTitle, exact: true }).check();
  for (const hours of [72, 24]) {
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
  assert.equal(statusReads, 0, "The update region polled the status instead of using the push");
  assert.deepEqual(errors, []);
  return { statusReads, applyCalls, intervalSaves: patches.map(patch => patch.changes.update.checkIntervalHours), recoveredInitialFailure: true, recoveredStaleStatus: true };
}
