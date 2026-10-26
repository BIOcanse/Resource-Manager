import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { seedFrontendPreferences, writeFrontendPreferences } from "./frontendPreferencesFixture.js";
import { waitForStableRendering } from "./journeyWaits.js";

/**
 * 滚轮按格累积（interactions/wheelNotches.ts）：
 * - 磁盘方格图：十下 10 px 的触控板滚动合起来是一格（放大 1.2 倍，而不是旧的每个事件 1.2 倍），
 *   画面一路单调地放大、分好几帧走完，没有一帧跳一大截；鼠标一格（100 px）也是几帧带过去；
 *   “无动画”下一格直接到位。缩放倍数从画布画整图位图时的宽度读（drawImage 的目标宽度 = 画布宽 × 倍数）。
 * - 资源条：选中内存条的一个分段后，零碎的滚动攒够 100 px 才换一个分段，方向一反攒的量清零。
 */
export default async function wheelAccumulationJourney(page, baseUrl) {
  const assert = (value, message) => { if (!value) throw new Error(message); };
  const errors = [];
  page.on("pageerror", (error) => errors.push(String(error)));
  // 记下方格图画布上每次画位图的目标宽度和时刻。
  await page.addInitScript(() => {
    window.__treemapDraws = [];
    const draw = CanvasRenderingContext2D.prototype.drawImage;
    CanvasRenderingContext2D.prototype.drawImage = function (...args) {
      if (args.length >= 5 && this.canvas.classList?.contains("disk-usage-treemap-canvas")) {
        window.__treemapDraws.push({ t: performance.now(), width: args[3] });
      }
      return draw.apply(this, args);
    };
  });
  await seedFrontendPreferences(page, (value) => { value.appearance.animations = "normal"; });
  const copy = await loadAppCopy("zh-CN");

  // ---------- 磁盘方格图 ----------
  const scenario = await page.request.post(`${baseUrl}/__test/scenario`, { data: { diskUsageMultiRoot: true } });
  assert(scenario.ok(), "Could not configure the disk-tree fixture");
  await page.setViewportSize({ width: 1100, height: 760 });
  const openTreemap = async () => {
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await page.locator(".shell-pages").getByRole("button", { name: copy.page.diskUsage, exact: true }).click();
    await page.locator(".disk-usage-result .optimization-heading h2").getByText("C:\\", { exact: true }).waitFor();
    const canvas = page.locator(".disk-usage-treemap-canvas");
    await canvas.scrollIntoViewIfNeeded();
    await page.waitForFunction(() => window.__treemapDraws.length > 0);
    await waitForStableRendering(page, ".disk-usage-treemap-canvas", { includeCanvas: true });
    return canvas;
  };
  /**
   * 在画布中心按给定的 deltaY 依次发滚轮事件（间隔 gapMs），等 settleMs 后交回
   * 整图宽度、最后一个事件的时刻和这段时间里的每次绘制（只取到最后一个事件后 140 ms：
   * 再往后视图停下，上层会换一份更细的布局和位图，宽度不再只由倍数决定）。
   */
  const wheel = (canvas, deltas, gapMs, settleMs) => canvas.evaluate(async (element, [list, gap, settle]) => {
    const rect = element.getBoundingClientRect();
    const base = window.__treemapDraws.at(-1).width;
    window.__treemapDraws.length = 0;
    const started = performance.now();
    let last = started;
    for (const deltaY of list) {
      element.dispatchEvent(new WheelEvent("wheel", {
        deltaY, deltaMode: 0, bubbles: true, cancelable: true,
        clientX: rect.left + rect.width / 2, clientY: rect.top + rect.height / 2
      }));
      last = performance.now();
      if (gap > 0) await new Promise((resolve) => setTimeout(resolve, gap));
    }
    await new Promise((resolve) => setTimeout(resolve, settle));
    const draws = window.__treemapDraws.filter((entry) => entry.t <= last + 140).map((entry) => ({ t: entry.t - started, scale: entry.width / base }));
    return { base, last: last - started, draws };
  }, [deltas, gapMs, settleMs]);
  const describe = (result) => JSON.stringify({ last: Math.round(result.last), draws: result.draws.map((entry) => [Math.round(entry.t), Number(entry.scale.toFixed(4))]) });
  /** 一路单调放大到 1.2 倍，中间至少 minimumFrames 帧，每帧最多走全程（对数）的 maxShare。 */
  const checkSmooth = (result, step, minimumFrames, maxShare) => {
    const scales = result.draws.map((entry) => entry.scale);
    const final = scales.at(-1);
    assert(final !== undefined && Math.abs(final - 1.2) < 0.012, `${step}: zoomed to ${final}, expected one notch (1.2): ${describe(result)}`);
    let previous = 1;
    let largest = 0;
    let between = 0;
    for (const scale of scales) {
      assert(scale >= previous - 1e-6, `${step}: zoom went backwards: ${describe(result)}`);
      largest = Math.max(largest, Math.log(scale / previous));
      if (scale > 1 + 1e-4 && scale < final - 1e-4) between += 1;
      previous = scale;
    }
    assert(between >= minimumFrames, `${step}: only ${between} in-between frames: ${describe(result)}`);
    assert(largest <= Math.log(1.2) * maxShare, `${step}: one frame jumped ${Math.exp(largest).toFixed(4)}x: ${describe(result)}`);
    return { frames: scales.length, between, largestFrameFactor: Number(Math.exp(largest).toFixed(4)) };
  };

  let canvas = await openTreemap();
  const initialTreemapWidth = await page.evaluate(() => window.__treemapDraws.at(-1).width);
  // 触控板：十下 10 px，每 8 ms 一下。
  const trackpad = checkSmooth(await wheel(canvas, Array(10).fill(-10), 8, 400), "Trackpad wheel", 4, 0.5);
  await page.getByRole("button", { name: copy.diskUsage.resetView, exact: true }).click();
  await page.waitForFunction(width => Math.abs(window.__treemapDraws.at(-1).width - width) < 0.1,
    initialTreemapWidth, { timeout: 5000 });
  await waitForStableRendering(page, ".disk-usage-treemap-canvas", { includeCanvas: true });
  // 鼠标一格：几帧带过去，不是一帧到位。
  const notch = checkSmooth(await wheel(canvas, [-100], 0, 400), "Mouse notch", 2, 0.85);

  // 无动画：一格直接到位，之后每一帧都是 1.2 倍。
  await writeFrontendPreferences(page, (value) => { value.appearance.animations = "none"; });
  canvas = await openTreemap();
  await page.waitForFunction(() => document.body.dataset.animations === "none");
  const still = await wheel(canvas, [-100], 0, 300);
  assert(still.draws.length > 0 && still.draws.every((entry) => Math.abs(entry.scale - 1.2) < 0.012),
    `No motion still animates the wheel zoom: ${describe(still)}`);
  await writeFrontendPreferences(page, (value) => { value.appearance.animations = "normal"; });

  // ---------- 资源条 ----------
  const bars = await page.request.post(`${baseUrl}/__test/scenario`, { data: { diskUsageMultiRoot: false, resourceSegmentCount: 8 } });
  assert(bars.ok(), "Could not configure the resource-bar fixture");
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const track = page.locator('.resource-bar-track[data-metric-id="memory.usage"]');
  await page.waitForFunction(() => Number(document.querySelector('.resource-bar-track[data-metric-id="memory.usage"] canvas')?.dataset.resourceSegmentCount) === 8);
  // 画布落定（分段的过渡走完）再点。
  await page.waitForFunction(() => !document.querySelector('.resource-bar-track[data-metric-id="memory.usage"]')?.classList.contains("moving"),
    null, { timeout: 5000 });
  await waitForStableRendering(page, '.resource-bar-track[data-metric-id="memory.usage"]', { includeCanvas: true });
  const bounds = await track.boundingBox();
  assert(bounds, "Memory bar bounds are missing");
  // 点一下选中一个分段，再按 Home 回到第一段（分段宽度、顺序跟占用走，点中的是哪段不固定），然后用滚轮换。
  await track.click({ position: { x: bounds.width / 64, y: bounds.height / 2 } });
  // 指针移开：悬停的分段优先于选中的分段占用交互层，读选中要先让悬停清掉。
  await page.mouse.move(2, 2);
  const selected = () => track.evaluate((element) => Number(element.querySelector(".precision-selected")?.getAttribute("aria-posinset") ?? 0));
  await page.waitForFunction(() => document.querySelector('.resource-bar-track[data-metric-id="memory.usage"] .precision-selected') !== null);
  await track.focus();
  await page.keyboard.press("Home");
  await page.waitForFunction(() => document.querySelector('.resource-bar-track[data-metric-id="memory.usage"] .precision-selected')
    ?.getAttribute("aria-posinset") === "1", null, { timeout: 5000 });
  const start = await selected();
  assert(start >= 1 && start <= 6, `Unexpected starting segment ${start}`);
  /** 发一串 deltaY，交回每个事件之后选中的是第几段。 */
  const scroll = (deltas) => track.evaluate(async (element, list) => {
    const rect = element.getBoundingClientRect();
    const positions = [];
    let pageScrolled = false;
    for (const deltaY of list) {
      const event = new WheelEvent("wheel", {
        deltaY, deltaMode: 0, bubbles: true, cancelable: true,
        clientX: rect.left + rect.width / 2, clientY: rect.top + rect.height / 2
      });
      element.dispatchEvent(event);
      // 攒着还没走的那几下也要拦住，页面不能跟着滚。
      if (!event.defaultPrevented) pageScrolled = true;
      await new Promise((resolve) => requestAnimationFrame(() => resolve()));
      positions.push(Number(element.querySelector(".precision-selected")?.getAttribute("aria-posinset") ?? 0));
    }
    return { positions, pageScrolled };
  }, deltas);
  const steps = [];
  // 9 下 10 px 不换；第 10 下攒够一格，换到下一段。
  let result = await scroll(Array(10).fill(10));
  assert(!result.pageScrolled, "Small wheel deltas on the selected bar were not intercepted");
  assert(result.positions.slice(0, 9).every((value) => value === start) && result.positions[9] === start + 1,
    `Trackpad deltas did not step once per 100 px: ${JSON.stringify({ start, ...result })}`);
  steps.push(result.positions);
  // 25 下 4 px 正好再一格。
  result = await scroll(Array(25).fill(4));
  assert(result.positions.slice(0, 24).every((value) => value === start + 1) && result.positions[24] === start + 2,
    `25 x 4 px did not step exactly one segment: ${JSON.stringify({ start, ...result })}`);
  steps.push(result.positions);
  // 往下攒 60 px 再往上 60 px：方向一反清零，不走；往上再 40 px 攒够一格，退回一段。
  result = await scroll([60, -60, -40]);
  assert(JSON.stringify(result.positions) === JSON.stringify([start + 2, start + 2, start + 1]),
    `Reversing the wheel did not reset the accumulated delta: ${JSON.stringify({ start, ...result })}`);
  steps.push(result.positions);
  // 鼠标一格照旧一格一段。
  result = await scroll([-100]);
  assert(result.positions[0] === start, `A mouse notch did not step one segment: ${JSON.stringify({ start, ...result })}`);
  steps.push(result.positions);

  assert(errors.length === 0, errors.join("\n"));
  return { treemap: { trackpad, notch, noMotionFrames: still.draws.length }, resourceBar: { start, steps } };
}
