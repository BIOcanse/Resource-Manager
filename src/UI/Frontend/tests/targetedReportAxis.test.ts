import assert from "node:assert/strict";
import { test } from "node:test";
import { applyByteUnitMode } from "../src/presentation/byteUnits.ts";
import { axisTicks, curveScale, formatTick } from "../src/features/targetedReport/targetedReportAxis.ts";

const tickValues = (largest: number, integer: boolean) => {
  const ticks = axisTicks(largest, integer);
  return Array.from({ length: ticks.count + 1 }, (_, index) => ticks.step * index);
};

test("count axes such as the frame-rate distribution only get integer ticks", () => {
  for (const largest of [0, 1, 2, 3, 5, 7, 13, 99, 1234]) {
    const values = tickValues(largest, true);
    assert.ok(values.every(Number.isInteger), `${largest}: ${values.join(",")}`);
    assert.ok(values.at(-1)! >= largest && values.length <= 5, `${largest}: ${values.join(",")}`);
  }
  assert.deepEqual(tickValues(3, true), [0, 1, 2, 3]);
  assert.deepEqual(tickValues(0, true), [0, 1]);
});

test("value axes use 1, 2, 2.5 or 5 steps instead of quarters of the maximum", () => {
  assert.deepEqual(tickValues(25, false), [0, 10, 20, 30]);
  assert.deepEqual(tickValues(10, false), [0, 2.5, 5, 7.5, 10]);
  assert.deepEqual(tickValues(40, false).map(formatTick), ["0", "10", "20", "30", "40"]);
});

test("a byte curve's axis and card title use one binary unit chosen by the byte formatter", () => {
  applyByteUnitMode("native");
  const mib = 1024 * 1024;
  const memory = curveScale("memory.usage", [{ y: 0.5 * mib }, { y: null }, { y: 1.5 * mib }]);
  assert.deepEqual(memory.axis, { unit: "MiB", divisor: mib, integerTicks: false });
  assert.equal(memory.format(mib), "1.00 MiB");
  // The title uses the axis unit even when the latest value alone would pick another one.
  assert.equal(memory.format(512 * 1024), "0.50 MiB");
  const vram = curveScale("gpu.0.vram", [{ y: 3 * 1024 * mib }]);
  assert.equal(vram.axis.unit, "GiB");
  const network = curveScale("network.receive", [{ y: 2_500_000 }]);
  assert.deepEqual(network.axis, { unit: "MB/s", divisor: 1_000_000, integerTicks: false });
  assert.equal(network.format(2_500_000), "2.50 MB/s");
  applyByteUnitMode("binary");
  assert.equal(curveScale("network.receive", [{ y: 2 * mib }]).axis.unit, "MiB/s");
  applyByteUnitMode("native");
});

test("non-byte curves keep their unit", () => {
  const cpu = curveScale("cpu.usage", [{ y: 40 }]);
  assert.deepEqual(cpu.axis, { unit: "%", divisor: 1, integerTicks: false });
  assert.equal(cpu.format(40), "40.0 %");
  assert.equal(curveScale("cpu.temperature", [{ y: 60 }]).axis.unit, "°C");
});
