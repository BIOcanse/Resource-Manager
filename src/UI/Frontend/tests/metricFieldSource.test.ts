import assert from "node:assert/strict";
import {
  buildMetricFieldSubscriptionUrl,
  findMetricField,
  metricFieldDevicesDecoder
} from "../src/data/metricFields/metricFieldSource.ts";
import { effectiveFrequencyRows, numaNodeRows } from "../src/features/details/cpuMetricFields.ts";
import { applyByteUnitMode } from "../src/presentation/byteUnits.ts";
import { deviceMetricSections } from "../src/features/deviceTopology/deviceMetricFields.ts";

const current = 1;
const unavailable = 3;
const value = (name: string, unit: string, valueKind: string, extra: Record<string, unknown>) =>
  ({ name, unit, valueKind, numericValue: null, integerValue: null, status: current, observedAt: "2026-10-22T00:00:00Z", sourceId: "s", ...extra });

// 订阅只按字段，去重排序，不带 device。
assert.equal(
  buildMetricFieldSubscriptionUrl({ fields: ["b", "a", "b", " "] }, 999.6),
  "/api/metrics/subscribe?fields=a%2Cb&intervalMs=1000");

const devices = metricFieldDevicesDecoder.decode([
  { deviceIdentity: "numa|1", device: { kind: "numa_node", address: { kind: "numa_node", nodeNumber: 1 }, displayName: null, ordinal: 0 }, fields: [
    value("memory_all_numa_node_capacity_mib", "MiB", "unsigned64", { integerValue: "0" }),
    value("memory_all_numa_node_available_bytes", "B", "unsigned64", { integerValue: "9007199254740993" })
  ] },
  { deviceIdentity: "numa|0", device: { kind: "numa_node", address: { kind: "numa_node", nodeNumber: 0 }, displayName: null, ordinal: 1 }, fields: [
    value("memory_all_numa_node_capacity_mib", "MiB", "unsigned64", { integerValue: "32768" }),
    value("memory_all_numa_node_available_bytes", "B", "unsigned64", { integerValue: "5", status: unavailable })
  ] },
  { deviceIdentity: "thread|a", device: { kind: "cpu_logical_processor", address: { kind: "cpu_logical_processor", processorGroup: 0, groupRelativeIndex: 4 }, displayName: null, ordinal: 0 }, fields: [
    value("cpu_intel_effective_frequency_mhz", "MHz", "float64", { numericValue: 4812.4 })
  ] },
  { deviceIdentity: "thread|b", device: { kind: "cpu_logical_processor", address: { kind: "cpu_logical_processor", processorGroup: 0, groupRelativeIndex: 5 }, displayName: null, ordinal: 1 }, fields: [
    value("cpu_intel_effective_frequency_mhz", "MHz", "float64", { numericValue: 100, status: unavailable })
  ] },
  { deviceIdentity: "future|x", device: { kind: "something-new", address: { kind: "something-new", id: 1 }, displayName: null, ordinal: 0 }, fields: [] },
  { deviceIdentity: "plain", device: { kind: "platform", address: null, displayName: null, ordinal: 0 }, fields: [] }
]);

// unsigned64 精确保留；非 current 一律不可用，不当 0。
assert.equal(devices[0].fields[1].integerValue, 9007199254740993n);
assert.equal(devices[1].fields[1].integerValue, null);
assert.equal(devices[3].fields[0].numericValue, null);
assert.equal(devices[3].fields[0].observedAt, null);
// 还没有消费者的地址种类当作没有地址。
assert.equal(devices[4].address, null);
assert.equal(devices[5].address, null);
assert.throws(() => metricFieldDevicesDecoder.decode([
  { deviceIdentity: "x", device: { kind: "platform", address: null, displayName: null, ordinal: 1 }, fields: [value("f", "B", "unsigned64", { integerValue: "1.5" })] }
]), /integerValue/);

