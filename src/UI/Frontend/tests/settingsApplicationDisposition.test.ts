import assert from "node:assert/strict";
import test from "node:test";
import { classifySettingsApplicationOutcome } from "../src/features/settings/settingsApplicationDisposition.ts";

test("only a fully delivered settings publication is applied", () => {
  assert.equal(classifySettingsApplicationOutcome("committedAndApplied"), "applied");
  assert.equal(
    classifySettingsApplicationOutcome("committedWithCapabilityConstraints"),
    "capability-constrained");
  assert.equal(classifySettingsApplicationOutcome("committedWithDeliveryFailures"), "persisted-pending");
  assert.equal(classifySettingsApplicationOutcome("savedNotApplied"), "persisted-pending");
});

test("uncommitted and unknown settings results are rejected", () => {
  assert.equal(classifySettingsApplicationOutcome("revisionConflict"), "rejected");
  assert.equal(classifySettingsApplicationOutcome("rejectedBeforeCommit"), "rejected");
  assert.equal(classifySettingsApplicationOutcome(undefined), "rejected");
});
