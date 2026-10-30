import { readFrontendPreferences, seedFrontendPreferences, writeFrontendPreferences } from "./frontendPreferencesFixture.js";

// 资源条的手感回归（混合数据 resourceSoftwareMix: "audit"）：
// 1. 键盘逐段走时每一步选中框都在（选中类不被展示分组的类名冲掉）；
// 2. 指针能指到每一段，包括不到 1 px 的小段；
// 3. 悬停换段时悬停框直接到位，不在指针后面滑；提示在视口里；
// 4. 选中空余段后数据更新，选择还在；
// 5. 正常动画下数据变化连续过渡、不来回（过渡中外观属性被写成同样的值也不打断），软件退出和回来时条上不露底；无动画下一帧到位；
// 6. Windows 高对比下焦点和选中用系统颜色外框；
// 7. 提示挂在页面最顶层：资源条下面紧挨着另一条时，提示整个看得见（四角和中心取到的都是提示本身）、在视口里、
//    不盖住悬停的那一段；1100 和 1920 宽、简体中文和德语都查，德语的读数各占一行。
// 5 里还有：过渡没走完时下一次更新就到了（软件刚退出就又回来），新的过渡从画面当前的位置接着走，不直接跳到末帧、不露底。
export default async function resourceBarInteractionJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", (error) => errors.push(String(error)));
  await setScenario(page, baseUrl, { resourceSoftwareMix: "audit", resourceBreakdownGeneration: 0 });
  await seedFrontendPreferences(page, (value) => {
    value.appearance.theme = "light";
    value.appearance.animationStyle = "desktop";
  });
  await page.setViewportSize({ width: 1100, height: 800 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  const track = page.locator('.resource-bar-track[data-metric-id="memory.usage"]');
  await waitForBar(page);
  await track.scrollIntoViewIfNeeded();

  // 1. 键盘逐段走
  await page.mouse.move(1, 1);
  await track.focus();
  const keyboard = [];
  for (let step = 0; step < 4; step++) {
    await page.keyboard.press("ArrowRight");
    await page.waitForTimeout(80);
    keyboard.push(await proxyState(page));
  }
  for (const [index, state] of keyboard.entries()) {
    assert(state && state.selected, `Selection ring missing after ArrowRight #${index + 1}: ${JSON.stringify(keyboard)}`);
  }
  assert(new Set(keyboard.map((state) => state.id)).size === 4, `Keyboard did not move segment by segment: ${JSON.stringify(keyboard)}`);

  // 4. 空余段的选择经得住数据更新
  await page.keyboard.press("End");
  await page.waitForTimeout(80);
  const endState = await proxyState(page);
  assert(endState?.id === "__resource-empty:memory.usage" && endState.selected, `End did not select the free segment: ${JSON.stringify(endState)}`);
  const header = () => page.locator('.resource-breakdown-item:has(.resource-bar-track[data-metric-id="memory.usage"]) .resource-breakdown-header > span').textContent();
  const headerBefore = await header();
  await setScenario(page, baseUrl, { resourceBreakdownGeneration: 1 });
  await waitUntil(async () => (await header()) !== headerBefore, "memory bar update");
  await page.waitForTimeout(150);
  const afterUpdate = await proxyState(page);
  assert(afterUpdate?.id === "__resource-empty:memory.usage" && afterUpdate.selected,
    `A data update dropped the free-segment selection: ${JSON.stringify(afterUpdate)}`);

  // 2. 指针能指到每一段
  const box = await track.boundingBox();
  const reached = new Set();
  let total = 0;
  for (let x = 2; x < box.width - 2; x += 1) {
    await page.mouse.move(box.x + x, box.y + box.height / 2);
    const state = await proxyState(page);
    if (state) {
      reached.add(state.id);
      total = state.setSize;
    }
  }
  assert(total > 10 && reached.size === total, `Pointer could reach ${reached.size} of ${total} segments: ${[...reached].join(", ")}`);

  // 3. 换段时悬停框直接到位，提示在视口里
  await page.mouse.move(box.x + box.width - 20, box.y + box.height / 2);
  await page.waitForTimeout(500);
  await page.mouse.move(box.x + 12, box.y + box.height / 2);
  await page.waitForTimeout(60);
  const jump = await page.evaluate(() => {
    const item = document.querySelector('.resource-bar-track[data-metric-id="memory.usage"]').closest(".resource-breakdown-item");
    const layer = item.querySelector(".resource-segment-interaction-layer").getBoundingClientRect();
    const tooltip = document.querySelector(".resource-tooltip")?.getBoundingClientRect();
    const trackBox = item.querySelector(".resource-bar-track").getBoundingClientRect();
    return { layerLeft: layer.left - trackBox.left, tooltipLeft: tooltip?.left ?? -1, tooltipRight: tooltip?.right ?? -1,
      viewportRight: document.documentElement.clientWidth };
  });
  assert(jump.layerLeft < 4, `Hover ring slid instead of jumping to the hovered segment: ${JSON.stringify(jump)}`);
  assert(jump.tooltipLeft >= 0 && jump.tooltipRight <= jump.viewportRight,
    `Tooltip left the viewport: ${JSON.stringify(jump)}`);
  await page.mouse.move(1, 1);

  // 5. 过渡：正常动画连续、不来回；软件退出时不露底
  // 过渡开始后把外观属性原样再写一遍（App 的外观副作用随自调度快照重跑时就是这样），过渡不能被打断。
  const values = await recordTransition(page, baseUrl, 2, { rewriteAppearance: true });
  assert(values.positions.length >= 5, `Value change did not animate: ${JSON.stringify(values)}`);
  assert(values.reversals === 0, `Value change went back and forth: ${JSON.stringify(values)}`);
  const leave = await recordTransition(page, baseUrl, 3);
  assert(leave.positions.length >= 5 && leave.holeFrames === 0, `Leaving software left a hole or jumped: ${JSON.stringify(leave)}`);
  const back = await recordTransition(page, baseUrl, 4);
  assert(back.positions.length >= 5 && back.holeFrames === 0, `Returning software left a hole or jumped: ${JSON.stringify(back)}`);
  // 过渡中途又来一次更新：软件退出（第 11 代）的过渡刚动起来，就把数据换成它回来的第 12 代。
  // 只在这个画布上把 --motion-bar 拉长到 1500 ms，让第二次更新一定落在第一次过渡里；画布的段数（data-resource-segment-count）
  // 标出两次更新各自到达的那一帧。
  const interrupted = await recordTransition(page, baseUrl, 11, { interruptWith: 12, durationMs: 1500, windowMs: 5000 });
  assert(interrupted.leftAt !== null && interrupted.returnedAt !== null, `The leave and return updates did not both arrive: ${JSON.stringify(interrupted)}`);
  assert(interrupted.returnedAt - interrupted.leftAt < 1500,
    `The second update arrived after the first transition had ended, so nothing was interrupted: ${JSON.stringify(interrupted)}`);
  assert(interrupted.holeFrames === 0, `An interrupted transition left a hole: ${JSON.stringify(interrupted)}`);
  assert(interrupted.positionsAfterReturn >= 5,
    `The transition after an interrupting update jumped to its end instead of moving on from the current frame: ${JSON.stringify(interrupted)}`);
  await writeFrontendPreferences(page, (value) => { value.appearance.animationStyle = "none"; });
  await page.reload({ waitUntil: "domcontentloaded" });
  await waitForBar(page);
  const reduced = await recordTransition(page, baseUrl, 5);
  assert(reduced.positions.length === 1 && reduced.holeFrames === 0, `No-animation mode did not land in one frame: ${JSON.stringify(reduced)}`);

  // 6. 高对比
  await page.emulateMedia({ forcedColors: "active" });
  await page.mouse.move(1, 1);
  await track.focus();
  await page.keyboard.press("ArrowRight");
  await page.waitForTimeout(80);
  const forced = await page.evaluate(() => {
    const element = document.querySelector('.resource-bar-track[data-metric-id="memory.usage"]');
    const proxy = element.querySelector(".resource-segment-interaction-proxy");
    const trackStyle = getComputedStyle(element);
    const proxyStyle = getComputedStyle(proxy);
    return {
      focusVisible: element.matches(":focus-visible"),
      track: [trackStyle.outlineStyle, trackStyle.outlineWidth],
      proxy: [proxyStyle.outlineStyle, proxyStyle.outlineWidth]
    };
  });
  await page.emulateMedia({ forcedColors: "none" });
  assert(forced.focusVisible && forced.track[0] === "solid" && forced.track[1] === "2px", `Forced-colours focus outline missing: ${JSON.stringify(forced)}`);
  assert(forced.proxy[0] === "solid" && forced.proxy[1] === "3px", `Forced-colours selection outline missing: ${JSON.stringify(forced)}`);

  // 7. 提示层（换了语言、动画和窗口大小，查完还原，门禁之后的页面检查按简体中文找区域）
  const layer = [];
  const original = (await readFrontendPreferences(page)).appearance;
  const originalLanguage = original.language;
  const originalAnimations = original.animationStyle;
  for (const [width, height] of [[1100, 800], [1920, 1080]]) {
    for (const language of ["zh-CN", "de-DE"]) {
      await writeFrontendPreferences(page, (value) => { value.appearance.language = language; value.appearance.animationStyle = "none"; });
      await page.setViewportSize({ width, height });
      await page.reload({ waitUntil: "domcontentloaded" });
      await waitForBar(page);
      for (const position of ["top", "middle"]) {
        const result = await tooltipLayerCheck(page, position);
        layer.push({ width, language, position, ...result });
        assert(result.tooltip, `${width}/${language}/${position}: no tooltip while hovering`);
        assert(!result.insideBarRow, `${width}/${language}/${position}: the tooltip is still inside a bar row`);
        assert(result.hits.every(Boolean), `${width}/${language}/${position}: part of the tooltip is covered ${JSON.stringify(result)}`);
        assert(result.inViewport, `${width}/${language}/${position}: the tooltip leaves the viewport ${JSON.stringify(result)}`);
        assert(!result.coversSegment, `${width}/${language}/${position}: the tooltip covers the hovered segment ${JSON.stringify(result)}`);
        assert(result.lines.every((count) => count === 1), `${width}/${language}/${position}: a reading wraps ${JSON.stringify(result.lines)}`);
        if (position === "top") {
          assert(result.placement === "below" && result.overlapsNextBar,
            `${width}/${language}: the top case must open over the next bar to test the layer ${JSON.stringify(result)}`);
        }
      }
    }
  }

  await writeFrontendPreferences(page, (value) => { value.appearance.language = originalLanguage; value.appearance.animationStyle = originalAnimations; });
  await page.setViewportSize({ width: 1100, height: 800 });
  await page.reload({ waitUntil: "domcontentloaded" });
  await waitForBar(page);

  assert(errors.length === 0, `Page errors: ${JSON.stringify(errors)}`);
  return { keyboard, endState, afterUpdate, reached: reached.size, total, jump, values, leave, back, reduced, forced, layer };
}

// CPU 条放到视口顶部（提示只能往下开，正好压在物理内存那一行上）或中间，悬停它的第一段，量提示。
async function tooltipLayerCheck(page, position) {
  await page.mouse.move(1, 1);
  await page.evaluate((where) => {
    document.querySelector('.resource-bar-track[data-metric-id="cpu.usage"]').scrollIntoView({ block: where === "top" ? "start" : "center" });
  }, position);
  await page.waitForTimeout(200);
  const box = await page.locator('.resource-bar-track[data-metric-id="cpu.usage"]').boundingBox();
  await page.mouse.move(box.x + 30, box.y + box.height / 2);
  await page.waitForFunction(() => document.querySelector(".resource-tooltip")?.dataset.placement, null, { timeout: 5000 }).catch(() => {});
  await page.waitForTimeout(150);
  return page.evaluate(() => {
    const tooltip = document.querySelector(".resource-tooltip");
    if (!tooltip) return { tooltip: false };
    const rect = tooltip.getBoundingClientRect();
    tooltip.style.pointerEvents = "auto";
    // 角上的点往里收半个圆角（至少 2 px），落在圆角形状里面；收 2 px 在 9 px 圆角外面，取到的是提示下面的元素。
    const inset = Math.max(2, (Number.parseFloat(getComputedStyle(tooltip).borderTopLeftRadius) || 0) / 2);
    const points = [[rect.left + inset, rect.top + inset], [rect.right - inset, rect.top + inset], [rect.left + inset, rect.bottom - inset], [rect.right - inset, rect.bottom - inset],
      [(rect.left + rect.right) / 2, (rect.top + rect.bottom) / 2]];
    const hits = points.map(([x, y]) => { const element = document.elementFromPoint(x, y); return Boolean(element && tooltip.contains(element)); });
    tooltip.style.pointerEvents = "";
    const segment = document.querySelector('.resource-bar-track[data-metric-id="cpu.usage"] .resource-segment-interaction-proxy').getBoundingClientRect();
    const next = document.querySelector('.resource-bar-track[data-metric-id="memory.usage"]').closest(".resource-breakdown-item").getBoundingClientRect();
    const viewport = document.documentElement;
    return {
      tooltip: true,
      insideBarRow: Boolean(tooltip.closest(".resource-breakdown")),
      placement: tooltip.dataset.placement,
      rect: { left: Math.round(rect.left), top: Math.round(rect.top), right: Math.round(rect.right), bottom: Math.round(rect.bottom) },
      hits,
      inViewport: rect.left >= 0 && rect.top >= 0 && rect.right <= viewport.clientWidth && rect.bottom <= viewport.clientHeight,
      coversSegment: rect.top < segment.bottom && rect.bottom > segment.top,
      overlapsNextBar: rect.bottom > next.top && rect.top < next.bottom,
      lines: [...tooltip.querySelectorAll(".resource-tooltip-line")].map((line) =>
        Math.round(line.getBoundingClientRect().height / Number.parseFloat(getComputedStyle(line).lineHeight)))
    };
  });
}

async function waitForBar(page) {
  await page.waitForFunction(() => {
    const canvas = document.querySelector('.resource-bar-track[data-metric-id="memory.usage"] canvas');
    return canvas instanceof HTMLCanvasElement && canvas.width > 100 && Number(canvas.dataset.resourceSegmentCount) >= 10;
  }, null, { timeout: 10000 });
  await page.waitForTimeout(500);
}

function proxyState(page) {
  return page.evaluate(() => {
    const proxy = document.querySelector('.resource-bar-track[data-metric-id="memory.usage"] .resource-segment-interaction-proxy');
    return proxy ? {
      id: proxy.getAttribute("data-software-id"),
      selected: proxy.classList.contains("precision-selected"),
      setSize: Number(proxy.getAttribute("aria-setsize"))
    } : null;
  });
}

// 数据变到第 generation 代，记下 3 s 内每帧第一段右边界的位置（最左两段是纯色，不受斜纹影响）和露底的像素。
// interruptWith：第一段边界开始动的那一帧之后马上换成这一代，记下两次更新到达的时刻（画布段数变化的那一帧）。
// durationMs：只在这块画布上覆盖 --motion-bar，记录完恢复。
async function recordTransition(page, baseUrl, generation, { rewriteAppearance = false, interruptWith = null, durationMs = null, windowMs = 3000 } = {}) {
  await page.evaluate(({ rewrite, duration, windowLength }) => {
    const canvas = document.querySelector('.resource-bar-track[data-metric-id="memory.usage"] canvas');
    if (duration !== null) canvas.style.setProperty("--motion-bar", `${duration}ms`);
    const context = canvas.getContext("2d");
    const ratio = canvas.width / canvas.getBoundingClientRect().width;
    const overscan = Number.parseFloat(getComputedStyle(canvas.parentElement).getPropertyValue("--segment-paint-overscan")) * ratio;
    const frames = [];
    let rewritten = !rewrite;
    window.__barTransition = frames;
    window.__barTransitionMoved = false;
    window.__barTransitionDone = false;
    const started = performance.now();
    const sample = (now) => {
      if (now - started > windowLength) {
        canvas.style.removeProperty("--motion-bar");
        window.__barTransitionDone = true;
        return;
      }
      const data = context.getImageData(0, Math.floor(canvas.height - overscan - 3 * ratio), canvas.width, 1).data;
      let boundary = -1;
      let lastPainted = -1;
      let holes = 0;
      for (let x = 0; x < canvas.width; x++) {
        if (data[x * 4 + 3] > 0) lastPainted = x;
        if (boundary < 0 && x > 0 && (data[x * 4] !== data[0] || data[x * 4 + 1] !== data[1] || data[x * 4 + 2] !== data[2])) boundary = x;
      }
      for (let x = 0; x < lastPainted; x++) if (data[x * 4 + 3] === 0) holes++;
      frames.push({ boundary, holes, at: now - started, count: Number(canvas.dataset.resourceSegmentCount) });
      if (frames.length > 1 && boundary !== frames[0].boundary) window.__barTransitionMoved = true;
      if (!rewritten && frames.length > 1 && boundary !== frames[0].boundary) {
        rewritten = true;
        document.body.dataset.animations = document.body.dataset.animations;
        document.documentElement.dataset.theme = document.documentElement.dataset.theme;
      }
      requestAnimationFrame(sample);
    };
    requestAnimationFrame(sample);
  }, { rewrite: rewriteAppearance, duration: durationMs, windowLength: windowMs });
  await setScenario(page, baseUrl, { resourceBreakdownGeneration: generation });
  if (interruptWith !== null) {
    await page.waitForFunction(() => window.__barTransitionMoved === true, null, { polling: "raf", timeout: windowMs });
    await setScenario(page, baseUrl, { resourceBreakdownGeneration: interruptWith });
  }
  await page.waitForFunction(() => window.__barTransitionDone === true, null, { timeout: windowMs + 5000 });
  const frames = await page.evaluate(() => window.__barTransition);
  const positions = [];
  for (let index = 1; index < frames.length; index++) {
    if (frames[index].boundary !== frames[index - 1].boundary) positions.push(frames[index].boundary);
  }
  const start = frames[0]?.boundary ?? 0;
  const direction = positions.length ? Math.sign(positions.at(-1) - start) : 0;
  const reversals = positions.filter((value, index) =>
    direction !== 0 && Math.sign(value - (index === 0 ? start : positions[index - 1])) === -direction).length;
  const result = { start, positions, reversals, holeFrames: frames.filter((frame) => frame.holes > 0).length, frames: frames.length };
  if (interruptWith === null) return result;
  const initialCount = frames[0]?.count;
  const leftIndex = frames.findIndex((frame) => frame.count < initialCount);
  const returnedIndex = leftIndex < 0 ? -1 : frames.findIndex((frame, index) => index > leftIndex && frame.count === initialCount);
  let positionsAfterReturn = 0;
  for (let index = Math.max(returnedIndex, 1); returnedIndex > 0 && index < frames.length; index++) {
    if (frames[index].boundary !== frames[index - 1].boundary) positionsAfterReturn++;
  }
  return {
    ...result,
    leftAt: leftIndex < 0 ? null : Math.round(frames[leftIndex].at),
    returnedAt: returnedIndex < 0 ? null : Math.round(frames[returnedIndex].at),
    positionsAfterReturn
  };
}

async function setScenario(page, baseUrl, patch) {
  const response = await page.request.post(`${baseUrl}/__test/scenario`, { data: patch });
  assert(response.ok(), `Fixture setup failed: ${response.status()}`);
}

async function waitUntil(check, label) {
  const deadline = Date.now() + 8000;
  while (Date.now() < deadline) {
    if (await check()) return;
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error(`Timed out waiting for ${label}`);
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}
