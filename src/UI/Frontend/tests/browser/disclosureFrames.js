/**
 * 折叠标题的逐帧读数（docs/animation_style_2026-10-26.md「总原则」）：交互和无动画完全一致，所以内容体的高度当帧到位、不做动画；
 * 会动的风格里画面只有内容淡入（进场槽位 disclosure）和收起时折叠块里的幽灵淡出（退出槽位 disclosure）。
 *
 * toggleDisclosure 点一下标题，按帧读：内容体高度、真实内容的不透明度、折叠块里退出幽灵的不透明度（没有幽灵是 null）。
 * checkDisclosureFrames 按风格给出不符合的地方（空数组就是通过）。
 */
export function toggleDisclosure(page, headerSelector, durationMs = 900) {
  return page.evaluate(async ({ headerSelector, durationMs }) => {
    const button = [...document.querySelectorAll(headerSelector)].find((element) => !element.closest("[data-exit-ghost]"));
    const root = button.closest(".ui-disclosure");
    const body = root.querySelector(":scope > .ui-disclosure-body");
    const content = body.querySelector(":scope > .ui-disclosure-content");
    const read = () => {
      const ghost = root.querySelector(':scope > [data-exit-ghost][data-exit-slot="disclosure"]');
      return {
        height: Math.round(body.getBoundingClientRect().height),
        opacity: Math.round(Number(getComputedStyle(content).opacity) * 1000) / 1000,
        ghost: ghost ? Math.round(Number(getComputedStyle(ghost).opacity) * 1000) / 1000 : null,
        ghostHidden: ghost ? ghost.inert && ghost.getAttribute("aria-hidden") === "true" && getComputedStyle(ghost).pointerEvents === "none" : true
      };
    };
    const frames = [];
    button.click();
    // 点击处理完的那一刻：布局已经是终态。
    const sameTask = read();
    const start = performance.now();
    while (performance.now() - start < durationMs) {
      await new Promise((resolve) => requestAnimationFrame(resolve));
      frames.push(read());
    }
    return { sameTask, frames, expanded: button.getAttribute("aria-expanded"), inert: body.inert,
      openness: button.style.getPropertyValue("--disclosure-openness").trim() };
  }, { headerSelector, durationMs });
}

export function checkDisclosureFrames(run, { moving, opening }) {
  const problems = [];
  const final = run.frames.at(-1);
  const between = (value) => value !== null && value > 0.01 && value < 0.99;
  if (opening ? !(final.height > 0) : final.height !== 0) problems.push(`the body does not end ${opening ? "open" : "closed"}: ${final.height}`);
  if (run.sameTask.height !== final.height || run.frames.some((frame) => frame.height !== final.height)) {
    problems.push(`the body height animates; it must switch when the click is handled: ${run.sameTask.height}, ${run.frames.slice(0, 6).map((frame) => frame.height)}`);
  }
  if (run.frames.some((frame) => !frame.ghostHidden)) problems.push("the exit ghost can be hit or reached");
  if (opening) {
    if (final.opacity !== 1) problems.push(`the content does not end opaque: ${final.opacity}`);
    const fading = run.frames.filter((frame) => between(frame.opacity)).length;
    if (moving && fading < 3) problems.push(`opening shows no fade-in: ${run.frames.slice(0, 12).map((frame) => frame.opacity)}`);
    if (!moving && fading > 0) problems.push(`opening still fades without motion: ${run.frames.slice(0, 6).map((frame) => frame.opacity)}`);
  } else {
    const fading = run.frames.filter((frame) => between(frame.ghost)).length;
    if (moving && fading < 3) problems.push(`closing shows no fade-out ghost: ${run.frames.slice(0, 12).map((frame) => frame.ghost)}`);
    if (!moving && (run.sameTask.ghost !== null || run.frames.some((frame) => frame.ghost !== null))) problems.push("closing leaves a ghost without motion");
    if (final.ghost !== null) problems.push(`the closing ghost is still there after the run: ${final.ghost}`);
  }
  return problems;
}
