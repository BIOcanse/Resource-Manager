import assert from "node:assert/strict";
import {
  clampToBounds,
  edgePanDelta,
  identityViewport,
  panByPixels,
  pixelsToUnit,
  unitToPixels,
  zoomAt
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

// 平移不许把图划出画布。
const panned = panByPixels(zoomed, width, height, -10_000, -10_000);
const maximumOffset = 1 - 1 / zoomed.scale;
assert.ok(panned.offsetX <= maximumOffset + 1e-6);
assert.ok(panned.offsetY <= maximumOffset + 1e-6);
assert.ok(panned.offsetX >= 0 && panned.offsetY >= 0);

// 放大之前没有可平移的余地，怎么拖都还是原样。
assert.deepEqual(
  panByPixels(identityViewport, width, height, 500, 500),
  identityViewport);

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
assert.equal(clamped.offsetX, 0.75);
assert.equal(clamped.offsetY, 0);

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

console.log("diskUsageViewport: ok");
