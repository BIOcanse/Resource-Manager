import assert from "node:assert/strict";
import { AUTO_ANIMATION_RECENT_INPUT_MS, AUTO_ANIMATION_OPTIMIZE_MODE } from "../../src/app/selfScheduling/createAnimationModeSelection.ts";
import { seedFrontendPreferences, customRefresh } from "./frontendPreferencesFixture.js";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";

export default async function automaticAnimationJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  await seedFrontendPreferences(page, value => {
    value.appearance.animations = "auto";
    value.refresh.monitor = customRefresh(100);
  });
  // Start visible but unfocused so the backend snapshot arrives before first foreground.
  await page.addInitScript(() => { Document.prototype.hasFocus = () => false; });
  await patchGrade("optimize");
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await observedGrade("optimize");
  await expectTier("normal");
  await focus(true);
  await expectTier(AUTO_ANIMATION_OPTIMIZE_MODE);
  await page.evaluate(() => {
    window.animationTierChanges = 0;
    new MutationObserver(records => { window.animationTierChanges += records.length; })
      .observe(document.body, { attributes: true, attributeFilter: ["data-animations"] });
  });

  for (const grade of ["normal", "optimize", "normal", "optimize", "normal"]) {
    await patchGrade(grade);
    await observedGrade(grade);
    await expectTier(AUTO_ANIMATION_OPTIMIZE_MODE);
  }
  const foregroundFlipTierChanges = await page.evaluate(() => window.animationTierChanges);
  assert.equal(foregroundFlipTierChanges, 0,
    "Repeated foreground grade flips must cause zero tier changes");
  await focus(false);
  await patchGrade("optimize"); await observedGrade("optimize");
  await patchGrade("normal"); await observedGrade("normal");
  await expectTier(AUTO_ANIMATION_OPTIMIZE_MODE);
  await focus(true);
  await expectTier("normal");

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
    await expectTier("normal");
  }
  await page.waitForTimeout(AUTO_ANIMATION_RECENT_INPUT_MS + 20);
  await expectTier("normal"); // Expiry while foreground does not reselect.
  await focus(false); await focus(true);
  await expectTier(AUTO_ANIMATION_OPTIMIZE_MODE);

  await visibility(false);
  await focus(false); await focus(true);
  await expectTier(AUTO_ANIMATION_OPTIMIZE_MODE); // Hidden + focused is background.
  await focus(false);
  await patchGrade("normal");
  await visibility(true);
  await observedGrade("normal");
  await expectTier(AUTO_ANIMATION_OPTIMIZE_MODE); // Visible without focus is background.
  await focus(true);
  await expectTier("normal");

  for (const reducedMotion of ["reduce", "no-preference"]) {
    await page.emulateMedia({ reducedMotion });
    await expectTier("normal");
    await focus(false);
    await patchGrade("optimize"); await observedGrade("optimize");
    await focus(true);
    await expectTier(AUTO_ANIMATION_OPTIMIZE_MODE);
    await focus(false);
    await patchGrade("normal"); await observedGrade("normal");
    await focus(true);
    await expectTier("normal");
  }

  const text = await loadSettingsText("zh-CN");
  await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "设置", exact: true }).click();
  await page.getByRole("button", { name: text.sections.appearance, exact: true }).click();
  for (const mode of ["none", "ultra", "normal"]) {
    const label = text.animationOptions.find(option => option.id === mode).label;
    await page.getByRole("radiogroup", { name: text.appearance.animationTitle, exact: true })
      .getByRole("radio", { name: label, exact: true }).click();
    await page.getByRole("button", { name: text.actions.save, exact: true }).click();
    await expectTier(mode);
    await focus(false);
    await patchGrade("optimize"); await observedGrade("optimize");
    await focus(true);
    await expectTier(mode);
  }
  assert.deepEqual(errors, []);
  return { foregroundFlips: 5, foregroundFlipTierChanges, inputKinds: ["pointer", "key", "wheel"],
    visibilityAndFocus: true, reducedMotionIgnored: true, manualTiers: true };

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
