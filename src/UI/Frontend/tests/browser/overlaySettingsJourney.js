import { NOT_GHOST } from "./liveLocator.js";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { pointOverlayPresentation } from "../../src/preferences/frontendPreferences.ts";
import { installElevatedBackendBridge } from "./elevatedBackendBridge.js";

export default async function overlaySettingsJourney(page, baseUrl) {
  const copy = await loadAppCopy("zh-CN");
  const assert = (value, message) => { if (!value) throw new Error(message); };
  const errors = [];
  const backendOverlayRequests = [];
  page.on("pageerror", error => errors.push(String(error)));
  page.on("request", request => {
    const path = new URL(request.url()).pathname;
    if (path.startsWith("/api/startup-modules/") || path.startsWith("/api/performance-overlay/")) backendOverlayRequests.push({ method: request.method(), path });
  });
  const patch = async value => {
    const response = await fetch(`${baseUrl}/__test/scenario`, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(value)
    });
    assert(response.ok, "Scenario update failed");
  };
  await page.setViewportSize({ width: 1200, height: 950 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const modulePath = "/api/startup-modules/software/frontend-harness.software";
  const plain = await page.evaluate(async path => (await fetch(path, { method: "PUT", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ softwareId: "frontend-harness.software", moduleIds: [] }) })).status, modulePath);
  assert(plain === 403, "Ordinary default-scope module mutation must be denied");
  for (const invalid of [
    { path: "https://untrusted.invalid/api/startup-modules/software/frontend-harness.software", body: { softwareId: "frontend-harness.software", moduleIds: [] } },
    { path: modulePath, body: { softwareId: "frontend-harness.software", moduleIds: [], enabled: true } }
  ]) {
    const denied = await (await fetch(`${baseUrl}/__test/elevated-backend-request`, { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ type: "backend.elevatedRequest", requestId: "invalid-bridge-fixture", method: "PUT", path: invalid.path, body: JSON.stringify(invalid.body) }) })).json();
    assert(denied.error === "unavailable" || denied.statusCode === 400, "Native fixture accepted an invalid mutation route/body");
  }
  await page.getByRole("button", { name: "组件与软件", exact: true }).click();
  await page.getByRole("tablist", { name: "组件和软件分类" })
    .getByRole("tab", { name: /一般应用/ }).click();
  const card = page.locator(".software-item").filter({ hasText: "示例软件" }).first();
  const open = async () => {
    await card.getByRole("button", { name: "软件设置" }).click();
    await page.getByRole("tab", { name: copy.performanceOverlay.tab, exact: true }).click();
    await page.locator(`.overlay-settings-fields${NOT_GHOST}`).waitFor({ state: "visible" });
  };
  await open();
  await installElevatedBackendBridge(page);
  const editor = page.locator(`.overlay-settings${NOT_GHOST}`);
  const save = editor.getByRole("button", { name: copy.softwareDetail.save, exact: true });
  const font = editor.getByRole("spinbutton", { name: copy.performanceOverlay.fontSizePx });
  await editor.getByRole("checkbox").check();
  await editor.getByRole("combobox", { name: copy.performanceOverlay.mode, exact: true }).click();
  await page.getByRole("option", { name: copy.performanceOverlay.injected, exact: true }).click();
  await font.fill("21");
  // Exercise the shared picker and ordering before persisting the complete object.
  await editor.getByRole("button", { name: copy.performanceOverlay.chooseMetrics }).click();
  await page.getByRole("button", { name: copy.metricPicker.confirm, exact: true }).click();
  const before = await editor.locator(".overlay-settings-metrics li span").allTextContents();
  await editor.getByRole("button", { name: copy.performanceOverlay.moveDown, exact: true }).first().click();
  const ordered = await editor.locator(".overlay-settings-metrics li span").allTextContents();
  assert(ordered[1] === before[0], "Metric order did not change");
  await patch({ overlaySaveDelayMs: 1500 });
  const sent = page.waitForRequest(request => request.method() === "POST" && request.url().endsWith("/__test/elevated-backend-request"));
  const completed = page.waitForResponse(response => response.request().method() === "POST"
    && response.url().endsWith("/__test/elevated-backend-request"));
  // 后面的断言失败时页面会关掉，这个等待随之拒绝：先接住，免得未处理的拒绝打断整个工作进程、盖住真正的失败。
  completed.catch(() => undefined);
  await save.click();
  const request = await sent;
  const command = request.postDataJSON();
  const selection = JSON.parse(command.body);
  assert(command.type === "backend.elevatedRequest" && command.method === "PUT" && command.path === modulePath, "Mutation bypassed the explicit native bridge");
  assert(JSON.stringify(Object.keys(selection).sort()) === JSON.stringify(["moduleIds", "softwareId"]), "Renderer preferences entered the backend startup-module request");
  assert(JSON.stringify(selection.moduleIds) === JSON.stringify(["performance-overlay-preload", "performance-overlay-vulkan"]), "Injected mode did not select its declared startup modules");
  // 一次读完所有控件（逐个往返读时保存可能已经结束、编辑区重新挂上）。
  const editable = await editor.evaluate(root => [...root.querySelectorAll("input, button")]
    .filter(control => !control.disabled && !control.closest("[data-exit-ghost]")).map(control => control.outerHTML.slice(0, 200)));
  assert(editable.length === 0, `Overlay controls remained editable during save: ${editable.join(" | ")}`);
  // Even a dispatched event cannot change the pending draft.
  await font.evaluate(element => { element.value = "36"; element.dispatchEvent(new Event("input", { bubbles: true })); });
  assert((await (await completed).json()).statusCode === 200, "Delayed elevated save failed");
  await page.waitForFunction(() => !document.querySelector('.overlay-settings input[type="number"]')?.disabled);
  assert(await font.inputValue() === "21", "Pending edit guard lost the submitted value");
  assert(await save.isDisabled(), "Successful save left the editor dirty");
  await page.getByRole("dialog", { name: "示例软件", exact: true }).getByRole("button", { name: "关闭", exact: true }).first().click();
  await open();
  assert(await font.inputValue() === "21", "Reopening lost saved font size");
  assert(await editor.getByRole("checkbox").isChecked(), "Reopening lost enabled state");
  assert(JSON.stringify(await editor.locator(".overlay-settings-metrics li span").allTextContents()) === JSON.stringify(ordered), "Reopening lost metric order");
  const beforeAppearanceSave = backendOverlayRequests.length;
  const beforeAppearanceBridge = await page.evaluate(() => window.__elevatedBackendRequests.length);
  await font.fill("22");
  await save.click();
  await page.waitForFunction(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1"))?.overlays["frontend-harness.software"]?.fontSizePx === 22);
  assert(backendOverlayRequests.length === beforeAppearanceSave, "Appearance-only save requested backend module state");
  assert(await page.evaluate(() => window.__elevatedBackendRequests.length) === beforeAppearanceBridge, "Appearance-only save prompted for elevation");
  await patch({ overlaySaveDelayMs: 0, overlayElevationOutcome: "canceled" });
  await font.fill("27");
  await editor.getByRole("checkbox").uncheck();
  await save.click();
  await editor.getByRole("alert").filter({ hasText: copy.misc.transportCanceled }).waitFor({ state: "visible" });
  assert(await font.inputValue() === "27" && await save.isEnabled(), "Canceled UAC lost the draft or retry");
  const canceledCache = await page.evaluate(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1")));
  assert(canceledCache.overlays["frontend-harness.software"].fontSizePx === 22 && canceledCache.overlays["frontend-harness.software"].enabled === true, "Canceled UAC committed frontend preferences");
  await patch({ overlayElevationOutcome: "unavailable" });
  const unavailable = page.waitForResponse(response => response.url().endsWith("/__test/elevated-backend-request"));
  await save.click();
  await unavailable;
  await page.waitForFunction(() => !document.querySelector('.overlay-settings input[type="number"]')?.disabled);
  assert(await font.inputValue() === "27" && await save.isEnabled(), "Failed native bridge lost the draft or retry");
  const unavailableCache = await page.evaluate(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1")));
  assert(unavailableCache.overlays["frontend-harness.software"].fontSizePx === 22 && unavailableCache.overlays["frontend-harness.software"].enabled === true, "Failed native bridge committed frontend preferences");
  await patch({ overlayElevationOutcome: null, overlaySaveFailure: true });
  await font.fill("27");
  await editor.getByRole("checkbox").uncheck();
  await save.click();
  await editor.getByRole("alert").waitFor({ state: "visible" });
  assert(await font.inputValue() === "27", "Failed save lost the draft");
  assert(await save.isEnabled(), "Failed save cannot be retried");
  const persisted = await page.evaluate(async url => (await fetch(url)).json(), modulePath);
  assert(JSON.stringify(persisted.moduleIds) === JSON.stringify(selection.moduleIds)
    && !("enabled" in persisted) && !("mode" in persisted) && !("fontSizePx" in persisted), "Failed operation changed module selection or backend retained renderer preferences");
  const cached = await page.evaluate(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1")));
  assert(Object.values(cached.overlays).some(value => value.fontSizePx === 22 && value.enabled === true && value.mode === "injected"), "Failed operation changed the frontend cache");
  await patch({ overlaySaveFailure: false });
  await save.click();
  await editor.getByRole("alert").waitFor({ state: "hidden" });
  await page.waitForFunction(() => !document.querySelector('.overlay-settings input[type="number"]')?.disabled);
  assert(await save.isDisabled(), "Successful retry left the editor dirty");
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "组件与软件", exact: true }).click();
  await page.getByRole("tablist", { name: "组件和软件分类" }).getByRole("tab", { name: /一般应用/ }).click();
  await open();
  await installElevatedBackendBridge(page);
  assert(await font.inputValue() === "27", "Frontend overlay cache did not survive page restart");
  await editor.getByRole("button", { name: (await loadSettingsText("zh-CN")).actions.restoreDefaults, exact: true }).click();
  await page.waitForFunction(fontPx => document.querySelector('.overlay-settings input[type="number"]')?.value === String(fontPx), pointOverlayPresentation().fontSizePx);
  const resetCache = await page.evaluate(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1")));
  assert(resetCache.overlays["frontend-harness.software"].enabled === false && resetCache.overlays["frontend-harness.software"].mode === "injected", "Appearance reset changed local enabled/mode preferences");
  await font.fill("31");
  await save.click();
  await page.waitForFunction(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1"))?.overlays["frontend-harness.software"]?.fontSizePx === 31);
  await page.getByRole("dialog", { name: "示例软件", exact: true }).getByRole("button", { name: "关闭", exact: true }).first().click();
  const settingsText = await loadSettingsText("zh-CN");
  await page.getByRole("button", { name: copy.page.settings, exact: true }).click();
  await page.getByRole("button", { name: settingsText.sections.appearance, exact: true }).click();
  await page.getByRole("button", { name: settingsText.appearance.resetCache, exact: true }).click();
  await page.waitForFunction(fontSizePx => {
    const value = JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1"));
    return value?.overlays["frontend-harness.software"]?.mode === "injected"
      && value.overlays["frontend-harness.software"].fontSizePx === fontSizePx;
  }, pointOverlayPresentation().fontSizePx);
  const globallyReset = await page.evaluate(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1")));
  assert(globallyReset.overlays["frontend-harness.software"].enabled === false, "Global presentation reset changed local enable choice");
  await page.getByRole("button", { name: copy.page.components, exact: true }).click();
  await page.getByRole("tablist", { name: "组件和软件分类" }).getByRole("tab", { name: /一般应用/ }).click();
  await open();
  await editor.getByRole("checkbox").check();
  await save.click();
  await page.waitForFunction(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1"))?.overlays["frontend-harness.software"]?.enabled === true);
  const removed = await (await fetch(`${baseUrl}/__test/elevated-backend-request`, { method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ type: "backend.elevatedRequest", requestId: "external-admin-fixture", method: "PUT", path: modulePath,
      body: JSON.stringify({ softwareId: "frontend-harness.software", moduleIds: [] }) }) })).json();
  assert(removed.statusCode === 200, "External generic admin module-selection update failed");
  await page.getByRole("dialog", { name: "示例软件", exact: true }).getByRole("button", { name: "关闭", exact: true }).first().click();
  await open();
  assert(!await editor.getByRole("checkbox").isChecked(), "Stale local enabled preference hid absent injection modules");
  await editor.getByRole("checkbox").check();
  const reenabled = page.waitForResponse(response => response.url().endsWith("/__test/elevated-backend-request"));
  await save.click();
  await reenabled;
  await page.waitForFunction(() => !document.querySelector('.overlay-settings input[type="number"]')?.disabled);
  assert(await editor.getByRole("checkbox").isChecked(), "Enabling once did not restore the missing module selection");
  // 后端登记与这里的选择不一致时必须可见，并允许保存来对齐（外部管理员改过、只登记了一部分）。
  const adminModules = async moduleIds => {
    const result = await (await fetch(`${baseUrl}/__test/elevated-backend-request`, { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ type: "backend.elevatedRequest", requestId: `admin-${moduleIds.length}`, method: "PUT", path: modulePath,
        body: JSON.stringify({ softwareId: "frontend-harness.software", moduleIds }) }) })).json();
    assert(result.statusCode === 200, "External admin module-selection fixture failed");
  };
  const closeDetail = () => page.getByRole("dialog", { name: "示例软件", exact: true }).getByRole("button", { name: "关闭", exact: true }).first().click();
  const lastBridgeSelection = async () => JSON.parse((await page.evaluate(() => window.__elevatedBackendRequests.at(-1))).body).moduleIds;
  const saveAndSettle = async () => {
    const done = page.waitForResponse(response => response.url().endsWith("/__test/elevated-backend-request"));
    await save.click();
    await done;
    await page.waitForFunction(() => !document.querySelector('.overlay-settings input[type="number"]')?.disabled);
  };
  await adminModules(["performance-overlay-preload"]);
  await closeDetail();
  await open();
  await editor.getByRole("status").filter({ hasText: copy.performanceOverlay.injectedModulesPartial }).waitFor();
  assert(!await editor.getByRole("checkbox").isChecked(), "Partially registered injection was shown as enabled");
  assert(await save.isEnabled(), "A module mismatch cannot be saved to align the backend");
  await saveAndSettle();
  assert(JSON.stringify(await lastBridgeSelection()) === "[]", "Saving a partial registration did not align it to the disabled choice");
  assert(await editor.getByRole("status").filter({ hasText: copy.performanceOverlay.injectedModulesPartial }).count() === 0, "Partial notice stayed after alignment");
  await editor.getByRole("combobox", { name: copy.performanceOverlay.mode, exact: true }).click();
  await page.getByRole("option", { name: copy.performanceOverlay.external, exact: true }).click();
  await save.click();
  await page.waitForFunction(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1"))?.overlays["frontend-harness.software"]?.mode === "external");
  await adminModules(["performance-overlay-preload", "performance-overlay-vulkan"]);
  await closeDetail();
  await open();
  await editor.getByRole("status").filter({ hasText: copy.performanceOverlay.externalModeModulesPresent }).waitFor();
  assert(await save.isEnabled(), "External mode with registered modules cannot be saved to align the backend");
  await saveAndSettle();
  assert(JSON.stringify(await lastBridgeSelection()) === "[]", "External mode left the injection modules registered");
  assert(await editor.getByRole("status").filter({ hasText: copy.performanceOverlay.externalModeModulesPresent }).count() === 0, "External-mode notice stayed after alignment");
  // 启动模块已写入但偏好没有保存：明确告诉用户后端已更新、偏好未保存。
  await editor.getByRole("checkbox").check();
  await editor.getByRole("combobox", { name: copy.performanceOverlay.mode, exact: true }).click();
  await page.getByRole("option", { name: copy.performanceOverlay.injected, exact: true }).click();
  await page.evaluate(() => {
    window.__originalSetItem = Storage.prototype.setItem;
    Storage.prototype.setItem = function (key, value) {
      if (key === "resource-manager:frontend-preferences:v1") throw new Error("fixture quota exceeded");
      return window.__originalSetItem.call(this, key, value);
    };
  });
  await save.click();
  await editor.getByRole("alert").filter({ hasText: copy.performanceOverlay.modulesSavedPreferencesFailed }).waitFor();
  await page.evaluate(() => { Storage.prototype.setItem = window.__originalSetItem; });
  assert(JSON.stringify(await lastBridgeSelection()) === JSON.stringify(["performance-overlay-preload", "performance-overlay-vulkan"]), "Modules were not written before the failed preference save");
  assert(await save.isEnabled(), "A failed preference save cannot be retried");
  await save.click();
  await editor.getByRole("alert").waitFor({ state: "hidden" });
  await page.waitForFunction(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1"))?.overlays["frontend-harness.software"]?.mode === "injected");
  assert(backendOverlayRequests.every(({ path }) => path.startsWith("/api/startup-modules/") || path === "/api/performance-overlay/metrics/catalog"), "Renderer requested a removed backend overlay settings route");
  assert(backendOverlayRequests.filter(({ method }) => method === "PUT").length === 1, "Product tried to write modules with the default web key");
  // 数字框打到一半时（清空、"0."）不能被改写成 0；离开框后显示的是会保存的草稿值。
  const defaults = pointOverlayPresentation();
  const margin = editor.getByRole("spinbutton", { name: copy.performanceOverlay.marginPx });
  await margin.fill("");
  assert(await margin.inputValue() === "", `Clearing the margin was rewritten to ${await margin.inputValue()}`);
  assert(await save.isDisabled(), "An empty number box changed the draft");
  await margin.blur();
  assert(await margin.inputValue() === String(defaults.marginPx), "Leaving an empty box did not show the draft value");
  await editor.getByRole("combobox", { name: copy.performanceOverlay.sizeMode, exact: true }).click();
  await page.getByRole("option", { name: copy.performanceOverlay.windowRatio, exact: true }).click();
  const width = editor.getByRole("spinbutton", { name: copy.performanceOverlay.regionWidthRatio });
  await width.fill("");
  await width.pressSequentially("0.5");
  assert(await width.inputValue() === "0.5", `Typing 0.5 ended as ${await width.inputValue()}`);
  await width.blur();
  assert(await width.inputValue() === "0.5", "Leaving the box changed a typed 0.5");
  assert(await save.isEnabled(), "A typed 0.5 did not reach the draft");
  assert(errors.length === 0, errors.join("\n"));
  return { numberBoxesKeepPartialInput: true, persistence: true, metricOrdering: true, pendingControlsLocked: true, appearanceSaveStaysLocal: true, genericModulePayload: true, explicitNativeElevation: true, ordinaryWriteDenied: true, canceledElevationRetainsDraft: true, failedBridgeRetainsDraft: true, actualInjectedModuleJoin: true, failureRetainsDraft: true, retry: true, moduleMismatchVisible: true, preferenceFailureAfterModulesVisible: true };
}
