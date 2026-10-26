/**
 * 退场的节点在交互上已经不在，画面上的幽灵（[data-exit-ghost]，src/presentation/nodeAnimation.ts）播完退出槽位才撤掉；
 * 进场的节点在交互上已经在终态，不透明度还在由呈现层带（内联的 will-change: opacity）。
 * 幽灵 inert、aria-hidden，按角色找的定位器看不见，按 CSS 类名数元素的会把它们也数进去；量画面的断言也要等进场走完。
 * 这类断言先等画面停下（正常动画下最多一次进场的时长，流畅平滑约 500 ms）。
 */
export async function waitForContentSettled(page, timeout = 3000) {
  await page.waitForFunction(() => document.querySelector("[data-exit-ghost]") === null
    && document.querySelector("[style*='will-change: opacity']") === null, undefined, { timeout });
}
