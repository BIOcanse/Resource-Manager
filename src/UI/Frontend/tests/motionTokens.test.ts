import assert from "node:assert/strict";
import { cubicBezier } from "../src/interactions/protocol/ease.ts";
import { observeMotionTokens, readMotionDurationMs, readMotionEase } from "../src/presentation/motionTokens.ts";

// 令牌按 body 的计算值读（:root 是桌面常规，body[data-animations] 换风格，data-window-resizing 清零），一份快照，属性变了才再读。
const values = new Map<string, string>([
  ...["fast", "medium", "page", "panel", "bar"].map((name) => [`--motion-${name}`, "480ms"] as [string, string]),
  ...["decel", "standard", "bar"].map((name) => [`--ease-${name}`, "cubic-bezier(0.35, 0, 0.15, 1)"] as [string, string])
]);
const bodyAttributes = new Map<string, string>();
const root = {};
const body = { getAttribute: (name: string) => bodyAttributes.get(name) ?? null };
const document = { documentElement: root, body } as unknown as Document;
const element = { ownerDocument: document } as Element;
const reads: unknown[] = [];
let observer!: FakeObserver;
class FakeObserver {
  pending: unknown[] = [];
  disconnected = false;
  constructor(readonly callback: () => void) { observer = this; }
  observe() {}
  disconnect() { this.disconnected = true; }
  takeRecords() { return this.pending.splice(0); }
}
Object.assign(globalThis, {
  MutationObserver: FakeObserver,
  getComputedStyle: (target: unknown) => {
    assert.ok(target === body, "tokens are read from body only, never from a mounted subtree");
    reads.push(target);
    return { getPropertyValue: (name: string) => bodyAttributes.get("data-window-resizing") === "yes" && name.startsWith("--motion-") ? "0ms" : values.get(name) ?? "" };
  }
});
const dispose = observeMotionTokens(document);
assert.equal(readMotionDurationMs(element, "--motion-bar"), 480);
const ease = readMotionEase(element, "--ease-bar");
const expected = cubicBezier(0.35, 0, 0.15, 1);
for (const progress of [0, 0.25, 0.5, 0.75, 1]) assert.equal(ease(progress), expected(progress));
for (let i = 0; i < 100; i++) { readMotionDurationMs(element, "--motion-bar"); readMotionEase(element, "--ease-bar"); }
assert.deepEqual(reads, [body], "mounts read the snapshot, without repeated CSS queries");
// 换风格：同一任务里属性记录还没投递，读的也是新值。终端顿挫的曲线是 steps(4, end)。
values.set("--motion-bar", "0.24s");
values.set("--ease-decel", "steps(4, end)");
bodyAttributes.set("data-animations", "terminal");
observer.pending.push({});
assert.equal(readMotionDurationMs(element, "--motion-bar"), 240, "same-task attribute change cannot return stale duration");
const stepped = readMotionEase(element, "--ease-decel");
assert.deepEqual([0, 0.2, 0.25, 0.49, 0.5, 0.99, 1].map(stepped), [0, 0, 0.25, 0.25, 0.5, 0.75, 1], "steps(n, end) jumps at the end of each step");
observer.callback();
assert.equal(reads.length, 2, "observer delivery does not read an unchanged attribute again");
bodyAttributes.set("data-window-resizing", "yes");
observer.pending.push({});
assert.equal(readMotionDurationMs(element, "--motion-bar"), 0);
bodyAttributes.delete("data-window-resizing");
observer.pending.push({});
assert.equal(readMotionDurationMs(element, "--motion-bar"), 240);
assert.deepEqual(reads, [body, body, body, body]);
dispose();
assert.equal(observer.disconnected, true);
assert.throws(() => readMotionDurationMs(element, "--motion-bar"), /not mounted/);
values.set("--ease-decel", "steps(4)");
values.delete("--motion-bar");
assert.throws(() => observeMotionTokens(document), /--motion-bar is not a duration/);
values.set("--motion-bar", "360ms");
values.set("--ease-bar", "ease-out");
assert.throws(() => observeMotionTokens(document), /--ease-bar is not a cubic-bezier or steps/);
console.log("PASS motion tokens: body owner, invalidation, cubic-bezier and steps parsing, no subtree reads");
