import assert from "node:assert/strict";
import { createWheelStepAccumulator, wheelNotches } from "../src/interactions/wheelNotches.ts";
import { accumulateWheelZoom, wheelZoomAt, type DiskUsageWheelZoom } from "../src/features/diskUsage/diskUsageWheelZoom.ts";
import { identityViewport, pixelsToUnit, zoomAt } from "../src/features/diskUsage/diskUsageViewport.ts";

const close = (actual: number, expected: number, message: string, tolerance = 1e-9) =>
  assert.ok(Math.abs(actual - expected) <= tolerance, `${message}: ${actual} vs ${expected}`);

// 格数：像素 100 一格，行 3 一格，页 1 一格，正负跟着 delta。
assert.equal(wheelNotches(100, 0), 1);
assert.equal(wheelNotches(-250, 0), -2.5);
assert.equal(wheelNotches(3, 1), 1);
assert.equal(wheelNotches(-1, 2), -1);
assert.equal(wheelNotches(0, 0), 0);

// 资源条：鼠标一格走一步；触控板 4 px 一次，攒够 25 次（100 px）才走一步，余量留到下一步。
{
  const step = createWheelStepAccumulator();
  assert.equal(step(wheelNotches(100, 0)), 1);
  assert.equal(step(wheelNotches(-100, 0)), -1);
  const trackpad = createWheelStepAccumulator();
  const steps: number[] = [];
  for (let i = 0; i < 60; i += 1) steps.push(trackpad(wheelNotches(4, 0)));
  assert.equal(steps.reduce((sum, value) => sum + value, 0), 2, "240 px of trackpad scrolling is two steps");
  assert.equal(steps.indexOf(1), 24, "the first step comes on the 25th 4 px event");
  assert.ok(steps.every((value) => value === 0 || value === 1), "never more than one step per small event");
  // 0.1 格攒十次算一整格。
  const tenths = createWheelStepAccumulator();
  let total = 0;
  for (let i = 0; i < 10; i += 1) total += tenths(0.1);
  assert.equal(total, 1);
  // 一次给 3 格走 3 步。
  assert.equal(createWheelStepAccumulator()(3), 3);
}

// 方向一反，攒的量清零：先往下攒 0.9 格，再往上 0.5 格不走，往上再 0.5 格才走一步。
{
  const step = createWheelStepAccumulator();
  assert.equal(step(0.9), 0);
  assert.equal(step(-0.5), 0, "the 0.9 downward does not cancel the upward scroll");
  assert.equal(step(-0.5), -1);
  assert.equal(step(0), 0);
}

// 方格图：几下小滚动累积的目标，和一次滚同样格数的目标相同（同一个锚）。
const width = 1000;
const height = 500;
const pointer = { x: 300, y: 120 };
{
  let zoom: DiskUsageWheelZoom | null = null;
  let shown = identityViewport;
  for (let i = 0; i < 5; i += 1) {
    const before = shown;
    zoom = accumulateWheelZoom(zoom, shown, width, height, pointer.x, pointer.y, -0.2, i * 10, 100);
    // 下一下来时画面走到哪儿就从哪儿出发。
    assert.deepEqual(zoom.from, before);
    shown = wheelZoomAt(zoom, i * 10 + 5, (progress) => progress).viewport;
  }
  const once = zoomAt(identityViewport, width, height, pointer.x, pointer.y, 1.2);
  close(zoom!.to.scale, once.scale, "five 0.2-notch scrolls zoom as much as one notch");
  close(zoom!.to.offsetX, once.offsetX, "same anchor x");
  close(zoom!.to.offsetY, once.offsetY, "same anchor y");
}

// 连滚时接着走：新的一段从眼下看到的视口出发，出发那一刻画面不跳。
{
  const first = accumulateWheelZoom(null, identityViewport, width, height, pointer.x, pointer.y, -1, 0, 100);
  const midway = wheelZoomAt(first, 40, (progress) => progress).viewport;
  assert.ok(midway.scale > 1 && midway.scale < first.to.scale, "halfway through the first notch");
  const second = accumulateWheelZoom(first, midway, width, height, pointer.x, pointer.y, -1, 40, 100);
  assert.deepEqual(second.from, midway);
  assert.deepEqual(wheelZoomAt(second, 40, (progress) => progress).viewport, midway, "no jump when the next notch arrives");
  close(second.to.scale, 1.44, "the target keeps accumulating on the previous target");
  const end = wheelZoomAt(second, 140, (progress) => progress);
  assert.equal(end.done, true);
  assert.deepEqual(end.viewport, second.to);
}

// 一路上指针下那一点不动（和一次到位的 zoomAt 同一个锚），缓动曲线只改快慢。
{
  const zoom = accumulateWheelZoom(null, identityViewport, width, height, pointer.x, pointer.y, -3, 0, 100);
  const anchor = pixelsToUnit(identityViewport, width, height, pointer.x, pointer.y);
  for (const t of [10, 30, 50, 80]) {
    const frame = wheelZoomAt(zoom, t, (progress) => progress * progress).viewport;
    const under = pixelsToUnit(frame, width, height, pointer.x, pointer.y);
    close(under.x, anchor.x, `anchor x at ${t} ms`);
    close(under.y, anchor.y, `anchor y at ${t} ms`);
  }
}

// 时长 0（无动画、窗口缩放中）直接到目标。缩放上下限不变：缩小不会小于整图，放大不超过 4096 倍。
{
  const instant = accumulateWheelZoom(null, identityViewport, width, height, pointer.x, pointer.y, -1, 0, 0);
  assert.deepEqual(wheelZoomAt(instant, 0, (progress) => progress), { viewport: instant.to, done: true });
  const out = accumulateWheelZoom(null, identityViewport, width, height, pointer.x, pointer.y, 5, 0, 100);
  assert.equal(out.to, identityViewport, "zooming out of the whole map stays at the whole map");
  const far = accumulateWheelZoom(null, identityViewport, width, height, pointer.x, pointer.y, -1000, 0, 100);
  assert.equal(far.to.scale, 4096);
}

console.log("wheel accumulators ok");
