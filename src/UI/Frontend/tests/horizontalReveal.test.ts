import assert from "node:assert/strict";
import { createRoot, createSignal } from "solid-js";
import { useHorizontalReveal } from "../src/interactions/solid/horizontalReveal.ts";
let deliver!: (entries: unknown[]) => void;
const observed = new Set<unknown>();
Object.assign(globalThis, { ResizeObserver: class {
  constructor(callback: typeof deliver) { deliver = callback; }
  observe(target: unknown) { observed.add(target); }
  unobserve(target: unknown) { observed.delete(target); }
  disconnect() { observed.clear(); }
} });
const COORDINATE_EPSILON = 0.0001;
let left = 0;
let reads = 0;
const container = { getBoundingClientRect() { reads++; return { left: 0, right: 97, width: 97, height: 30 }; },
  get scrollLeft() { return left; }, set scrollLeft(value: number) { left = value; } } as HTMLElement;
const first = { getBoundingClientRect() { reads++; return { left: 97, right: 145.5 }; } } as HTMLElement;
const second = { getBoundingClientRect() { reads++; return { left: -19.4, right: 29.1 }; } } as HTMLElement;
const [selected, select] = createSignal(first);
const dispose = createRoot((dispose) => {
  useHorizontalReveal({ container: () => container, selected, change: selected });
  return dispose;
});
assert.equal(reads, 0, "mount does not read geometry");
const entries = () => [...observed].map(target => ({ target, borderBoxSize: [{ inlineSize: 100, blockSize: 30 }] }));
deliver(entries());
assert.ok(Math.abs(left - 50) < COORDINATE_EPSILON, "reveal right edge in layout pixels despite scale entrance");
select(second);
assert.equal(reads, 2, "selection change does not read dirty geometry");
deliver(entries());
assert.ok(Math.abs(left - 30) < COORDINATE_EPSILON, "reveal left edge from the latest layout");
dispose();
assert.equal(observed.size, 0);
console.log("PASS horizontal reveal: post-layout reads, scaled scroll coordinates, cleanup");
