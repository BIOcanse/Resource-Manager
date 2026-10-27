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
 * 4. 改大小、删除、新增、拖动排序放下是稍大一些的变化（docs/animation_style_2026-10-26.md「总原则」第一类）：真实槽位当即在终态、可命中，
 *    和无动画档的时刻相同；会动的风格里滑过去的画面在不可命中的副本上（[data-reflow-copy]），新出现的区域第一帧就在终态；
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
  const frames = (id) => card(id).locator(".card-slot:not(.card-slot-add):not([data-exit-ghost])");
  const geometry = (id) => card(id).evaluate((element) => {
    const origin = element.querySelector(".metric-slots").getBoundingClientRect();
    return [...element.querySelectorAll(".card-slot:not([data-exit-ghost])")].map((frame) => {
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
  // 4. 会动的风格：选大以后真实槽位当即在终态（点击处理完那一刻），这一槽的高度和下一槽的位置在副本上走过去。
  const timeline = await sampleAfter(page, page.getByRole("menuitem", { name: option("large"), exact: true }), "slots");
  const finalHeight = 3 * legacy[1].height + 2 * gap;
  assert.equal(Math.round(timeline.immediate.real[1].height), finalHeight, `the resized slot is final when the click is handled: ${JSON.stringify({ immediate: timeline.immediate, first: timeline.samples.slice(0, 3) })}`);
  assert.ok(timeline.samples.every((sample) => Math.round(sample.real[1].height) === finalHeight), "the real slot never animates");
  const heights = timeline.samples.flatMap((sample) => sample.copies.map((copy) => copy.height));
  assert.ok(heights.some((height) => height > legacy[1].height + 1 && height < finalHeight - 1),
    `the copy passes through intermediate heights: ${heights.map(Math.round).join(",")}`);
  assert.equal(timeline.samples.at(-1).copies.length, 0, "the copies are gone when the slide ends");
  assert.equal(Math.round(timeline.immediate.real[2].top), legacy[0].height + gap + finalHeight + gap, "the next slot moved down at once");
  assert.equal(await add(2).count(), 0, "the card is now full: no +");
  // 大了就有趋势。
  await frames("slots").nth(1).locator(".metric-trend").waitFor();
  // 删除最后一槽：剩余一个单位，“+”出现。出现是进场（docs/animation_style_2026-10-26.md「总原则」）：
  // 第一帧就在终态的位置和高度，只淡入；卡片槽位的平面移动只带已经在场的槽位。
  const removeLast = frames("slots").nth(2).locator(".slot-delete");
  const appear = (await sampleAfter(page, removeLast, "slots", ".card-slot-add:not([data-exit-ghost])")).samples.map((sample) => sample.real);
  const last = appear.at(-1);
  assert.equal(Math.round(last.height), legacy[1].height);
  assert.ok(appear.every((sample) => Math.abs(sample.height - last.height) < 0.5 && Math.abs(sample.top - last.top) < 0.5),
    `the + appears at its final place and height: ${appear.map((sample) => `${sample.top.toFixed(1)}/${sample.height.toFixed(1)}`).join(",")}`);

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
    assert.equal(Math.round(instant.immediate.real[1].height), finalHeight, `${mode}: the slot is at its new size when the click is handled`);
    assert.ok(instant.samples.every((sample) => sample.copies.length === 0), `${mode}: no reflow copy`);
    await page.getByRole("button", { name: zh.monitorPage.cancelEditLabel, exact: true }).click();
  }
  // 6. 重排的交互时刻和无动画档相同（「总原则」第一类）：改大小（卡片里的重排）和拖动排序放下（落位），
  //    每种风格都在点击、松开处理完的那一刻就是终态几何，真实槽位在中心可命中；会动的风格里副本在画面上滑过去。
  const reflowRuns = {};
  for (const style of ["none", "desktop", "terminal", "smooth"]) {
    await load(style);
    await page.getByRole("button", { name: zh.monitorPage.editLayoutLabel, exact: true }).click();
    await card("legacy").locator(".slot-resize").nth(1).click();
    const resize = await sampleAfter(page, page.getByRole("menuitem", { name: option("large"), exact: true }), "legacy");
    const drop = await dropAfter(page, frames("slots").nth(0), frames("slots").nth(2), "slots");
    await page.getByRole("button", { name: zh.monitorPage.cancelEditLabel, exact: true }).click();
    reflowRuns[style] = { resize, drop };
  }
  const shape = (state) => state.real.map((box) => [Math.round(box.top), Math.round(box.height)]);
  for (const [style, runs] of Object.entries(reflowRuns)) {
    for (const [what, run] of Object.entries(runs)) {
      assert.deepEqual(shape(run.immediate), shape(run.samples.at(-1)), `${style} ${what}: the real slots are final when the input is handled`);
      assert.deepEqual(shape(run.immediate), shape(reflowRuns.none[what].immediate), `${style} ${what}: the same final geometry as none`);
      assert.ok(run.immediate.hits, `${style} ${what}: every real slot is hit at its final place when the input is handled`);
      assert.ok(run.samples.every((sample) => sample.hits), `${style} ${what}: the real slots stay hittable while the copies slide`);
      const copied = run.samples.some((sample) => sample.copies.length > 0);
      assert.equal(copied, style !== "none", `${style} ${what}: ${style === "none" ? "no copy" : "the slide is drawn on a copy"}`);
    }
  }
  assert.deepEqual(errors, []);

  // 回到正常档，运行器收尾的可用性检查按默认页面走。
  await load("desktop");
  return { cards: cardHeights.length, legacy, normalFrames: heights.length,
    reflow: Object.fromEntries(Object.entries(reflowRuns).map(([style, runs]) => [style, Object.fromEntries(Object.entries(runs)
      .map(([what, run]) => [what, { frames: run.samples.length, copyFrames: run.samples.filter((sample) => sample.copies.length > 0).length }]))])) };
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * 点 target，之后逐帧记下卡片里每个槽位外框（selector 给了就只记那一个）的上边和高度，约 0.8 秒。
 * 点击和取样在同一个页面任务里，第一份样本就是点击后的第一帧。
 */
/** 卡片里此刻的样子：真实槽位（或 only 选中的那一个）的框、重排副本的框，以及每个真实槽位的中心命中的是不是它自己。 */
const readSlots = ([id, only]) => {
  const container = document.querySelector(`.metric-card[data-card-id="${id}"] .metric-slots`);
  const origin = container.getBoundingClientRect();
  const box = (frame) => { const rect = frame.getBoundingClientRect(); return { top: rect.top - origin.top, height: rect.height }; };
  const real = [...container.querySelectorAll(".card-slot:not(.card-slot-add):not([data-exit-ghost])")];
  const hits = real.every((frame) => {
    const rect = frame.getBoundingClientRect();
    const target = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
    return !!target && frame.contains(target);
  });
  return { real: only ? box(container.querySelector(only)) : real.map(box), copies: [...container.querySelectorAll("[data-reflow-copy]")].map(box), hits };
};

/** 点击 target，点击处理完那一刻读一次（immediate），之后 800 ms 每帧读一次。 */
async function sampleAfter(page, target, cardId, selector) {
  const handle = await target.elementHandle();
  return page.evaluate(async ([element, id, only, read]) => {
    const readNow = new Function(`return (${read})`)();
    element.click();
    const immediate = readNow([id, only]);
    const samples = [];
    const end = performance.now() + 800;
    while (performance.now() < end) {
      await new Promise((resolve) => requestAnimationFrame(resolve));
      samples.push(readNow([id, only]));
    }
    return { immediate, samples };
  }, [handle, cardId, selector ?? null, readSlots.toString()]);
}

/** 真实鼠标把 source 拖到 target 上松开：松开处理完那一刻读一次（immediate），之后 800 ms 每帧读一次。 */
async function dropAfter(page, source, target, cardId) {
  const from = await source.boundingBox();
  const to = await target.boundingBox();
  await page.evaluate(([id, read]) => {
    const readNow = new Function(`return (${read})`)();
    const probe = { immediate: null, samples: [], done: false };
    window.__dropProbe = probe;
    window.addEventListener("pointerup", () => {
      queueMicrotask(() => {
        probe.immediate = readNow([id, null]);
        const end = performance.now() + 800;
        const tick = () => {
          probe.samples.push(readNow([id, null]));
          if (performance.now() < end) requestAnimationFrame(tick);
          else probe.done = true;
        };
        requestAnimationFrame(tick);
      });
    }, { once: true });
  }, [cardId, readSlots.toString()]);
  const start = { x: from.x + 16, y: from.y + from.height / 2 };
  const end = { x: to.x + 16, y: to.y + to.height / 2 };
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  for (let step = 1; step <= 10; step += 1) await page.mouse.move(start.x + (end.x - start.x) * step / 10, start.y + (end.y - start.y) * step / 10);
  await page.mouse.up();
  await page.waitForFunction(() => window.__dropProbe.done, undefined, { timeout: 5000 });
  return page.evaluate(() => window.__dropProbe);
}
