import assert from "node:assert/strict";
import {
  defaultCurve,
  isMonotonic,
  maximumPoints,
  normalizeCurve,
  percentAt
} from "../src/control/fanCurve.ts";

// 收拾：排序、夹范围、同温度去重。
assert.deepEqual(
  normalizeCurve([
    { temperatureCelsius: 70, percent: 60 },
    { temperatureCelsius: 40, percent: 10 }
  ]),
  [
    { temperatureCelsius: 40, percent: 10 },
    { temperatureCelsius: 70, percent: 60 }
  ],
  "点要按温度排好");

assert.deepEqual(
  normalizeCurve([{ temperatureCelsius: -20, percent: 500 }]),
  [{ temperatureCelsius: 0, percent: 100 }],
  "超范围的夹进来，不丢点也不报错");

assert.deepEqual(
  normalizeCurve([
    { temperatureCelsius: 60, percent: 20 },
    { temperatureCelsius: 60, percent: 80 }
  ]),
  [{ temperatureCelsius: 60, percent: 80 }],
  "同一个温度后写的算数");

// 非数值直接跳过，不让 NaN 进到写入器里。
assert.deepEqual(
  normalizeCurve([
    { temperatureCelsius: Number.NaN, percent: 50 },
    { temperatureCelsius: 50, percent: Number.POSITIVE_INFINITY },
    { temperatureCelsius: 50, percent: 40 }
  ]),
  [{ temperatureCelsius: 50, percent: 40 }]);

// 点数有硬上限。
const many = Array.from({ length: 40 }, (_, index) => ({
  temperatureCelsius: index * 2,
  percent: index
}));
assert.equal(normalizeCurve(many).length, maximumPoints);

// 插值：两点之间线性。
const curve = [
  { temperatureCelsius: 40, percent: 0 },
  { temperatureCelsius: 60, percent: 50 }
];
assert.equal(percentAt(curve, 50), 25, "中点取中值");
assert.equal(percentAt(curve, 40), 0);
assert.equal(percentAt(curve, 60), 50);

// 曲线之外不外推 —— 外推出来的转速没有依据。
assert.equal(percentAt(curve, 10), 0, "低于第一个点就用第一个点");
assert.equal(percentAt(curve, 100), 50, "高于最后一个点就用最后一个点");
assert.equal(percentAt([], 70), 0, "空曲线不吹风");

// 单调性只判断，不自动改：用户可能真想要一段回落。
assert.equal(isMonotonic(defaultCurve()), true);
assert.equal(
  isMonotonic([
    { temperatureCelsius: 40, percent: 60 },
    { temperatureCelsius: 70, percent: 30 }
  ]),
  false,
  "温度升了转速反而降，要能看出来");

// 默认曲线本身要合法。
assert.deepEqual(normalizeCurve(defaultCurve()), defaultCurve());

console.log("fanCurve: ok");
