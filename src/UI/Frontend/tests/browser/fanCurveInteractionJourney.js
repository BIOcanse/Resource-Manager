import assert from "node:assert/strict";
import { fanModeFacts } from "./fanModeFacts.js";

/**
 * 风扇曲线走交互协议的运行时（Lodestone 协议第 4.4 节第 2 处迁移），真实鼠标事件：
 * 按在点上是抓住、不跳，拖动时点的圆心跟着指针；按在图里空白处交给横向最近的点，点跳到指针处接着拖；
 * 拖动中按 Esc 回到这次拖动前的值；方向键照旧。只改本地曲线，不点“应用”。
 * 点和曲线按运行时画出的位置画（docs/ui_interaction_kit.md 第 4 节第 18 项，2026-10-26 裁定）：默认风格（流畅平滑）下
 * Esc 取消和 Shift+方向键之后逐帧读点的圆心和曲线，要看到中间位置，曲线每一帧都经过点此刻的圆心。
 */
export default async function fanCurveInteractionJourney(page, baseUrl) {
  await page.route("**/api/control/objects", route => route.fulfill({ json: {
    readAt: new Date().toISOString(), objects: [{
      id: "A", kind: "fan", reportedName: "Fan A", isControllable: true,
      platform: { operatingSystem: "windows", vendor: "test" },
      ...fanModeFacts(["fan.curve"]),
      capabilities: [{ id: "fan.curve", valueKind: "curve", supported: true, requiredAccessLevel: "normal" }]
    }]
  } }));
  await page.route("**/api/control/objects/*/curve", route => route.fulfill({
    json: [40, 45, 50, 55, 60, 65, 70, 75, 80, 85, 95].map((temperatureCelsius, i) => ({ temperatureCelsius, percent: Math.min(100, 10 + i * 8) }))
  }));
  await page.route("**/api/control/state**", route => route.fulfill({ json: { desired: { objects: [] }, lastApply: { outcomes: [], appliedAt: new Date().toISOString() } } }));
  await page.setViewportSize({ width: 1400, height: 1000 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "控制面", exact: true }).click();
  const card = page.locator(".control-object").filter({ hasText: "Fan A" });
  await card.getByRole("radio", { name: "软件管理", exact: true }).click();
  await card.getByRole("checkbox", { name: "转速曲线", exact: true }).check();
  const chart = card.locator(".fan-curve-chart");
  await chart.waitFor();
  await chart.scrollIntoViewIfNeeded();

  // 点的可读名称同时说温度和转速（“60 °C，45%”），转速变了名称跟着变，所以按温度前缀找。
  const point = card.getByRole("slider", { name: /^60 °C，\d+%$/ });
  const value = async () => Number(await point.getAttribute("aria-valuenow"));
  const circle = async () => {
    const box = await point.locator(".fan-curve-point").boundingBox();
    return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
  };
  /** 屏幕纵坐标对应的转速（和图的坐标换算一致）。 */
  const percentAt = (y) => chart.evaluate((svg, clientY) => {
    const view = new DOMPoint(0, clientY).matrixTransform(svg.getScreenCTM().inverse());
    return Math.round(Math.min(100, Math.max(0, (1 - (view.y - 5) / 43) * 100)));
  }, y);
  const waitValue = async (expected, step) => {
    try {
      await page.waitForFunction(([element, target]) => Math.abs(Number(element.getAttribute("aria-valuenow")) - target) <= 1,
        [await point.elementHandle(), expected], { timeout: 3000 });
    } catch {
      throw new Error(`${step}: ${await value()}%, expected ${expected}%`);
    }
  };
  /** 做 action，之后 700 ms 逐帧读这个点的圆心（SVG 坐标）和曲线经过这个温度的那一点。 */
  const framesAfter = async (action) => {
    await point.evaluate((element) => {
      const dot = element.querySelector(".fan-curve-point");
      const line = element.ownerSVGElement.querySelector(".fan-curve-line");
      const cx = Number(dot.getAttribute("cx"));
      const samples = [];
      window.__fanCurveFrames = samples;
      let start = null;
      const tick = (now) => {
        start ??= now;
        const lineY = line.getAttribute("d").split(/[ML ]+/).filter(Boolean).map((pair) => pair.split(",").map(Number))
          .find(([x]) => Math.abs(x - cx) < 0.01)?.[1] ?? null;
        samples.push({ cy: Number(dot.getAttribute("cy")), lineY });
        if (now - start < 700) requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    });
    await action();
    await page.waitForFunction(() => window.__fanCurveFrames.length > 0, undefined, { timeout: 2000 });
    await page.waitForTimeout(750);
    return page.evaluate(() => window.__fanCurveFrames);
  };
  /** 画面经过中间位置走到终点，曲线每一帧都经过点的圆心。 */
  const assertSettling = (frames, step) => {
    const ys = frames.map((frame) => frame.cy);
    const from = ys[0];
    const to = ys.at(-1);
    const between = new Set(ys.filter((y) => Math.abs(y - from) > 0.01 && Math.abs(y - to) > 0.01).map((y) => y.toFixed(3)));
    assert.ok(between.size >= 2, `${step}: the point jumps instead of settling: ${JSON.stringify(ys.map((y) => Number(y.toFixed(2))))}`);
    for (const frame of frames) {
      assert.ok(frame.lineY !== null && Math.abs(frame.lineY - frame.cy) < 0.01, `${step}: the curve does not pass through the drawn point: ${JSON.stringify(frame)}`);
    }
    return { from: Number(from.toFixed(2)), to: Number(to.toFixed(2)), intermediate: between.size };
  };
  const moveTo = async (from, to) => {
    for (let i = 1; i <= 8; i += 1) await page.mouse.move(from.x + (to.x - from.x) * i / 8, from.y + (to.y - from.y) * i / 8);
  };

  // 按在点上偏下一点：抓住，不跳。
  const v0 = await value();
  let start = await circle();
  await page.mouse.move(start.x, start.y + 1);
  await page.mouse.down();
  await page.mouse.up();
  assert.equal(await value(), v0, "Pressing on the point grabs it without jumping");

  // 拖动：圆心跟着指针。
  start = await circle();
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await moveTo(start, { x: start.x, y: start.y - 60 });
  const dragged = await percentAt(start.y - 60);
  await waitValue(dragged, "While dragging the point");
  await page.mouse.up();
  await waitValue(dragged, "After releasing the point");
  const after = await circle();
  assert.ok(Math.abs(after.y - (start.y - 60)) <= 2, `The point centre follows the pointer: ${after.y} vs ${start.y - 60}`);

  // Esc 取消：回到这次拖动前的值。
  start = await circle();
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await moveTo(start, { x: start.x, y: start.y + 50 });
  await waitValue(await percentAt(start.y + 50), "Before cancelling");
  const cancelFrames = await framesAfter(() => page.keyboard.press("Escape"));
  await waitValue(dragged, "After Escape");
  await page.mouse.up();
  const cancelSettling = assertSettling(cancelFrames, "Escape");

  // 空白处：交给横向最近的点，点跳到指针处，接着拖。
  const box = await chart.boundingBox();
  const blank = { x: start.x + 2, y: box.y + box.height * 0.6 };
  await page.mouse.move(blank.x, blank.y);
  await page.mouse.down();
  await waitValue(await percentAt(blank.y), "Pressing the blank space jumps the nearest point");
  await moveTo(blank, { x: blank.x, y: blank.y - 30 });
  await waitValue(await percentAt(blank.y - 30), "Dragging after the jump");
  await page.mouse.up();

  // 方向键照旧。
  const v1 = await value();
  await point.press("ArrowUp");
  await waitValue(v1 + 1, "ArrowUp");
  const keyFrames = await framesAfter(() => point.press("Shift+ArrowDown"));
  await waitValue(v1 - 9, "Shift+ArrowDown");
  const keySettling = assertSettling(keyFrames, "Shift+ArrowDown");
  const spoken = await point.evaluate((element) => ({
    name: element.getAttribute("aria-label"),
    valueText: element.getAttribute("aria-valuetext"),
    title: element.querySelector("title")?.textContent
  }));
  assert.deepEqual(spoken, { name: `60 °C，${v1 - 9}%`, valueText: `${v1 - 9}%`, title: `60 °C，${v1 - 9}%` },
    "The point name carries temperature and speed in the interface language");
  return { grabbed: v0, dragged, keyboard: v1 - 9, cancelSettling, keySettling };
}
