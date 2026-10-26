import assert from "node:assert/strict";
import { AUTO_ANIMATION_RECENT_INPUT_MS, AUTO_ANIMATION_OPTIMIZE_STYLE } from "../../src/app/selfScheduling/createAnimationStyleSelection.ts";
import { seedFrontendPreferences, customRefresh } from "./frontendPreferencesFixture.js";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";

/**
 * 按自调度自动关闭动画（docs/animation_style_2026-10-26.md「设置」）：开着时，回到前台、近期没有操作的那一刻按自调度结果在选中的风格和无动画之间选；
 * 前台期间不改；无动画、超高性能不受它影响；关掉后直接用选中的风格；系统的减少动态效果设置不读。
 */
export default async function automaticAnimationJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  await seedFrontendPreferences(page, value => {
    value.appearance.animationStyle = "smooth";
    value.appearance.autoDisableAnimations = true;
    value.refresh.monitor = customRefresh(100);
  });
  // Start visible but unfocused so the backend snapshot arrives before first foreground.
  await page.addInitScript(() => { Document.prototype.hasFocus = () => false; });
  await patchGrade("optimize");
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await observedGrade("optimize");
  await expectTier("smooth");
  await focus(true);
  await expectTier(AUTO_ANIMATION_OPTIMIZE_STYLE);
  await page.evaluate(() => {
    window.animationTierChanges = 0;
    new MutationObserver(records => { window.animationTierChanges += records.length; })
      .observe(document.body, { attributes: true, attributeFilter: ["data-animations"] });
  });

  for (const grade of ["normal", "optimize", "normal", "optimize", "normal"]) {
    await patchGrade(grade);
    await observedGrade(grade);
    await expectTier(AUTO_ANIMATION_OPTIMIZE_STYLE);
  }
  const foregroundFlipTierChanges = await page.evaluate(() => window.animationTierChanges);
  assert.equal(foregroundFlipTierChanges, 0,
    "Repeated foreground grade flips must cause zero tier changes");
  await focus(false);
  await patchGrade("optimize"); await observedGrade("optimize");
  await patchGrade("normal"); await observedGrade("normal");
  await expectTier(AUTO_ANIMATION_OPTIMIZE_STYLE);
  await focus(true);
  await expectTier("smooth");

  for (const kind of ["pointer", "key", "wheel"]) {
    await page.evaluate(kind => {
      const event = kind === "pointer"
        ? new PointerEvent("pointermove", { isPrimary: true, pointerId: 1, clientX: 1, clientY: 1, bubbles: true })
        : kind === "key" ? new KeyboardEvent("keydown", { key: "Shift", code: "ShiftLeft", bubbles: true })
        : new WheelEvent("wheel", { deltaY: 1, bubbles: true });
      document.body.dispatchEvent(event);
    }, kind);
    await focus(false);
    await patchGrade("optimize"); await observedGrade("optimize");
    await focus(true);
    await expectTier("smooth");
  }
  await page.waitForTimeout(AUTO_ANIMATION_RECENT_INPUT_MS + 20);
  await expectTier("smooth"); // Expiry while foreground does not reselect.
  await focus(false); await focus(true);
  await expectTier(AUTO_ANIMATION_OPTIMIZE_STYLE);

  await visibility(false);
  await focus(false); await focus(true);
  await expectTier(AUTO_ANIMATION_OPTIMIZE_STYLE); // Hidden + focused is background.
  await focus(false);
  await patchGrade("normal");
  await visibility(true);
  await observedGrade("normal");
  await expectTier(AUTO_ANIMATION_OPTIMIZE_STYLE); // Visible without focus is background.
  await focus(true);
  await expectTier("smooth");

  for (const reducedMotion of ["reduce", "no-preference"]) {
    await page.emulateMedia({ reducedMotion });
    await expectTier("smooth");
    await focus(false);
    await patchGrade("optimize"); await observedGrade("optimize");
    await focus(true);
    await expectTier(AUTO_ANIMATION_OPTIMIZE_STYLE);
    await focus(false);
    await patchGrade("normal"); await observedGrade("normal");
    await focus(true);
    await expectTier("smooth");
  }

  const text = await loadSettingsText("zh-CN");
  await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "设置", exact: true }).click();
  await page.getByRole("button", { name: text.sections.appearance, exact: true }).click();
  const autoSwitch = page.getByRole("checkbox", { name: text.appearance.autoDisableAnimationsTitle, exact: true });
  const choose = async (style) => {
    const label = text.animationStyleOptions.find(option => option.id === style).label;
    await page.getByRole("radiogroup", { name: text.appearance.animationStyleTitle, exact: true })
      .getByRole("radio", { name: label, exact: true }).click();
    await page.getByRole("button", { name: text.actions.save, exact: true }).click();
  };
  // none、ultra：直接用，开关停用、对它们不起作用。
  for (const style of ["none", "ultra"]) {
    await choose(style);
    await expectTier(style);
    assert.equal(await autoSwitch.isDisabled(), true, `${style}: the auto switch has no effect and is disabled`);
    await focus(false);
    await patchGrade("normal"); await observedGrade("normal");
    await focus(true);
    await expectTier(style);
  }
  // 会动的风格：在前台选了不重新取样（上一次取样是正常），需要节省时回到前台选无动画。
  await choose("desktop");
  await expectTier("desktop");
  assert.equal(await autoSwitch.isDisabled(), false);
  await focus(false);
  await patchGrade("optimize"); await observedGrade("optimize");
  await focus(true);
  await expectTier(AUTO_ANIMATION_OPTIMIZE_STYLE);
  // 关掉开关：直接用选中的风格，需要节省时回到前台也不改。
  await autoSwitch.click();
  await page.getByRole("button", { name: text.actions.save, exact: true }).click();
  await expectTier("desktop");
  await focus(false); await focus(true);
  await expectTier("desktop");
  // 刷新后保持：风格、开关都从前端偏好缓存读回。
  await page.reload({ waitUntil: "domcontentloaded" });
  await focus(true);
  await expectTier("desktop");
  await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "设置", exact: true }).click();
  await page.getByRole("button", { name: text.sections.appearance, exact: true }).click();
  const checked = (await page.getByRole("radiogroup", { name: text.appearance.animationStyleTitle, exact: true })
    .getByRole("radio", { checked: true }).textContent())?.trim();
  assert.equal(checked, text.animationStyleOptions.find(option => option.id === "desktop").label);
  assert.equal(await autoSwitch.isChecked(), false);
  const cached = await page.evaluate(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1") ?? "null")?.appearance);
  assert.deepEqual([cached?.animationStyle, cached?.autoDisableAnimations, "animations" in (cached ?? {})], ["desktop", false, false]);
  assert.deepEqual(errors, []);
  return { foregroundFlips: 5, foregroundFlipTierChanges, inputKinds: ["pointer", "key", "wheel"],
    visibilityAndFocus: true, reducedMotionIgnored: true, manualStyles: ["none", "ultra"], switchOff: "desktop", reloaded: cached };

  async function patchGrade(gpuGrade) {
    const response = await page.request.post(`${baseUrl}/__test/scenario`, { data: { gpuGrade } });
    assert.equal(response.ok(), true);
  }
  async function observedGrade(grade) {
    await page.waitForFunction(grade => document.body.dataset.selfGpuGrade === grade, grade);
  }
  async function expectTier(tier) {
    await page.waitForFunction(tier => document.body.dataset.animations === tier, tier);
    assert.equal(await page.getAttribute("body", "data-animations"), tier);
  }
  async function focus(value) {
    await page.evaluate(value => window.dispatchEvent(new Event(value ? "focus" : "blur")), value);
  }
  async function visibility(value) {
    await page.evaluate(value => {
      Object.defineProperty(document, "visibilityState", { configurable: true, value: value ? "visible" : "hidden" });
      Object.defineProperty(document, "hidden", { configurable: true, value: !value });
      document.dispatchEvent(new Event("visibilitychange"));
    }, value);
  }
}