applyByteUnitMode("binary");
const numa = numaNodeRows(devices);
assert.deepEqual(numa.map((row) => row.nodeNumber), [0, 1], "NUMA nodes are ordered by node number");
assert.equal(numa[0].capacity, "32.0 GiB");
assert.equal(numa[0].available, null, "bound but not current is unavailable");
assert.equal(numa[1].capacity, "0 B", "zero capacity is a valid value");
assert.equal(numaNodeRows([{ deviceIdentity: "numa|2", kind: "numa_node", address: { kind: "numa_node", nodeNumber: 2 }, displayName: null, ordinal: 2, fields: [] }])[0].capacity,
  undefined, "an unbound field is not shown");

const logical = (id: number, groupRelativeIndex: number) => ({ id, processorGroup: 0, groupRelativeIndex }) as never;
assert.deepEqual(effectiveFrequencyRows(devices, [logical(4, 4), logical(5, 5), logical(6, 6)]), [
  { logicalProcessorId: 4, frequency: "4812 MHz" },
  { logicalProcessorId: 5, frequency: null }
], "unbound logical processors are left out, unavailable ones stay as null");
assert.deepEqual(effectiveFrequencyRows([], [logical(4, 4)]), [], "no binding on this machine hides the row");
assert.equal(findMetricField(devices, (address) => address.kind === "npu", "x"), null);

// 设备页：NPU 的 OS 忙碌率不截断；AMD 报告值单独一节；只列绑定了的字段。
const pnp = "PCI\\VEN_1022&DEV_1502\\4&1";
const gpuPnp = "PCI\\VEN_1002&DEV_150E\\4&2";
const deviceFields = metricFieldDevicesDecoder.decode([
  { deviceIdentity: "npu|x", device: { kind: "npu", address: { kind: "npu", pnpInstanceId: pnp }, displayName: null, ordinal: 0 }, fields: [
    value("npu_all_engine_busy_percent", "%", "float64", { numericValue: 137.54 }),
    value("npu_all_dedicated_memory_committed", "B", "unsigned64", { integerValue: "1048576" }),
    value("npu_all_shared_memory_resident", "B", "unsigned64", { integerValue: "7", status: unavailable })
  ] },
  { deviceIdentity: "gpu|y", device: { kind: "gpu_adapter", address: { kind: "gpu_adapter", pnpInstanceId: gpuPnp }, displayName: null, ordinal: 0 }, fields: [
    value("gpu_amd_npu_activity_percent", "%", "float64", { numericValue: 0 }),
    value("gpu_amd_npu_frequency_mhz", "MHz", "float64", { numericValue: 1800.4 })
  ] },
  { deviceIdentity: "gpu|z", device: { kind: "gpu_adapter", address: { kind: "gpu_adapter", pnpInstanceId: "PCI\\OTHER" }, displayName: null, ordinal: 1 }, fields: [] }
]);
const npuSections = deviceMetricSections(deviceFields, pnp);
assert.equal(npuSections.length, 1);
assert.deepEqual(npuSections[0].rows.map((row) => row.value), ["137.5%", "1.00 MiB", "不可用"],
  "busy above 100% is kept, unbound memory fields are left out, unavailable is not zero");
assert.ok(npuSections[0].rows[0].hint?.includes("任务管理器"));
const gpuSections = deviceMetricSections(deviceFields, gpuPnp);
assert.equal(gpuSections.length, 1);
assert.equal(gpuSections[0].title, "AMD 报告的 NPU 数值");
assert.deepEqual(gpuSections[0].rows.map((row) => row.value), ["0.0%", "1800 MHz"], "a vendor zero is a valid value");
assert.deepEqual(deviceMetricSections(deviceFields, "PCI\\OTHER"), [], "a device with no bound fields shows nothing");
assert.deepEqual(deviceMetricSections(deviceFields, pnp.toLowerCase()), [], "matching is exact equality with the topology deviceId");

console.log("metricFieldSource: ok");
