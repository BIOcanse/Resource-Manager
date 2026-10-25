import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";

/**
 * 设置开关的滑块走交互协议的运行时（ui/primitives/Switch.tsx），真实鼠标事件，简体中文：
 * 点一下切换一次，滑块随后到另一端；按住滑块拖到另一端松手切换一次，拖完的那次 click 不会再切换回来；
 * 拖一点再拖回原处松手不改值；空格键照常切换。用“自动更新”开关（只改设置草稿，不保存）。
 */
export default async function switchInteractionJourney(page, baseUrl) {
  const app = await loadAppCopy("zh-CN");
  const settings = await loadSettingsText("zh-CN");

  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
  await page.getByRole("button", { name: settings.sections.updates, exact: true }).click();
  const box = page.getByRole("checkbox", { name: settings.updates.autoUpdateTitle, exact: true });
  await box.waitFor();
  const root = box.locator("xpath=..");
  const knob = root.locator(".settings-switch-knob");
  const knobX = () => knob.evaluate((element) => { const value = getComputedStyle(element).translate; return value === "none" ? 0 : Number.parseFloat(value); });
  /** 滑块停在开（20 px）或关（0 px）这一端。 */
  const waitKnob = async (on, step) => {
    try {
      await page.waitForFunction(([element, target]) => {
        const value = getComputedStyle(element).translate;
        const x = value === "none" ? 0 : Number.parseFloat(value);
        return Math.abs(x - target) <= 0.5;
      }, [await knob.elementHandle(), on ? 20 : 0], { timeout: 3000 });
    } catch {
      throw new Error(`${step}: knob at ${await knobX()} px, expected ${on ? 20 : 0} px`);
    }
  };
  const waitChecked = async (on, step) => {
    try {
      await page.waitForFunction(([element, target]) => element.checked === target, [await box.elementHandle(), on], { timeout: 3000 });
    } catch {
      throw new Error(`${step}: checked ${await box.isChecked()}, expected ${on}`);
    }
  };
  const settle = () => page.waitForTimeout(400);
  const knobCentre = async () => {
    const rect = await knob.boundingBox();
    return { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 };
  };
  const drag = async (dx) => {
    const from = await knobCentre();
    await page.mouse.move(from.x, from.y);
    await page.mouse.down();
    for (let i = 1; i <= 8; i += 1) await page.mouse.move(from.x + dx * i / 8, from.y);
    await page.mouse.up();
  };

  const initial = await box.isChecked();
  await waitKnob(initial, "Initial knob");

  // 点一下：切换一次，滑块到另一端。
  await root.click();
  await waitChecked(!initial, "Click");
  await waitKnob(!initial, "Knob after click");
  await settle();
  if (await box.isChecked() === initial) throw new Error("A click toggled the switch twice");

  // 拖到另一端：切换一次，拖完的 click 不再切换回来。
  await drag(initial ? 30 : -30);
  await waitChecked(initial, "Drag to the other end");
  await waitKnob(initial, "Knob after the drag");
  await settle();
  if (await box.isChecked() !== initial) throw new Error("The click after the drag toggled the switch back");

  // 拖一点再拖回原处：不改值，滑块回到原来那一端。
  const from = await knobCentre();
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  for (const dx of [3, 6, 8, 4, 1, 0]) await page.mouse.move(from.x + (initial ? -dx : dx), from.y);
  await page.mouse.up();
  await settle();
  await waitChecked(initial, "Drag and return");
  await waitKnob(initial, "Knob after drag and return");


  // 空格键照常切换。
  await box.focus();
  await page.keyboard.press("Space");
  await waitChecked(!initial, "Space");
  await waitKnob(!initial, "Knob after Space");
  await page.keyboard.press("Space");
  await waitChecked(initial, "Space back");
  return { initial };
}
