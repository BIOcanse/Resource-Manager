import assert from "node:assert/strict";
import {
  controlItemStatusOf,
  hasPendingChanges
} from "../src/features/control/controlStateMachine.ts";
import type { ControlApplyOutcome, ControlSetting } from "../src/features/control/controlTypes.ts";

const capability = "gpu.power-limit";
const setting = (value: number): ControlSetting => ({ capabilityId: capability, number: value });
const outcome = (
  status: ControlApplyOutcome["status"],
  message: string | null = null
): ControlApplyOutcome => ({ objectId: "gpu:1", capabilityId: capability, status, message });

// 没设过就是没设过，不该显示成任何"应用"状态。
assert.equal(controlItemStatusOf(capability, [], [], [], false).status, "unset");

// 改了还没提交。
assert.equal(
  controlItemStatusOf(capability, [setting(120)], [], [], false).status,
  "edited");

// 提交中：本地改动和提交标志都在。
assert.equal(
  controlItemStatusOf(capability, [setting(120)], [], [], true).status,
  "applying");

// 存下来了、回执说写进去了 —— 这才是已应用。
assert.equal(
  controlItemStatusOf(capability, [setting(120)], [setting(120)], [outcome("applied")], false)
    .status,
  "applied");

// 存下来了但这一轮回执里没有它：还没施加过，不能报成已应用。
assert.equal(
  controlItemStatusOf(capability, [setting(120)], [setting(120)], [], false).status,
  "applying");

// 控不了要如实说，并且带着原因。
const unsupported = controlItemStatusOf(
  capability,
  [setting(120)],
  [setting(120)],
  [outcome("unsupported", "需要 NVIDIA NVAPI Provider")],
  false);
assert.equal(unsupported.status, "unsupported");
assert.equal(unsupported.message, "需要 NVIDIA NVAPI Provider");

// 本地改动优先于回执：刚拖完滑块还没提交时，上一次回执说的是旧值的事。
assert.equal(
  controlItemStatusOf(capability, [setting(140)], [setting(120)], [outcome("applied")], false)
    .status,
  "edited",
  "改了新值之后不能还显示成已应用");

// 有没有要提交的。
assert.equal(hasPendingChanges([], []), false);
assert.equal(hasPendingChanges([setting(120)], [setting(120)]), false);
assert.equal(hasPendingChanges([setting(140)], [setting(120)]), true);
// 删掉一项也算改动 —— 否则"取消设定"提交不上去。
assert.equal(hasPendingChanges([], [setting(120)]), true);

// 曲线按点比，不按引用比。
const curve = (percent: number): ControlSetting => ({
  capabilityId: "fan.curve",
  curve: [{ temperatureCelsius: 60, percent }]
});
assert.equal(hasPendingChanges([curve(40)], [curve(40)]), false);
assert.equal(hasPendingChanges([curve(50)], [curve(40)]), true);

assert.equal(hasPendingChanges([{ ...curve(40), curveExecution: "software" }], [{ ...curve(40), curveExecution: "firmware" }]), true);
assert.equal(hasPendingChanges([{ ...setting(40), unit: "W" }], [{ ...setting(40), unit: "MHz" }]), true);
assert.equal(controlItemStatusOf(capability, [], [setting(40)], [], false).status, "edited");
assert.equal(controlItemStatusOf(capability, [], [], [outcome("failed", "release failed")], false).status, "failed");
console.log("controlStateMachine: ok");
