import assert from "node:assert/strict";
import {
  isUnavailableStartupGpuTarget,
  startupTargetGpuOptionsForSoftwarePolicy
} from "../src/components/gpuPlacementTargetOptions.ts";

const inventory = [
  { targetGpu: "gpu0", adapterName: "Integrated", available: true },
  { targetGpu: "gpu2", adapterName: null, available: false, reasonCode: "target-missing" }
] as const;
const inventoryOptions = startupTargetGpuOptionsForSoftwarePolicy(
  true,
  "gpu2",
  inventory);
assert.deepEqual(inventoryOptions.find(([value]) => value === "gpu0"), ["gpu0", "GPU0 · Integrated", false]);
assert.deepEqual(
  inventoryOptions.find(([value]) => value === "gpu2"),
  ["gpu2", "GPU2 · 已保存 GPU 当前不在完整设备清单中。", true]);
assert.equal(inventoryOptions.some(([value]) => value === "gpu1" || value === "gpu3"), false);

const startupOptions = startupTargetGpuOptionsForSoftwarePolicy(true, "system_default_gpu");
assert.equal(startupOptions.some(([value]) => value === "auto_idle_gpu"), false);

const legacyStartupOptions = startupTargetGpuOptionsForSoftwarePolicy(true, "auto_idle_gpu");
assert.deepEqual(legacyStartupOptions[0], ["auto_idle_gpu", "自动选择空闲显卡（启动期暂不可用）", true]);
assert.equal(legacyStartupOptions.filter(([value]) => value === "auto_idle_gpu").length, 1);

const legacyOrdinaryStartupOptions = startupTargetGpuOptionsForSoftwarePolicy(false, "auto_idle_gpu");
assert.deepEqual(legacyOrdinaryStartupOptions[0], ["auto_idle_gpu", "自动选择空闲显卡（启动期暂不可用）", true]);
assert.equal(legacyOrdinaryStartupOptions.filter(([value]) => value === "auto_idle_gpu").length, 1);
assert.equal(isUnavailableStartupGpuTarget("auto_idle_gpu"), true);
assert.equal(isUnavailableStartupGpuTarget("gpu0"), false);

const missingSavedTarget = startupTargetGpuOptionsForSoftwarePolicy(true, "gpu7", inventory);
assert.deepEqual(missingSavedTarget[0], ["gpu7", "GPU7（当前不可用）", true]);
assert.equal(missingSavedTarget.filter(([value]) => value === "gpu7").length, 1);

// 精确选卡不可用（未开启精确调度或启动期能力缺失）时只给系统级目标，已存的具体显卡标为需要精确选择且不可选。
const ordinaryOptions = startupTargetGpuOptionsForSoftwarePolicy(false, "system_default_gpu", inventory);
assert.deepEqual(ordinaryOptions.map(([value]) => value), ["system_default_gpu", "high_performance_gpu", "integrated_gpu"]);
const ordinarySavedExact = startupTargetGpuOptionsForSoftwarePolicy(false, "gpu0", inventory);
assert.equal(ordinarySavedExact[0][0], "gpu0");
assert.equal(ordinarySavedExact[0][2], true);
assert.equal(ordinarySavedExact.some(([value]) => value === "gpu2"), false);
