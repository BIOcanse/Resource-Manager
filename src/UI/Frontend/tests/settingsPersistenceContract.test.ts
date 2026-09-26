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
