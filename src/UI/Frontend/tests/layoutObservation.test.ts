import assert from "node:assert/strict";
import { observeLayout } from "../src/interactions/solid/layoutObservation.ts";

let deliver!: (entries: { target: Element; borderBoxSize: { inlineSize: number; blockSize: number }[] }[]) => void;
const entry = (target: Element) => ({ target, borderBoxSize: [{ inlineSize: 123.25, blockSize: 31.5 }] });
const watched = new Set<Element>();
let resets = 0;
Object.assign(globalThis, { ResizeObserver: class {
  constructor(callback: typeof deliver) { deliver = callback; }
  observe(element: Element) { watched.add(element); }
  unobserve(element: Element) { if (watched.has(element)) resets++; watched.delete(element); }
  disconnect() { watched.clear(); }
} });
const events: string[] = [];
const a = {} as Element;
const b = {} as Element;
const one = observeLayout({ read: (sizes) => { assert.deepEqual(sizes.get(a), { width: 123.25, height: 31.5 }); events.push("read one"); }, write: () => events.push("write one") });
const two = observeLayout({ read: () => events.push("read two"), write: () => events.push("write two") });
one.update([a]); two.update([b]);
assert.deepEqual(events, [], "mounting must not read geometry");
deliver([entry(a), entry(b), entry(a)]);
assert.deepEqual(events, ["read one", "read two", "write one", "write two"], "deduplicate owners and batch all reads before writes");
one.update([]); two.update([]);
assert.equal(watched.size, 0, "no-animation mode releases geometry observations");
one.dispose();
two.update([b]);
const three = observeLayout({ read: () => events.push("read three"), write: () => events.push("write three") });
three.update([a]);
events.length = 0;
deliver([entry(b), entry(a)]);
assert.deepEqual(events, ["read two", "read three", "write two", "write three"], "all surviving owners keep the shared read phase across mode changes");
// 指示条和滚动定位可共享同一元素；一个消费者退出不应解除另一个的观察。
const resetsBeforeSharing = resets;
two.update([a, b]);
assert.equal(resets, resetsBeforeSharing + 1, "a new shared reader resets the pending layout notification");
three.dispose();
assert.equal(watched.has(a), true);
events.length = 0;
deliver([entry(a)]);
assert.deepEqual(events, ["read two", "write two"]);
two.dispose();
assert.equal(watched.size, 0);
console.log("PASS layout observation: deferred geometry, shared read/write phases, cleanup");
