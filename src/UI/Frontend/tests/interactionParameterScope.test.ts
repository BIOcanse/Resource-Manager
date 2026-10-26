import assert from "node:assert/strict";
import { selectionIndicatorDefinition } from "../src/interactions/components/selectionIndicator.ts";
import { Runtime } from "../src/interactions/protocol/runtime.ts";
import type { Composite, Host, Platform, Presentation } from "../src/interactions/protocol/runtime.ts";
import { Trace } from "../src/interactions/protocol/trace.ts";

// 字符串路径模拟包含关系：子部件先挂、母部件后挂，参数仍遵循从外到内的覆盖。
const platform: Platform = {
  contains: (parent, child) => parent === child || String(child).startsWith(`${parent}/`),
  rect: () => { throw new Error("Parameter changes must not read layout"); },
  responseZone: () => ({ left: 0, top: 0, right: 100, bottom: 100 }),
  capture() {}, release() {}, focus() {}, requestFrame() {}
};
const host: Host = { value: () => 0, enabled: () => true, call() {}, subscribe: () => () => {} };
const view = (element: string): Presentation => ({ element, bindingLabel: "test", geometry: () => null, render() {}, aria: () => "" });
for (const mode of ["reference", "optimized"] as const) {
  const runtime = new Runtime(null, new Trace(), platform, { mode });
  const leaf = runtime.add("leaf", selectionIndicatorDefinition, view("root/branch/leaf"), host);
  const sibling = runtime.add("sibling", selectionIndicatorDefinition, view("other/leaf"), host);
  const siblingParams = sibling.params;
  const original = leaf.params.settleTime.value;
  const parent = runtime.add("parent", { ...selectionIndicatorDefinition, params: { settleTime: 111 } }, view("root/branch"), host);
  assert.equal(leaf.params.settleTime.value, 111, "late parent applies to existing descendants");
  assert.equal(sibling.params, siblingParams, "unrelated mount does not resolve sibling parameters again");
  const outer: Composite = { name: "outer", element: "root", children: [], overrides: { settleTime: 222 }, route: () => null };
  runtime.addComposite(outer);
  assert.equal(leaf.params.settleTime.value, 111, "nearer parent wins over outer composite");
  runtime.setOverride(parent, "settleTime", 333, 1);
  assert.equal(leaf.params.settleTime.value, 333);
  const colocated = runtime.add("same element", { ...selectionIndicatorDefinition, params: { settleTime: 444 } }, view("root/branch/leaf"), host);
  assert.equal(leaf.params.settleTime.value, 333, "same-element instances do not become parents");
  assert.equal(colocated.params.settleTime.value, 444, "own definition still wins");
  runtime.remove(parent, 2);
  assert.equal(leaf.params.settleTime.value, 222, "removing a parent restores the remaining outer layer");
  runtime.remove(outer, 3);
  assert.equal(leaf.params.settleTime.value, original, "removing a composite restores source defaults");
  assert.equal(sibling.params, siblingParams, "unrelated removals also leave sibling parameters intact");
  runtime.setOverride(null, "settleTime", 555, 4);
  assert.equal(leaf.params.settleTime.value, 555);
  assert.equal(sibling.params.settleTime.value, 555, "global overrides still affect every branch");
}
console.log("PASS interaction parameter scope: late parents, composites, removal, same elements and global overrides");
