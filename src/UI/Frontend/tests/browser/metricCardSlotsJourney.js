import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { pointPreferences } from "../../src/preferences/frontendPreferences.ts";
import { frontendPreferencesKey } from "./frontendPreferencesFixture.js";

/**
 * 监视卡片的槽位大小（用户 2026-10-25：「最小，中（相当于两个最小）大（相当于三个最小）超大（占满），一个卡片一共六个单位空间。
 * 选择大小后就可以在里面选择监控项」，规则见 src/features/monitor/cardSlots.ts）：
 * 1. 旧布局迁移：有槽位之前的格式（主读数加两行）和同样内容的槽位格式渲染一模一样，主读数是“中”、每行是“最小”；
 * 2. 各档的高度是单位的倍数；大、超大有趋势图，超大写最低、最高；
 * 3. “+”只列放得下的大小，没有剩余单位时没有“+”；改大小只列“剩余 + 自己”放得下的大小，当前大小不可选；
 * 4. 改大小、删除、新增由磁石带过渡：正常档下高度和位置是走过去的，无动画档下下一帧就到终态；
 * 5. 选好大小再选监控项，保存后缓存里是槽位格式。
 */
export default async function metricCardSlotsJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", (error) => errors.push(String(error)));
  const zh = await loadAppCopy("zh-CN");
  const text = zh.dashboard;
  const tableColumns = [{ id: "name", visible: true, width: 260 }, { id: "cpu", visible: true, width: 110 }];
  const cards = [
    { id: "legacy", main: "cpu.usage", small: ["cpu.frequency", "cpu.frequencyPercent"] },
    { id: "slots", slots: [
      { metricId: "cpu.usage", size: "medium" }, { metricId: "cpu.frequency", size: "small" }, { metricId: "cpu.frequencyPercent", size: "small" }
    ] },
    { id: "big", slots: [{ metricId: "cpu.usage", size: "large" }, { metricId: "memory.usage", size: "medium" }] },
    { id: "full", slots: [{ metricId: "cpu.frequencyPercent", size: "full" }] }
  ];
  const seed = (style) => {
    const preferences = pointPreferences();
    preferences.appearance.uiStyle = "standard";
    preferences.appearance.theme = "light";
    preferences.appearance.animationStyle = style;
    preferences.appearance.autoDisableAnimations = false;
    preferences.dashboard = { ...preferences.dashboard, version: 16, cards, resourceBars: [],
      resourceTableColumns: tableColumns, resourceTableProcessColumns: tableColumns };
    return JSON.stringify(preferences);
  };
  const load = async (style) => {
    await page.evaluate(([key, value]) => localStorage.setItem(key, value), [frontendPreferencesKey, seed(style)]);
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.getByRole("navigation").getByRole("button", { name: zh.page.monitor, exact: true }).click();
    await card("legacy").locator(".metric-main-content .metric-value").filter({ hasText: "%" }).waitFor({ timeout: 15_000 });
  };
  const card = (id) => page.locator(`.metric-card[data-card-id="${id}"]`);
  const frames = (id) => card(id).locator(".card-slot:not(.card-slot-add)");
  const geometry = (id) => card(id).evaluate((element) => {
    const origin = element.querySelector(".metric-slots").getBoundingClientRect();
    return [...element.querySelectorAll(".card-slot")].map((frame) => {
      const box = frame.getBoundingClientRect();
      const content = frame.firstElementChild;
      return { top: Math.round(box.top - origin.top), height: Math.round(box.height), content: content.className,
        label: content.querySelector(".metric-label-text")?.textContent ?? null };
    });
  });
  const menuItems = async () => (await page.getByRole("menuitem").all());
  const menuNames = async () => Promise.all((await menuItems()).map(async (item) => ({
    name: (await item.textContent()).trim(), disabled: await item.isDisabled() })));
  const option = (size) => `${text.slotSize[size]} · ${text.slotSizeHint[size]}`;
  // 菜单打开后焦点要等一帧才进去：对菜单本身按 Esc。
  const closeMenu = async () => {
    await page.getByRole("menu").press("Escape");
    await page.getByRole("menu").waitFor({ state: "detached" });
  };

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await load("desktop");

  // 1. 旧布局迁移前后渲染一致。
  const legacy = await geometry("legacy");
  assert.deepEqual(await geometry("slots"), legacy, "a legacy card renders exactly like the same slots");
  assert.deepEqual(legacy.map((slot) => slot.content), ["metric-main-content metric-size-medium", "metric-small-row", "metric-small-row"]);
  // 2. 高度是单位的倍数：中 = 2 个最小再加一个间距。
  const gap = legacy[1].top - legacy[0].height;
  assert.ok(gap >= 2 && gap <= 8, `slot gap ${gap}`);
  assert.equal(legacy[0].height, 2 * legacy[1].height + gap, "medium is two small units");
  const [large, medium] = await geometry("big");
  assert.equal(large.height, 3 * legacy[1].height + 2 * gap, "large is three small units");
  assert.equal(medium.top, large.height + gap);
  const [full] = await geometry("full");
  assert.equal(full.height, 6 * legacy[1].height + 5 * gap, "full fills the six units");
  const cardHeights = await page.locator(".metric-card").evaluateAll((elements) => elements.map((element) => Math.round(element.getBoundingClientRect().height)));
  assert.equal(new Set(cardHeights).size, 1, `every card holds six units: ${cardHeights}`);
  // 趋势：大的折线随读数长出来，超大写最低、最高。
  // mock 的读数不变，线是水平的（外框高度为 0，不算“可见”）：按路径里有连线段判断。
  await card("big").locator(".metric-trend-line[d*='L']").waitFor({ state: "attached", timeout: 15_000 });
  assert.equal(await card("big").locator(".metric-trend-range").count(), 0, "large draws the line only");
  await card("full").locator(".metric-trend-line[d*='L']").waitFor({ state: "attached", timeout: 15_000 });
  const range = card("full").locator(".metric-trend-range");
  assert.deepEqual((await range.locator("dt").allTextContents()).map((value) => value.trim()), [text.trendLow, text.trendHigh]);
  for (const value of await range.locator("dd").allTextContents()) assert.match(value, /%/);
  assert.match(await card("full").locator(".metric-trend-chart").getAttribute("aria-label"), /最近 \d+ 次读数的趋势/);
  assert.equal(await card("slots").locator(".metric-trend").count(), 0, "small and medium have no trend");
  // 数值不被趋势挤压裁掉：中、大、超大的数值盒子装得下它的字。
  const clipped = await page.locator(".dashboard .metric-main-content .metric-value").evaluateAll((values) => values
    // 行高是字号的 1.1 倍；被压缩时盒子比字矮（单位小一号、按基线排，scrollHeight 本来就略高于盒子，不能拿它判断）。
    .filter((value) => value.getBoundingClientRect().height < parseFloat(getComputedStyle(value).fontSize) * 1.05)
    .map((value) => `${value.closest(".metric-card").dataset.cardId}: ${value.textContent}`));
  assert.deepEqual(clipped, [], "large and full values keep their full height next to the trend");

  // 3. 编辑：“+”只列放得下的大小，没有剩余时没有“+”。
  await page.getByRole("button", { name: zh.monitorPage.editLayoutLabel, exact: true }).click();
  const add = (ordinal) => page.getByRole("button", { name: text.addSlot(ordinal), exact: true });
  await add(2).click();
  assert.deepEqual(await menuNames(), ["small", "medium"].map((size) => ({ name: option(size), disabled: false })),
    "medium and two small rows leave two units: large and full are not offered");
  await closeMenu();
  await add(3).click();
  assert.deepEqual(await menuNames(), [{ name: option("small"), disabled: false }], "one unit left: only small");
  await closeMenu();
  assert.equal(await add(4).count(), 0, "a full card has no +");
  assert.equal(await card("full").locator(".slot-add").count(), 0);
  // 剩余的那块“+”占的就是剩余的单位。
  const addFrame = await card("slots").locator(".card-slot-add").evaluate((frame) => Math.round(frame.getBoundingClientRect().height));
  assert.equal(addFrame, 2 * legacy[1].height + gap);

  // 改大小：只列“剩余 + 自己”放得下的大小，当前大小不可选。
  const resizeFrequency = card("slots").locator(".slot-resize").nth(1);
  assert.match(await resizeFrequency.getAttribute("aria-label"), new RegExp(`^${escapeRegExp(text.resizeSlot(text.slotLabel(2, 2), ""))}`));
  await resizeFrequency.click();
  assert.deepEqual(await menuNames(), [
    { name: option("small"), disabled: true }, { name: option("medium"), disabled: false }, { name: option("large"), disabled: false }
  ], "one own unit plus two left: up to large, the current size is not offered as a change");
  assert.equal(await page.getByRole("menuitem").first().getAttribute("title"), text.currentSize);
  // 4. 正常档：选大后这一槽的高度和下一槽的位置是走过去的。
  const timeline = await sampleAfter(page, page.getByRole("menuitem", { name: option("large"), exact: true }), "slots");
  const finalHeight = 3 * legacy[1].height + 2 * gap;
  const heights = timeline.map((sample) => sample[1].height);
  assert.ok(heights.some((height) => height > legacy[1].height + 1 && height < finalHeight - 1),
    `normal animation passes through intermediate heights: ${heights.join(",")}`);
  assert.equal(Math.round(heights.at(-1)), finalHeight);
  assert.equal(Math.round(timeline.at(-1)[2].top), legacy[0].height + gap + finalHeight + gap, "the next slot moved down");
  assert.equal(await add(2).count(), 0, "the card is now full: no +");
  // 大了就有趋势。
  await frames("slots").nth(1).locator(".metric-trend").waitFor();
  // 删除最后一槽：剩余一个单位，“+”从高度 0 长出来。
  const removeLast = frames("slots").nth(2).locator(".slot-delete");
  const grow = await sampleAfter(page, removeLast, "slots", ".card-slot-add");
  assert.ok(grow[0].height < legacy[1].height - 1, `the + grows in from zero: ${grow.map((sample) => sample.height).join(",")}`);
  assert.equal(Math.round(grow.at(-1).height), legacy[1].height);

  // 5. 选好大小再选监控项：“+”→ 最小 → 选监控项弹窗，确认后加在末尾，保存后是槽位格式。
  await add(2).click();
  await page.getByRole("menuitem", { name: option("small"), exact: true }).click();
  const picker = page.locator("#metricModal");
  await picker.locator('[data-metric-id="cpu.usage"]').click();
  await picker.getByRole("button", { name: zh.metricPicker.confirm, exact: true }).click();
  await frames("slots").nth(2).waitFor();
  await page.getByRole("button", { name: zh.monitorPage.saveLayoutLabel, exact: true }).click();
  await page.getByRole("button", { name: zh.monitorPage.editLayoutLabel, exact: true }).waitFor();
  const saved = await page.evaluate((key) => JSON.parse(localStorage.getItem(key)).dashboard.cards, frontendPreferencesKey);
  assert.deepEqual(saved.find((item) => item.id === "slots").slots.map((slot) => [slot.metricId, slot.size]),
    [["cpu.usage", "medium"], ["cpu.frequency", "large"], ["cpu.usage", "small"]]);
  assert.deepEqual(saved.find((item) => item.id === "legacy").slots.map((slot) => slot.size), ["medium", "small", "small"],
    "the migrated card is saved as slots");

  // 4'. 两个减弱档：改大小下一帧就到终态。
  for (const mode of ["none", "ultra"]) {
    await load(mode);
    await page.getByRole("button", { name: zh.monitorPage.editLayoutLabel, exact: true }).click();
    await card("legacy").locator(".slot-resize").nth(1).click();
    const instant = await sampleAfter(page, page.getByRole("menuitem", { name: option("large"), exact: true }), "legacy");
    assert.equal(Math.round(instant[0][1].height), finalHeight, `${mode}: the slot is at its new size on the next frame`);
    await page.getByRole("button", { name: zh.monitorPage.cancelEditLabel, exact: true }).click();
  }
  assert.deepEqual(errors, []);

  // 回到正常档，运行器收尾的可用性检查按默认页面走。
  await load("desktop");
  return { cards: cardHeights.length, legacy, normalFrames: heights.length };
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * 点 target，之后逐帧记下卡片里每个槽位外框（selector 给了就只记那一个）的上边和高度，约 0.8 秒。
 * 点击和取样在同一个页面任务里，第一份样本就是点击后的第一帧。
 */
async function sampleAfter(page, target, cardId, selector) {
  const handle = await target.elementHandle();
  return page.evaluate(async ([element, id, only]) => {
    const container = document.querySelector(`.metric-card[data-card-id="${id}"] .metric-slots`);
    const read = () => {
      const origin = container.getBoundingClientRect();
      const box = (frame) => { const rect = frame.getBoundingClientRect(); return { top: rect.top - origin.top, height: rect.height }; };
      return only ? box(container.querySelector(only)) : [...container.querySelectorAll(".card-slot:not(.card-slot-add)")].map(box);
    };
    element.click();
    const samples = [];
    const end = performance.now() + 800;
    while (performance.now() < end) {
      await new Promise((resolve) => requestAnimationFrame(resolve));
      samples.push(read());
    }
    return samples;
  }, [handle, cardId, selector ?? null]);
}
