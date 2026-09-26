import assert from "node:assert/strict";
import {
  isUnavailableStartupGpuTarget,
  startupTargetGpuOptionsForSoftwarePolicy,
  targetGpuOptionsForSoftwarePolicy
} from "../src/components/gpuPlacementTargetOptions.ts";

const runtimeOptions = targetGpuOptionsForSoftwarePolicy(true, "Precise", "Precise", "SystemDefaultGpu");
assert.equal(runtimeOptions.some(([value, , disabled]) => value === "AutoIdleGpu" && disabled !== true), true);
assert.equal(runtimeOptions.some(([value]) => /^GPU\d+$/.test(value)), false);

const inventory = [
  { targetGpu: "GPU0", displayName: "GPU 0 · Integrated", available: true },
  { targetGpu: "GPU2", displayName: "GPU2（不可用）", available: false, reason: "设备已移除" }
] as const;
const inventoryOptions = targetGpuOptionsForSoftwarePolicy(
  true,
  "Precise",
  "Precise",
  "GPU2",
  inventory);
assert.deepEqual(inventoryOptions.find(([value]) => value === "GPU0"), ["GPU0", "GPU 0 · Integrated", false]);
assert.deepEqual(
  inventoryOptions.find(([value]) => value === "GPU2"),
  ["GPU2", "GPU2（不可用） · 设备已移除", true]);
assert.equal(inventoryOptions.some(([value]) => value === "GPU1" || value === "GPU3"), false);

const startupOptions = startupTargetGpuOptionsForSoftwarePolicy(true, "Precise", "SystemDefaultGpu");
assert.equal(startupOptions.some(([value]) => value === "AutoIdleGpu"), false);

const legacyStartupOptions = startupTargetGpuOptionsForSoftwarePolicy(true, "Precise", "AutoIdleGpu");
assert.deepEqual(legacyStartupOptions[0], ["AutoIdleGpu", "自动选择空闲显卡（启动期暂不可用）", true]);
assert.equal(legacyStartupOptions.filter(([value]) => value === "AutoIdleGpu").length, 1);

const legacyOrdinaryStartupOptions = startupTargetGpuOptionsForSoftwarePolicy(false, "Ordinary", "AutoIdleGpu");
assert.deepEqual(legacyOrdinaryStartupOptions[0], ["AutoIdleGpu", "自动选择空闲显卡（启动期暂不可用）", true]);
assert.equal(legacyOrdinaryStartupOptions.filter(([value]) => value === "AutoIdleGpu").length, 1);
assert.equal(isUnavailableStartupGpuTarget("AutoIdleGpu"), true);
assert.equal(isUnavailableStartupGpuTarget("GPU0"), false);

const missingSavedTarget = startupTargetGpuOptionsForSoftwarePolicy(true, "Precise", "GPU7", inventory);
assert.deepEqual(missingSavedTarget[0], ["GPU7", "GPU7（当前不可用）", true]);
assert.equal(missingSavedTarget.filter(([value]) => value === "GPU7").length, 1);
