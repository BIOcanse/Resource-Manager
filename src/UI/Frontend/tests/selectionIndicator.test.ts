import assert from "node:assert/strict";
import { createRoot, createSignal } from "solid-js";
import { useSelectionIndicator } from "../src/interactions/solid/selectionIndicator.ts";
import { setInteractionAnimationMode } from "../src/interactions/solid/lodestone.ts";

let deliver!: (entries: unknown[]) => void;
const observed = new Set<unknown>();
let reads = 0;
let observeCalls = 0;
let scale = 0.97;
class TestElement {
  parentElement: TestElement | null = null;
  readonly children: TestElement[] = [];
  readonly classes = new Set<string>();
  readonly classList = { add: (name: string) => this.classes.add(name), remove: (name: string) => this.classes.delete(name) };
  readonly style = { opacity: "", visibility: "", transform: "", width: "", height: "",
    removeProperty(_name: string) {}, setProperty(_name: string, _value: string) {} };
  clientLeft = 0; clientTop = 0; scrollLeft = 0; scrollTop = 0;
  constructor(readonly left: number, readonly width: number) {}
  getBoundingClientRect() {
    reads++;
    return { left: this.left * scale, top: 0, width: this.width * scale, height: 30 * scale };
  }
  appendChild(element: TestElement) {
    if (element.parentElement) {
      const siblings = element.parentElement.children;
      siblings.splice(siblings.indexOf(element), 1);
    }
    element.parentElement = this;
    this.children.push(element);
  }
  contains(element: TestElement) { return this === element || this.children.includes(element); }
}
Object.assign(globalThis, {
  Node: TestElement, Element: TestElement,
  window: { addEventListener() {}, removeEventListener() {}, document: { addEventListener() {}, removeEventListener() {} } },
  requestAnimationFrame() { return 1; },
  MutationObserver: class { observe() {} disconnect() {} },
  ResizeObserver: class {
    constructor(callback: typeof deliver) { deliver = callback; }
    observe(target: unknown) { observeCalls++; observed.add(target); }
    unobserve(target: unknown) { observed.delete(target); }
    disconnect() { observed.clear(); }
  }
});
const container = new TestElement(0, 200);
const first = new TestElement(0, 86);
const second = new TestElement(90, 90);
const indicator = new TestElement(0, 0);
container.appendChild(first); container.appendChild(second); container.appendChild(indicator);
const [order, setOrder] = createSignal(0);
const element = (value: TestElement) => value as unknown as HTMLElement;
const dispose = createRoot(dispose => {
  useSelectionIndicator({
    name: "test indicator", container: () => element(container), indicator: () => element(indicator),
    items: () => [element(first), element(second)],
    selected: () => element(order() === 0 ? first : second), order
  });
  return dispose;
});
assert.equal(reads, 0, "mount does not force layout");
deliver([...observed].map(target => ({ target, borderBoxSize: [{ inlineSize: (target as TestElement).width, blockSize: 30 }] })));
assert.equal(indicator.style.width, "86px", "cache layout width, not the 83.42 px entrance rectangle");
scale = 1;
assert.equal(indicator.style.width, "86px", "ending the scale entrance needs no second measurement");
const initialObserveCalls = observeCalls;
setOrder(1);
assert.equal(observeCalls, initialObserveCalls, "normal selection reuses geometry instead of resetting the mode observer");
setOrder(0);
setInteractionAnimationMode("none");
assert.equal(observed.size, 0);
const readsBeforeNone = reads;
assert.equal(indicator.parentElement, first);
setOrder(1);
assert.equal(indicator.parentElement, second, "the same indicator follows selection through layout");
assert.equal(reads, readsBeforeNone, "none mode never measures");
setInteractionAnimationMode("ultra");
assert.equal(indicator.parentElement, second);
assert.equal(reads, readsBeforeNone, "ultra mode never measures");
setInteractionAnimationMode("normal");
assert.equal(indicator.parentElement, container, "normal animation restores the group coordinate system");
dispose();
assert.equal(observed.size, 0);
console.log("PASS selection indicator: scale entrance cache, no-animation layout positioning, mode restoration, cleanup");
