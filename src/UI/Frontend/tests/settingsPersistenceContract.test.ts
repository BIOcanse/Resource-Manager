import assert from "node:assert/strict";
import test from "node:test";
import { applySettingsPatch, createSettingsPatch } from "../src/features/settings/settingsPatch.ts";

test("settings patch contains only changed leaves", () => {
  const baseline = {
    version: "1.0.23",
    appearance: { theme: "system", language: "zh-CN" },
    performance: { optimizationMode: "normal" }
  };
  const draft = structuredClone(baseline);
  draft.appearance.theme = "dark";

  assert.deepEqual(createSettingsPatch(baseline, draft), {
    appearance: { theme: "dark" }
  });
});

test("settings patch treats arrays as an atomic changed field", () => {
  const baseline = { version: "1.0.23", values: ["general"] };
  const draft = { version: "1.0.23", values: ["general", "gaming"] };

  assert.deepEqual(createSettingsPatch(baseline, draft), {
    values: ["general", "gaming"]
  });
});

test("rebase applies only edited leaves and arrays without mutating the committed value", () => {
  const baseline = { appearance: { theme: "system", color: "type" }, values: ["a"] };
  const draft = { appearance: { theme: "dark", color: "type" }, values: ["b"] };
  const committed = { appearance: { theme: "light", color: "distinct" }, values: ["a"], extra: true };
  const rebased = applySettingsPatch(committed, createSettingsPatch(baseline, draft));
  assert.deepEqual(rebased, {
    appearance: { theme: "dark", color: "distinct" }, values: ["b"], extra: true
  });
  assert.equal(committed.appearance.theme, "light");
  rebased.values.push("c");
  assert.deepEqual(draft.values, ["b"]);
  assert.deepEqual(committed.values, ["a"]);
});

test("settings patch drops unchanged nested objects at every depth and survives deep nesting", () => {
  const baseline = { version: "1", a: { b: { c: { d: 1 } }, keep: { same: true } } };
  const draft = { version: "2", a: { b: { c: { d: 2 } }, keep: { same: true } } };
  assert.deepEqual(createSettingsPatch(baseline, draft), { a: { b: { c: { d: 2 } } } });
  assert.deepEqual(createSettingsPatch(baseline, structuredClone(baseline)), {});

  // 比较用显式栈：嵌套深度超过调用栈时也必须得出补丁（递归实现会抛 RangeError）。
  const depth = 20_000;
  const deepBaseline: Record<string, unknown> = {};
  const deepDraft: Record<string, unknown> = {};
  let left = deepBaseline;
  let right = deepDraft;
  for (let index = 0; index < depth; index += 1) {
    const nextLeft: Record<string, unknown> = {};
    const nextRight: Record<string, unknown> = {};
    left.child = nextLeft;
    right.child = nextRight;
    left = nextLeft;
    right = nextRight;
  }
  left.value = 1;
  right.value = 2;
  let patch = createSettingsPatch(deepBaseline, deepDraft) as Record<string, unknown>;
  for (let index = 0; index < depth; index += 1) {
    patch = patch.child as Record<string, unknown>;
  }
  assert.deepEqual(patch, { value: 2 });
});
