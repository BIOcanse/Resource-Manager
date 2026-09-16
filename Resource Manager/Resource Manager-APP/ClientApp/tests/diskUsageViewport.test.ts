import assert from "node:assert/strict";
import {
  clampToBounds,
  edgePanDelta,
  identityViewport,
  needsMoreDetail,
  panByPixels,
  pixelsToUnit,
  unitToPixels,
  visibleUnitRect,
  zoomAt,
  type DiskUsageViewWindow
} from "../src/diskUsage/diskUsageViewport.ts";
import { LabelOccupancy } from "../src/diskUsage/diskUsageTreemapPaint.ts";

const width = 1000;
const height = 500;

// 起始视口就是整张图铺满画布。
assert.deepEqual(identityViewport, { scale: 1, offsetX: 0, offsetY: 0 });
assert.deepEqual(
  unitToPixels(identityViewport, width, height, 1, 1),
  { x: width, y: height });

// 缩放要以指针为锚：锚点原来对着图上的哪一点，缩放后还对着那一点。
const pointerX = 250;
const pointerY = 100;
const beforeUnit = pixelsToUnit(identityViewport, width, height, pointerX, pointerY);
const zoomed = zoomAt(identityViewport, width, height, pointerX, pointerY, 2);
const afterUnit = pixelsToUnit(zoomed, width, height, pointerX, pointerY);
assert.equal(zoomed.scale, 2);
assert.ok(Math.abs(beforeUnit.x - afterUnit.x) < 1e-6, "锚点横向不能漂");
assert.ok(Math.abs(beforeUnit.y - afterUnit.y) < 1e-6, "锚点纵向不能漂");

// 缩放倍数有下限：1 表示整图铺满，再缩就没意义了。
assert.equal(zoomAt(identityViewport, width, height, 0, 0, 0.5).scale, 1);

// 图可以被推出画布，但推不到找不回来：两个方向各留一整屏余量。
const panned = panByPixels(zoomed, width, height, -10_000, -10_000);
const span = 1 / zoomed.scale;
const upperBound = Math.max(0, 1 - span) + span;
assert.ok(panned.offsetX <= upperBound + 1e-6, "不能推到图外一屏以上");
assert.ok(panned.offsetY <= upperBound + 1e-6);
const pulled = panByPixels(zoomed, width, height, 10_000, 10_000);
assert.ok(pulled.offsetX >= -span - 1e-6, "另一个方向同样有上限");

// 不再"自动归位"：缩放为 1 时也能把图整个推开。
const nudged = panByPixels(identityViewport, width, height, 500, 500);
assert.notDeepEqual(nudged, identityViewport, "缩放为 1 时也要能移动");
assert.ok(nudged.offsetX > 0, "视口跟着移出去了");
assert.ok(nudged.offsetX <= 1 + 1e-6, "但最多推出一屏");

// 推出去之后，看得见的那块要和图取交集，不能报一块图外的空范围。
const pushedOut = clampToBounds({ scale: 1, offsetX: -0.9, offsetY: -0.9 });
const outRect = visibleUnitRect(pushedOut);
assert.ok(outRect.minX >= 0 && outRect.minY >= 0);
assert.ok(outRect.maxX <= 1 && outRect.maxY <= 1);

// 贴边才平移，越贴近边缘越快；中间不动。
assert.deepEqual(
  edgePanDelta(width / 2, height / 2, width, height),
  { deltaX: 0, deltaY: 0 });
const nearLeft = edgePanDelta(4, height / 2, width, height);
assert.ok(nearLeft.deltaX > 0, "贴左边要把视图往右推");
assert.equal(nearLeft.deltaY, 0);
const nearRight = edgePanDelta(width - 4, height / 2, width, height);
assert.ok(nearRight.deltaX < 0, "贴右边要把视图往左推");
const nearerLeft = edgePanDelta(1, height / 2, width, height);
assert.ok(nearerLeft.deltaX > nearLeft.deltaX, "越贴近边缘越快");

// clampToBounds 幂等：夹过一次的视口再夹还是它自己。
const clamped = clampToBounds({ scale: 4, offsetX: 9, offsetY: -3 });
assert.deepEqual(clampToBounds(clamped), clamped);
// 缩放 4 时一屏是 0.25，所以上界是 0.75 再加一屏余量，下界是负一屏。
assert.equal(clamped.offsetX, 1);
assert.equal(clamped.offsetY, -0.25);

// 名字占位：子方格的名字在父方格里面，撞上了就不画，
// 否则父方格的名字会被盖掉中间一截（"Windows Kits" → "10 ndows Kits"）。
const occupancy = new LabelOccupancy(400, 200);
assert.equal(occupancy.tryReserve(10, 10, 120, 15), true, "第一个名字总能画");
assert.equal(occupancy.tryReserve(40, 12, 30, 15), false, "压在上面的名字不画");
assert.equal(occupancy.tryReserve(10, 10, 120, 15), false, "同一个位置不能占两次");
assert.equal(occupancy.tryReserve(200, 100, 80, 15), true, "错开的名字照画");
// 画布外的坐标不能越界写，也不能把不相干的位置误判成已占用。
assert.equal(occupancy.tryReserve(-50, -50, 20, 15), true);
assert.equal(occupancy.tryReserve(9_000, 9_000, 20, 15), true);

// 看得见的那块：缩放 S 时正好是 1/S 见方，左上角就是平移量。
assert.deepEqual(
  visibleUnitRect(identityViewport),
  { minX: 0, minY: 0, maxX: 1, maxY: 1 });
assert.deepEqual(
  visibleUnitRect({ scale: 4, offsetX: 0.25, offsetY: 0.5 }),
  { minX: 0.25, minY: 0.5, maxX: 0.5, maxY: 0.75 });
// 不管平移到哪儿都不会报出图外的范围。
const edgeRect = visibleUnitRect({ scale: 2, offsetX: 0.9, offsetY: 0.9 });
assert.ok(edgeRect.maxX <= 1 && edgeRect.maxY <= 1);

// 要不要换一份更细的布局。
const shown: DiskUsageViewWindow = {
  pixelWidth: 1600, pixelHeight: 1000, scale: 1,
  minX: 0, minY: 0, maxX: 1, maxY: 1
};
assert.equal(needsMoreDetail(shown, shown), false, "没变就不该重新要");
assert.equal(
  needsMoreDetail(shown, { ...shown, scale: 1.1 }),
  false,
  "只动一点点不值得跑一趟");
assert.equal(
  needsMoreDetail(shown, { ...shown, scale: 4 }),
  true,
  "放大了就该把原先太小的方格要下来");
// 窗口变大或屏幕像素变多，同样意味着能看见更多。
assert.equal(
  needsMoreDetail(shown, { ...shown, pixelWidth: 3840, pixelHeight: 2160 }),
  true,
  "画布变大也要更细");
// 缩小回去用手上这份就够了，它覆盖的范围更广。
assert.equal(
  needsMoreDetail({ ...shown, scale: 4, minX: 0.2, maxX: 0.45, minY: 0.2, maxY: 0.45 },
    { ...shown, scale: 2, minX: 0.25, maxX: 0.4, minY: 0.25, maxY: 0.4 }),
  false,
  "缩小不用重新要");
// 移到原来没发过的地方就得要。
assert.equal(
  needsMoreDetail(
    { ...shown, scale: 4, minX: 0.2, maxX: 0.45, minY: 0.2, maxY: 0.45 },
    { ...shown, scale: 4, minX: 0.5, maxX: 0.75, minY: 0.2, maxY: 0.45 }),
  true,
  "移出原来那块就要重新要");

console.log("diskUsageViewport: ok");
