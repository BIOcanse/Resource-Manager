import assert from "node:assert/strict";
import {
  isUnavailableStartupGpuTarget,
  startupTargetGpuOptionsForSoftwarePolicy
} from "../src/components/gpuPlacementTargetOptions.ts";

const inventory = [
  { targetGpu: "GPU0", displayName: "GPU 0 · Integrated", available: true },
  { targetGpu: "GPU2", displayName: "GPU2（不可用）", available: false, reason: "设备已移除" }
] as const;
const inventoryOptions = startupTargetGpuOptionsForSoftwarePolicy(
  true,
  "GPU2",
  inventory);
assert.deepEqual(inventoryOptions.find(([value]) => value === "GPU0"), ["GPU0", "GPU 0 · Integrated", false]);
assert.deepEqual(
  inventoryOptions.find(([value]) => value === "GPU2"),
  ["GPU2", "GPU2（不可用） · 设备已移除", true]);
assert.equal(inventoryOptions.some(([value]) => value === "GPU1" || value === "GPU3"), false);

const startupOptions = startupTargetGpuOptionsForSoftwarePolicy(true, "SystemDefaultGpu");
assert.equal(startupOptions.some(([value]) => value === "AutoIdleGpu"), false);

const legacyStartupOptions = startupTargetGpuOptionsForSoftwarePolicy(true, "AutoIdleGpu");
assert.deepEqual(legacyStartupOptions[0], ["AutoIdleGpu", "自动选择空闲显卡（启动期暂不可用）", true]);
assert.equal(legacyStartupOptions.filter(([value]) => value === "AutoIdleGpu").length, 1);

const legacyOrdinaryStartupOptions = startupTargetGpuOptionsForSoftwarePolicy(false, "AutoIdleGpu");
assert.deepEqual(legacyOrdinaryStartupOptions[0], ["AutoIdleGpu", "自动选择空闲显卡（启动期暂不可用）", true]);
assert.equal(legacyOrdinaryStartupOptions.filter(([value]) => value === "AutoIdleGpu").length, 1);
assert.equal(isUnavailableStartupGpuTarget("AutoIdleGpu"), true);
assert.equal(isUnavailableStartupGpuTarget("GPU0"), false);

const missingSavedTarget = startupTargetGpuOptionsForSoftwarePolicy(true, "GPU7", inventory);
assert.deepEqual(missingSavedTarget[0], ["GPU7", "GPU7（当前不可用）", true]);
assert.equal(missingSavedTarget.filter(([value]) => value === "GPU7").length, 1);

// 精确选卡不可用（未开启精确调度或启动期能力缺失）时只给系统级目标，已存的具体显卡标为需要精确选择且不可选。
const ordinaryOptions = startupTargetGpuOptionsForSoftwarePolicy(false, "SystemDefaultGpu", inventory);
assert.deepEqual(ordinaryOptions.map(([value]) => value), ["SystemDefaultGpu", "HighPerformanceGpu", "IntegratedGpu"]);
const ordinarySavedExact = startupTargetGpuOptionsForSoftwarePolicy(false, "GPU0", inventory);
assert.equal(ordinarySavedExact[0][0], "GPU0");
assert.equal(ordinarySavedExact[0][2], true);
assert.equal(ordinarySavedExact.some(([value]) => value === "GPU2"), false);
