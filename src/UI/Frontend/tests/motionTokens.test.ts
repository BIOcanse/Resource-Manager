import assert from "node:assert/strict";
import { cubicBezier } from "../src/interactions/protocol/ease.ts";
import { observeMotionTokens, readMotionDurationMs, readMotionEase } from "../src/presentation/motionTokens.ts";

const values = new Map<string, string>([
  ...["fast", "medium", "page", "panel", "bar"].map((name) => [`--motion-${name}`, "480ms"] as [string, string]),
  ...["decel", "standard", "bar"].map((name) => [`--ease-${name}`, "cubic-bezier(0.35, 0, 0.15, 1)"] as [string, string])
]);
const rootAttributes = new Map<string, string>();
const bodyAttributes = new Map<string, string>();
const root = { getAttribute: (name: string) => rootAttributes.get(name) ?? null };
const body = { getAttribute: (name: string) => bodyAttributes.get(name) ?? null };
const document = { documentElement: root, body } as unknown as Document;
Object.assign(root, { ownerDocument: document });
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
    assert.ok(target === root || target === body, "tokens must never query a mounted subtree");
    reads.push(target);
    return { getPropertyValue: (name: string) => target === body && name.startsWith("--motion-") ? "0ms" : values.get(name) ?? "" };
  }
});
const dispose = observeMotionTokens(document);
assert.equal(readMotionDurationMs(element, "--motion-bar"), 480);
const ease = readMotionEase(element, "--ease-bar");
const expected = cubicBezier(0.35, 0, 0.15, 1);
for (const progress of [0, 0.25, 0.5, 0.75, 1]) assert.equal(ease(progress), expected(progress));
for (let i = 0; i < 100; i++) { readMotionDurationMs(element, "--motion-bar"); readMotionEase(element, "--ease-bar"); }
assert.deepEqual(reads, [root], "mounts read the snapshot, without repeated CSS queries");
values.set("--motion-bar", "0.24s");
rootAttributes.set("data-motion-feel", "crisp");
observer.pending.push({});
assert.equal(readMotionDurationMs(element, "--motion-bar"), 240, "same-task attribute change cannot return stale duration");
observer.callback();
assert.equal(reads.length, 2, "observer delivery does not read an unchanged attribute again");
bodyAttributes.set("data-window-resizing", "yes");
observer.pending.push({});
assert.equal(readMotionDurationMs(element, "--motion-bar"), 0);
assert.equal(readMotionDurationMs(root as unknown as Element, "--motion-bar"), 240);
bodyAttributes.delete("data-window-resizing");
observer.pending.push({});
assert.equal(readMotionDurationMs(element, "--motion-bar"), 240);
assert.deepEqual(reads, [root, root, body]);
dispose();
assert.equal(observer.disconnected, true);
assert.throws(() => readMotionDurationMs(element, "--motion-bar"), /not mounted/);
values.delete("--motion-bar");
assert.throws(() => observeMotionTokens(document), /--motion-bar is not a duration/);
values.set("--motion-bar", "360ms");
values.set("--ease-bar", "ease-out");
assert.throws(() => observeMotionTokens(document), /--ease-bar is not a cubic-bezier/);
console.log("PASS motion tokens: owner, invalidation, parsing, no subtree reads");
