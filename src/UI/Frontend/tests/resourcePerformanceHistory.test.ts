import assert from "node:assert/strict";
import {
  appendPerformancePoint,
  appendPerformanceHistory,
  buildPerformanceRuns,
  type PerformanceSeries
} from "../src/features/resourceTable/resourcePerformanceHistory.ts";

const series: readonly PerformanceSeries[] = Object.freeze([{
  id: "cpu.usage",
  label: "CPU",
  color: "#000000"
}]);

const inputs = [25, null, 0, 30] as const;
const history = inputs.map((numericValue, index) => appendPerformancePoint(
  snapshot(`2026-08-22T12:00:0${index}.000Z`, numericValue),
  series));

let accumulated = history.reduce((previous, point) => appendPerformanceHistory(previous, point), [] as readonly typeof history[number][]);
assert.deepEqual(accumulated, history);
assert.equal(appendPerformanceHistory(accumulated, history.at(-1)!).length, 4, "a repeated observation must not add another point");
assert.deepEqual(appendPerformanceHistory(accumulated, clampedPoint(), 3).map(p => p.values["cpu.usage"]), [0, 30, 80]);
function clampedPoint() { return appendPerformancePoint(snapshot("2026-08-22T12:02:00.000Z", 80), series); }

assert.deepEqual(
  buildPerformanceRuns(history, "cpu.usage"),
  [
    [{ index: 0, value: 25 }],
    [{ index: 2, value: 0 }, { index: 3, value: 30 }]
  ]);
assert.equal(history[1].displays["cpu.usage"], "N/A");
assert.equal(history[2].displays["cpu.usage"], "0%");

for (const numericValue of [undefined, Number.NaN, Number.POSITIVE_INFINITY]) {
  const point = appendPerformancePoint(
    snapshot("2026-08-22T12:01:00.000Z", numericValue, "invalid"),
    series);
  assert.equal(point.values["cpu.usage"], null);
  assert.equal(point.displays["cpu.usage"], "invalid");
}

const clamped = appendPerformancePoint(
  snapshot("2026-08-22T12:02:00.000Z", 125, "125%"),
  series);
assert.equal(clamped.values["cpu.usage"], 100);

const currentEmpty = appendPerformancePoint(
  snapshot(null, null, "-"),
  series);
assert.equal(currentEmpty.capturedAt, null);
assert.equal(currentEmpty.displays["cpu.usage"], "-");

function snapshot(
  capturedAt: string | null,
  numericValue: number | null | undefined,
  displayValue = numericValue === null ? "N/A" : `${numericValue}%`
) {
  return {
    version: 5 as const,
    capturedAt,
    catalogRevision: "catalog-a",
    items: { "cpu.usage": { numericValue, displayValue } }
  };
}
