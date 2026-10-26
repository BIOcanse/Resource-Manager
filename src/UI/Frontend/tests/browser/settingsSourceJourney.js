import assert from "node:assert/strict";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";

export default async function settingsSourceJourney(page, baseUrl) {
  const errors = [];
  const patches = [];
  const text = await loadSettingsText("zh-CN");
  page.on("pageerror", error => errors.push(String(error)));
  page.on("request", request => {
    if (request.method() === "PATCH" && new URL(request.url()).pathname === "/api/settings/app") patches.push(request.postDataJSON());
  });
  const scenario = async value => {
    assert((await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(value) })).ok);
  };
  const cache = () => page.evaluate(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1")));
  const open = async section => {
    await page.getByRole("button", { name: "设置", exact: true }).click();
    await page.getByRole("button", { name: section, exact: true }).click();
  };
  const save = () => page.getByRole("button", { name: "保存", exact: true }).click();
  const theme = name => page.getByRole("radio", { name, exact: true });
  await page.setViewportSize({ width: 1100, height: 850 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await open("外观");
  assert(await theme("跟随系统").isChecked());
  assert.equal(await cache(), null, "Missing cache should use points without writing a cache");
  await theme("深色").click();
  await theme("异色区分").click();
  assert.equal(await page.locator("html").getAttribute("data-theme"), "system", "Draft applied before save");
  await save();
  await page.waitForFunction(() => document.documentElement.dataset.theme === "dark" && document.body.dataset.barColor === "distinct");
  assert.equal(patches.length, 0, "Presentation was sent to the backend");
  assert.equal((await cache()).appearance.theme, "dark");
  await scenario({ settingsRevision: "frontend-harness-r7" });
  await page.reload({ waitUntil: "domcontentloaded" });
  await open("外观");
  assert(await theme("深色").isChecked(), "Backend refresh or restart overwrote the cache");
  await page.getByRole("radiogroup", { name: "资源条动画帧率" }).getByRole("radio", { name: "自定义", exact: true }).click();
  const fps = page.getByRole("textbox", { name: "资源条动画帧率 帧率 (FPS)" });
  await fps.fill("90"); await fps.press("Enter");
  await page.getByRole("radiogroup", { name: "动画风格" }).getByRole("radio", { name: "无动画", exact: true }).click();
  await page.getByRole("radiogroup", { name: "文字平滑" }).getByRole("radio", { name: "灰度平滑", exact: true }).click();
  await save();
  await page.waitForFunction(() => document.body.dataset.resourceBarFrameRate === "90");
  assert.equal((await cache()).appearance.animationStyle, "none");
  assert.equal((await cache()).appearance.fontSmoothing, "grayscale");
  await page.getByRole("button", { name: "性能", exact: true }).click();
  const cadence = page.getByRole("radiogroup", { name: "资源表", exact: true });
  await cadence.getByRole("radio", { name: "自定义", exact: true }).click();
  const interval = page.getByRole("textbox", { name: "资源表 自定义 (ms)", exact: true });
  await interval.fill("2300"); await interval.press("Enter");
  await save();
  await page.waitForFunction(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1"))?.refresh.resourceTable.customValue === 2300);
  assert.equal(patches.length, 0);
  await page.reload({ waitUntil: "domcontentloaded" });
  await open("性能");
  assert.equal(await interval.inputValue(), "2300");
  // Operational settings retain backend revision/conflict semantics.
  await page.getByRole("button", { name: "系统集成", exact: true }).click();
  const autoStart = page.getByRole("checkbox", { name: text.systemIntegration.autoStartTitle, exact: true });
  await autoStart.check();
  await scenario({ settingsConflict: true });
  const conflict = page.waitForResponse(response => response.status() === 409 && new URL(response.url()).pathname === "/api/settings/app");
  await save(); await conflict;
  await page.getByRole("alert").filter({ hasText: "设置已在其他位置更新，请重新加载" }).waitFor({ state: "visible" });
  assert(await autoStart.isChecked(), "Backend conflict discarded the draft");
  await scenario({ settingsConflict: false });
  const reloaded = page.waitForResponse(response => response.request().method() === "GET" && new URL(response.url()).pathname === "/api/settings/app");
  await page.getByRole("button", { name: "重新加载", exact: true }).click();
  await reloaded;
  await page.waitForFunction(label => !Array.from(document.querySelectorAll('input[type="checkbox"]')).find(input => input.getAttribute('aria-label') === label)?.checked, text.systemIntegration.autoStartTitle);
  await autoStart.check();
  const operation = page.waitForResponse(response => response.ok() && response.request().method() === "PATCH" && new URL(response.url()).pathname === "/api/settings/app");
  await save(); await operation;
  // A durable save can still be pending runtime delivery or capability constrained.
  const runtimeOutcomes = [];
  for (const disposition of ["savedNotApplied", "committedWithDeliveryFailures", "committedWithCapabilityConstraints"]) {
    await scenario({ settingsRuntimeDisposition: disposition });
    const checked = !await autoStart.isChecked();
    await autoStart.setChecked(checked);
    const saved = page.waitForResponse(response => response.request().method() === "PATCH" && new URL(response.url()).pathname === "/api/settings/app");
    await save();
    const response = await saved;
    assert.equal(response.status(), disposition === "committedWithCapabilityConstraints" ? 200 : 202);
    assert.equal((await response.json()).runtimeApplicationDisposition, disposition);
    const message = disposition === "committedWithCapabilityConstraints" ? text.saveState.constrained : text.saveState.partial;
    await page.locator(".settings-save-state").filter({ hasText: message }).waitFor();
    const stored = await (await fetch(baseUrl + "/api/settings/app")).json();
    assert.equal(stored.settings.systemIntegration.autoStartEnabled, checked);
    assert.equal(stored.runtimeApplicationDisposition, disposition);
    await page.reload({ waitUntil: "domcontentloaded" });
    await open("系统集成");
    await page.locator(".settings-save-state").filter({ hasText: message }).waitFor();
    assert.equal(await autoStart.isChecked(), checked);
    if (disposition !== "committedWithCapabilityConstraints") {
      await scenario({ settingsRuntimeDisposition: "committedAndApplied" });
      const reapplied = page.waitForResponse(response => response.request().method() === "POST" && new URL(response.url()).pathname === "/api/settings/app/reapply");
      await page.getByRole("button", { name: text.actions.reapply, exact: true }).click();
      assert.equal((await reapplied).status(), 200);
      await page.locator(".settings-save-state").filter({ hasText: text.saveState.saved }).waitFor();
      assert.equal((await (await fetch(baseUrl + "/api/settings/app")).json()).revision, stored.revision, "Reapply must not create a new settings revision");
    }
    runtimeOutcomes.push(disposition);
  }
  await scenario({ settingsRuntimeDisposition: "committedAndApplied" });
  assert(patches.every(patch => !patch.changes.appearance && !patch.changes.performance?.resourceTableRefreshIntervalMs));
  await page.getByRole("button", { name: "外观", exact: true }).click();
  await page.getByRole("button", { name: text.appearance.resetCache, exact: true }).click();
  await page.waitForFunction(() => localStorage.getItem("resource-manager:frontend-preferences:v1") === null);
  assert(await theme("跟随系统").isChecked(), "Reset did not restore point values");
  await page.reload({ waitUntil: "domcontentloaded" });
  await open("外观");
  assert(await theme("跟随系统").isChecked(), "Cache loss did not restore point values");
  assert.deepEqual(errors, []);
  return { missingCacheUsesPoints: true, presentationNeverSentToBackend: true, restart: true, reset: true, backendConflictPreservesDraft: true, runtimeOutcomes };
}
