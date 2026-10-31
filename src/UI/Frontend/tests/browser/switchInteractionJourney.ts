import type { ElementHandle } from "playwright-core";
import type { Page } from "playwright-core";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { waitForNextPaint, waitForStableRendering } from "./journeyWaits.ts";

/**
 * 设置开关的滑块走交互协议的运行时（ui/primitives/Switch.tsx），真实鼠标事件，简体中文：
 * 点一下切换一次，滑块随后到另一端；按住滑块拖到另一端松手切换一次，拖完的那次 click 不会再切换回来；
 * 拖一点再拖回原处松手不改值；拖到一半时轨道颜色已经在关、开两色之间（颜色按开度跟着滑块走，不等松手）；
 * 空格键照常切换。用“间隔检查”开关（只改设置草稿，不保存）。
 */
export default async function switchInteractionJourney(page: Page, baseUrl: string) {
  const app = await loadAppCopy("zh-CN");
  const settings = await loadSettingsText("zh-CN");

  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
  await page.getByRole("button", { name: settings.sections.updates, exact: true }).click();
  const box = page.getByRole("checkbox", { name: settings.updates.periodicTitle, exact: true });
  await box.waitFor();
  const root = box.locator("xpath=..");
  const knob = root.locator(".settings-switch-knob");
  const knobX = () => knob.evaluate((element) => { const value = getComputedStyle(element).translate; return value === "none" ? 0 : Number.parseFloat(value); });
  /** 滑块停在开（20 px）或关（0 px）这一端。 */
  const waitKnob = async (on: boolean, step: string) => {
    try {
      await page.waitForFunction(([element, target]) => {
        const value = getComputedStyle(element).translate;
        const x = value === "none" ? 0 : Number.parseFloat(value);
        return Math.abs(x - target) <= 0.5;
      }, [await knob.elementHandle() as ElementHandle<HTMLElement>, on ? 20 : 0] as const, { timeout: 3000 });
    } catch {
      throw new Error(`${step}: knob at ${await knobX()} px, expected ${on ? 20 : 0} px`);
    }
  };
  const waitChecked = async (on: boolean, step: string) => {
    try {
      await page.waitForFunction(([element, target]) => element.checked === target, [await box.elementHandle() as ElementHandle<HTMLInputElement>, on] as const, { timeout: 3000 });
    } catch {
      throw new Error(`${step}: checked ${await box.isChecked()}, expected ${on}`);
    }
  };
  const settleRendering = () => waitForStableRendering(page, [".settings-switch-knob", ".settings-switch-track"]);
  const knobCentre = async () => {
    const rect = (await knob.boundingBox())!;
    return { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 };
  };
  const drag = async (dx: number) => {
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
  // Keep the full 400 ms to observe that this click never causes a second toggle.
  await page.waitForTimeout(400);
  if (await box.isChecked() === initial) throw new Error("A click toggled the switch twice");

  // 拖到另一端：切换一次，拖完的 click 不再切换回来。
  await drag(initial ? 30 : -30);
  await waitChecked(initial, "Drag to the other end");
  await waitKnob(initial, "Knob after the drag");
  // Keep the full 400 ms after the drag's synthesized click to observe no toggle back.
  await page.waitForTimeout(400);
  if (await box.isChecked() !== initial) throw new Error("The click after the drag toggled the switch back");

  // 拖一点再拖回原处：不改值，滑块回到原来那一端。
  const from = await knobCentre();
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  for (const dx of [3, 6, 8, 4, 1, 0]) await page.mouse.move(from.x + (initial ? -dx : dx), from.y);
  await page.mouse.up();
  // Keep 400 ms after release: returning to the starting position must leave the
  // checked value unchanged even after deferred click and motion callbacks.
  await page.waitForTimeout(400);
  await waitChecked(initial, "Drag and return");
  await waitKnob(initial, "Knob after drag and return");


  // 拖到一半：轨道颜色的每个分量都落在两端颜色之间，离两端都有距离；复选框还没变。拖回原处松手不改值。
  const track = root.locator(".settings-switch-track");
  const trackColour = () => track.evaluate((element) => getComputedStyle(element).backgroundColor);
  const restColour = await trackColour();
  await box.focus();
  await page.keyboard.press("Space");
  await waitChecked(!initial, "Space before the colour check");
  await waitKnob(!initial, "Knob before the colour check");
  await settleRendering();
  const otherColour = await trackColour();
  await page.keyboard.press("Space");
  await waitChecked(initial, "Space back before the colour check");
  await waitKnob(initial, "Knob back before the colour check");
  await settleRendering();
  const half = await knobCentre();
  await page.mouse.move(half.x, half.y);
  await page.mouse.down();
  for (let i = 1; i <= 8; i += 1) await page.mouse.move(half.x + (initial ? -10 : 10) * i / 8, half.y);
  await waitForNextPaint(page);
  const middle = await track.evaluate((element) => ({
    colour: getComputedStyle(element).backgroundColor,
    openness: Number.parseFloat(element.style.getPropertyValue("--switch-openness"))
  }));
  const middleChecked = await box.isChecked();
  await page.mouse.move(half.x, half.y);
  await page.mouse.up();
  // Keep 400 ms after returning from the colour probe: release/click must not
  // change the draft after the unchanged checked state would already pass.
  await page.waitForTimeout(400);
  await waitChecked(initial, "Release after the colour check");
  const numbers = (colour: string) => (colour.match(/-?\d*\.?\d+(e-?\d+)?/g) ?? []).map(Number);
  const [off, on] = initial ? [otherColour, restColour] : [restColour, otherColour];
  const a = numbers(off);
  const b = numbers(on);
  const m = numbers(middle.colour);
  const spread = Math.max(...a.map((value, i) => Math.abs(value - b[i])));
  const between = a.length === b.length && a.length === m.length && spread > 0
    && m.every((value, i) => value >= Math.min(a[i], b[i]) - 1e-3 && value <= Math.max(a[i], b[i]) + 1e-3)
    && Math.max(...m.map((value, i) => Math.abs(value - a[i]))) > spread * 0.25
    && Math.max(...m.map((value, i) => Math.abs(value - b[i]))) > spread * 0.25;
  if (middleChecked !== initial || !(middle.openness > 0.3 && middle.openness < 0.7) || !between) {
    throw new Error(`Mid-drag track colour does not follow the knob: ${JSON.stringify({ off, on, middle, middleChecked, initial })}`);
  }

  // 空格键照常切换。
  await box.focus();
  await page.keyboard.press("Space");
  await waitChecked(!initial, "Space");
  await waitKnob(!initial, "Knob after Space");
  await page.keyboard.press("Space");
  await waitChecked(initial, "Space back");
  return { initial, off, on, middle };
}
