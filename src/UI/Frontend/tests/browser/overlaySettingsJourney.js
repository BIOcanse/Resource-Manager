import { NOT_GHOST } from "./liveLocator.js";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { pointOverlayPresentation } from "../../src/preferences/frontendPreferences.ts";

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
  // 后端只收 softwareId 和 moduleIds：界面偏好（开关、方式、字号）混进来就被拒绝。
  const extraField = await page.evaluate(async path => (await fetch(path, { method: "PUT", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ softwareId: "frontend-harness.software", moduleIds: [], enabled: true }) })).status, modulePath);
  assert(extraField === 400, "The fixture accepted renderer preferences in a module selection");
  const moduleWrites = [];
  page.on("request", request => {
    if (request.method() === "PUT" && new URL(request.url()).pathname === modulePath) moduleWrites.push(request.postDataJSON());
  });
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
  const editor = page.locator(`.overlay-settings${NOT_GHOST}`);
  const save = editor.getByRole("button", { name: copy.softwareDetail.save, exact: true });
  const font = editor.getByRole("spinbutton", { name: copy.performanceOverlay.fontSizePx });
  await editor.getByRole("checkbox").check();
  await editor.getByRole("combobox", { name: copy.performanceOverlay.mode, exact: true }).click();
  await page.getByRole("option", { name: copy.performanceOverlay.injected, exact: true }).click();
  await font.fill("21");
  // Exercise the shared item picker (overlay use) and ordering before persisting the complete object.
  await editor.getByRole("button", { name: copy.performanceOverlay.chooseItems }).click();
  await page.getByRole("dialog", { name: copy.performanceOverlay.pickerTitle }).waitFor({ state: "visible" });
  await page.getByRole("button", { name: copy.metricPicker.confirm, exact: true }).click();
  const before = await editor.locator(".overlay-settings-metrics li span").allTextContents();
  await editor.getByRole("button", { name: copy.performanceOverlay.moveDown, exact: true }).first().click();
  const ordered = await editor.locator(".overlay-settings-metrics li span").allTextContents();
  assert(ordered[1] === before[0], "Item order did not change");
  await patch({ overlaySaveDelayMs: 1500 });
  const sent = page.waitForRequest(request => request.method() === "PUT" && request.url().endsWith(modulePath));
  const completed = page.waitForResponse(response => response.request().method() === "PUT" && response.url().endsWith(modulePath));
  // 后面的断言失败时页面会关掉，这个等待随之拒绝：先接住，免得未处理的拒绝打断整个工作进程、盖住真正的失败。
  completed.catch(() => undefined);
  await save.click();
  const selection = (await sent).postDataJSON();
  assert(JSON.stringify(Object.keys(selection).sort()) === JSON.stringify(["moduleIds", "softwareId"]), "Renderer preferences entered the backend startup-module request");
  assert(JSON.stringify(selection.moduleIds) === JSON.stringify(["performance-overlay-preload", "performance-overlay-vulkan"]), "Injected mode did not select its declared startup modules");
  // 一次读完所有控件（逐个往返读时保存可能已经结束、编辑区重新挂上）。
  const editable = await editor.evaluate(root => [...root.querySelectorAll("input, button")]
    .filter(control => !control.disabled && !control.closest("[data-exit-ghost]")).map(control => control.outerHTML.slice(0, 200)));
  assert(editable.length === 0, `Overlay controls remained editable during save: ${editable.join(" | ")}`);
  // Even a dispatched event cannot change the pending draft.
  await font.evaluate(element => { element.value = "36"; element.dispatchEvent(new Event("input", { bubbles: true })); });
  assert((await completed).status() === 200, "Delayed module save failed");
  await page.waitForFunction(() => !document.querySelector('.overlay-settings input[type="number"]')?.disabled);
  assert(await font.inputValue() === "21", "Pending edit guard lost the submitted value");
  assert(await save.isDisabled(), "Successful save left the editor dirty");
  await page.getByRole("dialog", { name: "示例软件", exact: true }).getByRole("button", { name: "关闭", exact: true }).first().click();
  await open();
  assert(await font.inputValue() === "21", "Reopening lost saved font size");
  assert(await editor.getByRole("checkbox").isChecked(), "Reopening lost enabled state");
  assert(JSON.stringify(await editor.locator(".overlay-settings-metrics li span").allTextContents()) === JSON.stringify(ordered), "Reopening lost item order");
  const beforeAppearanceSave = backendOverlayRequests.length;
  await font.fill("22");
  await save.click();
  await page.waitForFunction(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1"))?.overlays["frontend-harness.software"]?.fontSizePx === 22);
  assert(backendOverlayRequests.length === beforeAppearanceSave, "Appearance-only save requested backend module state");
  await patch({ overlaySaveDelayMs: 0, overlaySaveFailure: true });
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
  // 界面外的程序改了启动模块登记（同一个后端端点）。
  const externalModules = async moduleIds => {
    const result = await fetch(`${baseUrl}${modulePath}`, { method: "PUT", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ softwareId: "frontend-harness.software", moduleIds }) });
    assert(result.status === 200, "External module-selection fixture failed");
  };
  await externalModules([]);
  await page.getByRole("dialog", { name: "示例软件", exact: true }).getByRole("button", { name: "关闭", exact: true }).first().click();
  await open();
  assert(!await editor.getByRole("checkbox").isChecked(), "Stale local enabled preference hid absent injection modules");
  await editor.getByRole("checkbox").check();
  const reenabled = page.waitForResponse(response => response.request().method() === "PUT" && response.url().endsWith(modulePath));
  await save.click();
  await reenabled;
  await page.waitForFunction(() => !document.querySelector('.overlay-settings input[type="number"]')?.disabled);
  assert(await editor.getByRole("checkbox").isChecked(), "Enabling once did not restore the missing module selection");
  // 后端登记与这里的选择不一致时必须可见，并允许保存来对齐（界面外改过、只登记了一部分）。
  const closeDetail = () => page.getByRole("dialog", { name: "示例软件", exact: true }).getByRole("button", { name: "关闭", exact: true }).first().click();
  const lastModuleSelection = () => moduleWrites.at(-1).moduleIds;
  const saveAndSettle = async () => {
    const done = page.waitForResponse(response => response.request().method() === "PUT" && response.url().endsWith(modulePath));
    await save.click();
    await done;
    await page.waitForFunction(() => !document.querySelector('.overlay-settings input[type="number"]')?.disabled);
  };
  await externalModules(["performance-overlay-preload"]);
  await closeDetail();
  await open();
  await editor.getByRole("status").filter({ hasText: copy.performanceOverlay.injectedModulesPartial }).waitFor();
  assert(!await editor.getByRole("checkbox").isChecked(), "Partially registered injection was shown as enabled");
  assert(await save.isEnabled(), "A module mismatch cannot be saved to align the backend");
  await saveAndSettle();
  assert(JSON.stringify(lastModuleSelection()) === "[]", "Saving a partial registration did not align it to the disabled choice");
  assert(await editor.getByRole("status").filter({ hasText: copy.performanceOverlay.injectedModulesPartial }).count() === 0, "Partial notice stayed after alignment");
  await editor.getByRole("combobox", { name: copy.performanceOverlay.mode, exact: true }).click();
  await page.getByRole("option", { name: copy.performanceOverlay.external, exact: true }).click();
  await save.click();
  await page.waitForFunction(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1"))?.overlays["frontend-harness.software"]?.mode === "external");
  await externalModules(["performance-overlay-preload", "performance-overlay-vulkan"]);
  await closeDetail();
  await open();
  await editor.getByRole("status").filter({ hasText: copy.performanceOverlay.externalModeModulesPresent }).waitFor();
  assert(await save.isEnabled(), "External mode with registered modules cannot be saved to align the backend");
  await saveAndSettle();
  assert(JSON.stringify(lastModuleSelection()) === "[]", "External mode left the injection modules registered");
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
  assert(JSON.stringify(lastModuleSelection()) === JSON.stringify(["performance-overlay-preload", "performance-overlay-vulkan"]), "Modules were not written before the failed preference save");
  assert(await save.isEnabled(), "A failed preference save cannot be retried");
  await save.click();
  await editor.getByRole("alert").waitFor({ state: "hidden" });
  await page.waitForFunction(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1"))?.overlays["frontend-harness.software"]?.mode === "injected");
  // The picker reads /api/targeted-recordings/items/catalog; the old overlay metric catalog route is gone.
  assert(backendOverlayRequests.every(({ path }) => path.startsWith("/api/startup-modules/")), "Renderer requested a removed backend overlay settings route");
  const stored = await page.evaluate(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1")).overlays["frontend-harness.software"]);
  assert(Array.isArray(stored.items) && stored.items.length > 0 && !("metrics" in stored) && !("metricSources" in stored), "Overlay preferences are not stored as items");
  assert(stored.items.every(item => ["frames", "metric", "software"].includes(item.kind) && typeof item.source === "string"
    && ["low", "mid", "high"].includes(item.syncPrecision) && (item.kind !== "frames" || item.intervalMs === null)), "A stored overlay item lost its kind, source, interval or precision");
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
  return { numberBoxesKeepPartialInput: true, persistence: true, itemOrdering: true, pendingControlsLocked: true, appearanceSaveStaysLocal: true, genericModulePayload: true, actualInjectedModuleJoin: true, failureRetainsDraft: true, retry: true, moduleMismatchVisible: true, preferenceFailureAfterModulesVisible: true };
}
