import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { pointOverlayPresentation } from "../../src/preferences/frontendPreferences.ts";

export default async function overlaySettingsJourney(page, baseUrl) {
  const copy = await loadAppCopy("zh-CN");
  const assert = (value, message) => { if (!value) throw new Error(message); };
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  const patch = async value => {
    const response = await fetch(`${baseUrl}/__test/scenario`, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(value)
    });
    assert(response.ok, "Scenario update failed");
  };
  await page.setViewportSize({ width: 1200, height: 950 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "组件与软件", exact: true }).click();
  await page.getByRole("navigation", { name: "组件和软件分类" })
    .getByRole("button", { name: /一般应用/ }).click();
  const card = page.locator(".software-item").filter({ hasText: "示例软件" }).first();
  const open = async () => {
    await card.getByRole("button", { name: "软件设置" }).click();
    await page.getByRole("tab", { name: copy.performanceOverlay.tab, exact: true }).click();
    await page.locator(".overlay-settings-fields").waitFor({ state: "visible" });
  };
  await open();
  const editor = page.locator(".overlay-settings");
  const save = editor.getByRole("button", { name: copy.softwareDetail.save, exact: true });
  const font = editor.getByRole("spinbutton", { name: copy.performanceOverlay.fontSizePx });
  await editor.getByRole("checkbox").check();
  await font.fill("21");
  // Exercise the shared picker and ordering before persisting the complete object.
  await editor.getByRole("button", { name: copy.performanceOverlay.chooseMetrics }).click();
  await page.getByRole("button", { name: copy.metricPicker.confirm, exact: true }).click();
  const before = await editor.locator(".overlay-settings-metrics li span").allTextContents();
  await editor.getByRole("button", { name: copy.performanceOverlay.moveDown, exact: true }).first().click();
  const ordered = await editor.locator(".overlay-settings-metrics li span").allTextContents();
  assert(ordered[1] === before[0], "Metric order did not change");
  await patch({ overlaySaveDelayMs: 1500 });
  const sent = page.waitForRequest(request => request.method() === "PUT" && request.url().includes("/api/performance-overlay/software/"));
  const completed = page.waitForResponse(response => response.request().method() === "PUT"
    && response.url().includes("/api/performance-overlay/software/"));
  await save.click();
  const request = await sent;
  assert(JSON.stringify(Object.keys(request.postDataJSON()).sort()) === JSON.stringify(["enabled", "mode", "softwareId"]), "Presentation entered the backend overlay request");
  for (const control of await editor.locator("input, button").all()) {
    assert(await control.isDisabled(), "An overlay control remained editable during save");
  }
  // Even a dispatched event cannot change the pending draft.
  await font.evaluate(element => { element.value = "36"; element.dispatchEvent(new Event("input", { bubbles: true })); });
  assert((await completed).ok(), "Delayed save failed");
  await page.waitForFunction(() => !document.querySelector('.overlay-settings input[type="number"]')?.disabled);
  assert(await font.inputValue() === "21", "Pending edit guard lost the submitted value");
  assert(await save.isDisabled(), "Successful save left the editor dirty");
  await page.getByRole("dialog", { name: "示例软件", exact: true }).getByRole("button", { name: "关闭", exact: true }).first().click();
  await open();
  assert(await font.inputValue() === "21", "Reopening lost saved font size");
  assert(await editor.getByRole("checkbox").isChecked(), "Reopening lost enabled state");
  assert(JSON.stringify(await editor.locator(".overlay-settings-metrics li span").allTextContents()) === JSON.stringify(ordered), "Reopening lost metric order");
  await patch({ overlaySaveDelayMs: 0, overlaySaveFailure: true });
  await font.fill("27");
  await editor.getByRole("checkbox").uncheck();
  await save.click();
  await editor.getByRole("alert").waitFor({ state: "visible" });
  assert(await font.inputValue() === "27", "Failed save lost the draft");
  assert(await save.isEnabled(), "Failed save cannot be retried");
  const persisted = await page.evaluate(async url => (await fetch(url)).json(), request.url());
  assert(!("fontSizePx" in persisted) && persisted.enabled === true, "Backend overlay retained presentation or failed operation changed settings");
  const cached = await page.evaluate(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1")));
  assert(Object.values(cached.overlays).some(value => value.fontSizePx === 21), "Failed operation changed the frontend cache");
  await patch({ overlaySaveFailure: false });
  await save.click();
  await editor.getByRole("alert").waitFor({ state: "hidden" });
  await page.waitForFunction(() => !document.querySelector('.overlay-settings input[type="number"]')?.disabled);
  assert(await save.isDisabled(), "Successful retry left the editor dirty");
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "组件与软件", exact: true }).click();
  await page.getByRole("navigation", { name: "组件和软件分类" }).getByRole("button", { name: /一般应用/ }).click();
  await open();
  assert(await font.inputValue() === "27", "Frontend overlay cache did not survive page restart");
  await editor.getByRole("button", { name: (await loadSettingsText("zh-CN")).actions.restoreDefaults, exact: true }).click();
  await page.waitForFunction(fontPx => document.querySelector('.overlay-settings input[type="number"]')?.value === String(fontPx), pointOverlayPresentation().fontSizePx);
  assert(errors.length === 0, errors.join("\n"));
  return { persistence: true, metricOrdering: true, pendingControlsLocked: true, failureRetainsDraft: true, retry: true };
}
