import assert from "node:assert/strict";
import {
  curveFromStops,
  curveStops,
  defaultStops,
  isMonotonic,
  percentAt,
  stopsFromCurve
} from "../src/features/control/fanCurve.ts";

// 调整点是固定的，每 5 度一个。
assert.equal(curveStops.length, 10);
assert.deepEqual([...curveStops], [40, 45, 50, 55, 60, 65, 70, 75, 80, 85]);
curveStops.forEach((temperature, index) => {
  if (index > 0) {
    assert.equal(temperature - curveStops[index - 1], 5, "相邻调整点必须差 5 度");
  }
});

// 存下去再读回来，值不变。
const stops = defaultStops();
assert.deepEqual(stopsFromCurve(curveFromStops(stops)), stops, "存取要是可逆的");

// 没设过就是全 0，而不是报错或凭空给一条曲线。
assert.deepEqual(stopsFromCurve(null), curveStops.map(() => 0));
assert.deepEqual(stopsFromCurve([]), curveStops.map(() => 0));

// 存的点没落在这组温度上（早先存的、别处导入的）也要能读，按插值取。
// 对不齐就丢掉用户设过的东西是不能接受的。
const legacy = [
  { temperatureCelsius: 40, percent: 0 },
  { temperatureCelsius: 60, percent: 50 },
  { temperatureCelsius: 80, percent: 100 }
];
const fromLegacy = stopsFromCurve(legacy);
assert.equal(fromLegacy[0], 0, "40 度取到 0");
assert.equal(fromLegacy[4], 50, "60 度取到 50");
assert.equal(fromLegacy[2], 25, "50 度插值到 25");
// 插值结果必须是整数：界面上出现 38.33333333333333 这种档位是不能接受的，
// 用户也调不到那个值上去。
fromLegacy.forEach((percent, index) => assert.ok(
  Number.isInteger(percent),
  `第 ${index} 个档位应当是整数，实际是 ${percent}`));
const uneven = stopsFromCurve([
  { temperatureCelsius: 40, percent: 0 },
  { temperatureCelsius: 85, percent: 100 }
]);
uneven.forEach((percent) => assert.ok(Number.isInteger(percent)));

// 超范围的值夹进来。
assert.deepEqual(
  stopsFromCurve([{ temperatureCelsius: 40, percent: 500 }])[0],
  100);

// **曲线必须穿过每一个控制点。** 用户把点拖到哪里，线就得经过哪里 ——
// 否则点和线对不上，看着就像坏了。
const shape = [0, 15, 25, 35, 45, 55, 70, 85, 95, 100];
const shaped = curveFromStops(shape);
curveStops.forEach((temperature, index) => {
  assert.equal(
    Math.round(percentAt(shaped, temperature)),
    shape[index],
    `${temperature} 度上曲线要正好落在控制点 ${shape[index]}`);
});

// 两点之间严格直线插值，不受相邻段斜率影响。
const kink = curveFromStops([0, 0, 0, 100, 100, 100, 100, 100, 100, 100]);
assert.equal(
  Math.round(percentAt(kink, 51.25)),
  25,
  "四分之一点正好取到 25");

// 样条在陡峭处会冲出范围，必须夹回来 —— 转速没有负的，也没有超过满转的。
for (let temperature = 40; temperature <= 85; temperature += 0.5) {
  const value = percentAt(kink, temperature);
  assert.ok(value >= 0 && value <= 100, `${temperature} 度上越界了：${value}`);
}

// 曲线之外不外推。
assert.equal(percentAt(shaped, 10), shape[0]);
assert.equal(percentAt(shaped, 120), shape[9]);
assert.equal(percentAt(null, 70), 0, "没设过就不吹风");

// 单调性只判断，不自动改。
assert.equal(isMonotonic(defaultStops()), true);
assert.equal(
  isMonotonic([0, 80, 70, 60, 50, 40, 30, 20, 10, 0]),
  false,
  "温度升了转速反而降，要能看出来");

console.log("fanCurve: ok");
