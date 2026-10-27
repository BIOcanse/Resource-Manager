import assert from "node:assert/strict";
import { gpuAdapterNames, gpuIndexOfMetric, gpuNameForMetric } from "../src/features/monitor/gpuAdapterNames.ts";
import { controlFanGpuName } from "../src/features/control/controlPresentation.ts";

// 用户 10-27：「前端直接用后端读到的，不过由于有显卡名字小字所以不会错」。编号原样，名字取字段目录里同一设备身份的 gpu_adapter。
// 形状照真实切片 20261027T043049Z：gpu.0 是 NVIDIA（ordinal 0），gpu.1 是 AMD 核显（ordinal 1）；没绑定字段的别名指标 deviceIdentity 为 null，
// scopeKey 照样是适配器的设备身份。
const nvidia = "windows-gpu|PCI\\VEN_10DE&DEV_2D19";
const amd = "windows-gpu|PCI\\VEN_1002&DEV_164E";
const metrics = [
  { id: "gpu.0.usage", scopeKind: "gpu", scopeKey: nvidia },
  { id: "gpu.0.boardPower", scopeKind: "gpu", scopeKey: nvidia },
  { id: "gpu.1.usage", scopeKind: "gpu", scopeKey: amd },
  { id: "gpu.1.vram", scopeKind: "gpu", scopeKey: amd },
  // 设备不在字段目录里：只写编号。
  { id: "gpu.2.usage", scopeKind: "gpu", scopeKey: "absent" },
  // 不是按编号的显卡指标，或没有 gpu 范围：不认名字。
  { id: "cpu.usage", scopeKind: "cpu", scopeKey: "static|cpu" },
  { id: "gpu.3.usage", scopeKind: null, scopeKey: null }
];
const devices = [
  { deviceIdentity: amd, kind: "gpu_adapter", displayName: "AMD Radeon(TM) 610M" },
  { deviceIdentity: nvidia, kind: "gpu_adapter", displayName: " NVIDIA GeForce RTX 5060 Laptop GPU " },
  { deviceIdentity: "static|cpu", kind: "cpu_package", displayName: "AMD Ryzen 9 8940HX" }
];
const names = gpuAdapterNames(metrics, devices);
assert.deepEqual([...names].sort(), [["0", "NVIDIA GeForce RTX 5060 Laptop GPU"], ["1", "AMD Radeon(TM) 610M"]]);
assert.equal(gpuNameForMetric("gpu.1.vram", names), "AMD Radeon(TM) 610M");
assert.equal(gpuNameForMetric("gpu.2.usage", names), null, "No name from the backend: the number alone");
assert.equal(gpuNameForMetric("cpu.usage", names), null);
assert.equal(gpuIndexOfMetric("gpu.10.usage"), "10");
assert.equal(gpuIndexOfMetric("gpu_all_usage"), null);
assert.equal(gpuAdapterNames(metrics, null).size, 0, "Without the field catalog nothing is named");
// 设备没报名字、种类不是 gpu_adapter：不起名字。
assert.equal(gpuAdapterNames(metrics, [{ deviceIdentity: amd, kind: "gpu_adapter", displayName: null },
  { deviceIdentity: nvidia, kind: "gpu_engine", displayName: "engine" }]).size, 0);
// 同一编号对上两个不同的名字：不写，免得写错。
assert.equal(gpuAdapterNames([{ id: "gpu.0.usage", scopeKind: "gpu", scopeKey: nvidia }, { id: "gpu.0.vram", scopeKind: "gpu", scopeKey: amd }], devices).has("0"), false);

// 控制页的显卡风扇控制器：名字取同一份对象目录里同编号的显卡对象自报的名字。
const objects = [
  { kind: "gpu", reportedName: "NVIDIA GeForce RTX 5060 Laptop GPU", adapterIndex: 0 },
  { kind: "gpu", reportedName: "AMD Radeon(TM) 610M", adapterIndex: 1 },
  { kind: "fan", reportedName: null, adapterIndex: 0 },
  { kind: "fan", reportedName: null, adapterIndex: 5 }
];
assert.equal(controlFanGpuName(objects[2], objects), "NVIDIA GeForce RTX 5060 Laptop GPU");
assert.equal(controlFanGpuName(objects[3], objects), null, "No GPU object with that number: the number alone");
assert.equal(controlFanGpuName(objects[0], objects), null, "A GPU object names itself");
assert.equal(controlFanGpuName({ kind: "fan", reportedName: "Fan A", adapterIndex: 0 }, objects), null);
console.log("GPU names next to backend GPU numbers passed");
