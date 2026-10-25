import assert from "node:assert/strict";
import { cubicBezier } from "../src/interactions/protocol/ease.ts";
import { readMotionDurationMs, readMotionEase } from "../src/presentation/motionTokens.ts";

// 脚本画的过渡读样式表的令牌：时长 ms 和 s 都认，曲线解 cubic-bezier；写错了报错，不给默认值。
const values = new Map<string, string>();
(globalThis as { getComputedStyle?: unknown }).getComputedStyle = () => ({
  getPropertyValue: (name: string) => values.get(name) ?? ""
});
const element = {} as Element;

values.set("--motion-bar", " 480ms");
assert.equal(readMotionDurationMs(element, "--motion-bar"), 480);
values.set("--motion-bar", "0.24s");
assert.equal(readMotionDurationMs(element, "--motion-bar"), 240);
values.set("--motion-bar", "0ms");
assert.equal(readMotionDurationMs(element, "--motion-bar"), 0, "window resizing sets the bar duration to 0");

values.set("--ease-bar", "cubic-bezier(0.35, 0, 0.15, 1)");
const ease = readMotionEase(element, "--ease-bar");
const expected = cubicBezier(0.35, 0, 0.15, 1);
for (const progress of [0, 0.25, 0.5, 0.75, 1]) assert.equal(ease(progress), expected(progress));

values.delete("--motion-bar");
assert.throws(() => readMotionDurationMs(element, "--motion-bar"), /--motion-bar is not a duration/);
values.set("--ease-bar", "ease-out");
assert.throws(() => readMotionEase(element, "--ease-bar"), /--ease-bar is not a cubic-bezier/);

console.log("PASS motion tokens");
