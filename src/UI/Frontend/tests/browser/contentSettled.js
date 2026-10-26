/**
 * 换页、换二级分页、换页签面板之后，旧内容的副本（.content-transition-leaving，docs/page_motion_2026-10-25.md）淡出期间还在 DOM 里：
 * 它们 inert、aria-hidden，按角色找的定位器看不见，按 CSS 类名数元素的会把它们也数进去。
 * 这类断言先等副本清掉（正常动画下最多一次换页缓动的时长，平滑手感 380 ms）。
 */
export async function waitForContentSettled(page, timeout = 3000) {
  await page.waitForFunction(() => document.querySelector(".content-transition-leaving") === null, undefined, { timeout });
}
