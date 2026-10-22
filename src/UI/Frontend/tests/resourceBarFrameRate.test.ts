import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import {
  normalizeResourceBarFrameRate,
  normalizeResourceBarFrameRateMode,
  parseResourceBarFrameRate,
  resolveResourceBarFrameRate,
  shouldDrawResourceBarFrame,
  type ResourceBarFrameRate
} from "../src/presentation/resourceBarFrameRate.ts";

// 设置值：模式只认 custom，其余都是跟随屏幕；帧率非整数用默认 60，整数夹到 10–240。
assert.equal(normalizeResourceBarFrameRateMode("custom"), "custom");
assert.equal(normalizeResourceBarFrameRateMode("display"), "display");
assert.equal(normalizeResourceBarFrameRateMode(undefined), "display");
assert.equal(normalizeResourceBarFrameRateMode("60"), "display");
assert.equal(normalizeResourceBarFrameRate(144), 144);
assert.equal(normalizeResourceBarFrameRate(5), 10);
assert.equal(normalizeResourceBarFrameRate(1000), 240);
assert.equal(normalizeResourceBarFrameRate(59.5), 60);
assert.equal(normalizeResourceBarFrameRate(Number.NaN), 60);
assert.equal(normalizeResourceBarFrameRate(null), 60);

// 生效值：跟随屏幕时忽略帧率字段；body 上的写法能原样读回。
assert.equal(resolveResourceBarFrameRate(undefined), "display");
assert.equal(resolveResourceBarFrameRate({ resourceBarFrameRateMode: "display", resourceBarFrameRate: 30 }), "display");
assert.equal(resolveResourceBarFrameRate({ resourceBarFrameRateMode: "custom", resourceBarFrameRate: 30 }), 30);
assert.equal(resolveResourceBarFrameRate({ resourceBarFrameRateMode: "custom" }), 60);
for (const value of ["display", 10, 60, 240] as const) {
  assert.equal(parseResourceBarFrameRate(String(value)), value);
}
assert.equal(parseResourceBarFrameRate(undefined), "display");

// 跟随屏幕：每帧都画，与原来逐帧过渡完全一样。
assert.equal(shouldDrawResourceBarFrame("display", null, 0), true);
assert.equal(shouldDrawResourceBarFrame("display", 100, 100.1), true);
// 自选帧率：一段过渡的第一帧照画；之后距上次画至少 1000/帧率 毫秒，允许 1 ms 抖动。
assert.equal(shouldDrawResourceBarFrame(60, null, 5), true);
assert.equal(shouldDrawResourceBarFrame(60, 0, 1000 / 60 - 1), true);
assert.equal(shouldDrawResourceBarFrame(60, 0, 1000 / 60 - 1.01), false);
assert.equal(shouldDrawResourceBarFrame(30, 0, 1000 / 60), false);

// 一段 360 ms 过渡在 180 Hz 屏上（rAF 时间戳交替抖 ±0.3 ms）实际画几帧。
function drawnFrames(frameRate: ResourceBarFrameRate, refreshHz: number) {
  const frameMs = 1000 / refreshHz;
  const drawn: number[] = [];
  let lastDrawnAt: number | null = null;
  for (let index = 1; index * frameMs <= 360; index++) {
    const now = index * frameMs + (index % 2 === 0 ? 0.3 : -0.3);
    if (shouldDrawResourceBarFrame(frameRate, lastDrawnAt, now)) {
      drawn.push(index);
      lastDrawnAt = now;
    }
  }
  return drawn;
}

assert.equal(drawnFrames("display", 180).length, 64, "跟随屏幕在 180 Hz 下每帧都画");
const sixty = drawnFrames(60, 180);
assert.equal(sixty.length, 22, "60 帧在 180 Hz 下每 3 帧画一次");
assert.ok(sixty.every((frame, index) => index === 0 || frame - sixty[index - 1] === 3), `60 帧的间隔应恒为 3 帧：${sixty.join(",")}`);
assert.equal(drawnFrames(240, 180).length, 64, "设定高于屏幕刷新率时每帧都画");
const ten = drawnFrames(10, 180);
assert.equal(ten.length, 4, "10 帧在 360 ms 内画 4 帧");
assert.ok(ten.every((frame, index) => index === 0 || frame - ten[index - 1] === 18));

// 画布只把中间帧交给帧率判断，最后一帧不经过判断，过渡结束时总是画出准确的终态；
// 帧率经 body 的 data-resource-bar-frame-rate 传给画布。
const paintPlane = readFileSync(new URL("../src/features/resourceBreakdown/ResourceSegmentPaintPlane.tsx", import.meta.url), "utf8");
const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");
assert.match(paintPlane, /if \(progress < 1 && !shouldDrawResourceBarFrame\(presentation\.frameRate, drawnAt, now\)\)/);
assert.match(paintPlane, /frameRate: parseResourceBarFrameRate\(document\.body\.dataset\.resourceBarFrameRate\)/);
assert.match(paintPlane, /"data-resource-bar-frame-rate"/);
assert.match(app, /document\.body\.dataset\.resourceBarFrameRate = String\(resolveResourceBarFrameRate\(settings\.settings\(\)\.appearance\)\)/);

console.log("resourceBarFrameRate: ok");
