// 滚轮的量换成“格”：鼠标滚轮一格是 1，触控板一次只给几个像素，就是零点几格。
// 磁盘方格图的缩放和资源条的滚轮换选中都按格算，同一个滚动量在两处是同一个意思。
// Chromium（WebView2）在 Windows 上一格报 100 px（系统默认每格 3 行；改了滚动行数，像素跟着变，这里不另定）；
// 按行报的一格是 3 行，按页报的一格是 1 页。

const pixelsPerNotch = 100;
const linesPerNotch = 3;

/** WheelEvent 的 deltaY（或 deltaX）按 deltaMode 换成格数，正负和 delta 相同。 */
export function wheelNotches(delta: number, deltaMode: number): number {
  if (deltaMode === 1) return delta / linesPerNotch;
  if (deltaMode === 2) return delta;
  return delta / pixelsPerNotch;
}

/**
 * 按格走的累加器：零碎的滚动量攒够一整格才走一步，余下的留给下一次；方向一反，攒的量清零重来。
 * 每次返回这一下该走的整步数（带正负，大多数时候是 0 或 ±1；一次给了好几格就是好几步）。
 */
export function createWheelStepAccumulator(): (notches: number) => number {
  let total = 0;
  return (notches) => {
    if (notches === 0) return 0;
    if (total !== 0 && Math.sign(total) !== Math.sign(notches)) total = 0;
    total += notches;
    // 0.1 格攒十次是 0.9999…，按一整格算：留一点浮点余量。
    const steps = Math.trunc(total + Math.sign(total) * 1e-9);
    total -= steps;
    // trunc 会给出 -0，统一成 0。
    return steps === 0 ? 0 : steps;
  };
}
