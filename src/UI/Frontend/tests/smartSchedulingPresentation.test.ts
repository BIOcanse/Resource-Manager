import assert from "node:assert/strict";
import { smartPlacementTarget } from "../src/presentation/smartScheduling.ts";
import { applyLanguage, currentLanguage } from "../src/text.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import type { HostManagerAppliedPlacementReceipt } from "../src/types.ts";

const placement: HostManagerAppliedPlacementReceipt = {
  targetId: "process:42:133700000000000000", softwareId: "example",
  processId: 42, processStartKey: "133700000000000000", processName: "Tool-A",
  resourceKind: "cpu", records: [], appliedAt: "2026-10-24T12:00:00Z", updatedAt: "2026-10-24T12:00:00Z"
};
const rendered = new Set<string>();
for (const language of languageOptions.filter(option => option.id !== "system")) {
  await applyLanguage(language.id);
  assert.equal(currentLanguage(), language.id);
  const copy = await loadAppCopy(language.id);
  await loadSettingsText(language.id);
  const label = smartPlacementTarget(placement);
  assert.equal(label, copy.smartReport.processTarget("Tool-A", 42));
  assert(label.includes("Tool-A") && label.includes("42"));
  rendered.add(label);
  assert.equal(smartPlacementTarget({ ...placement, processId: null }), "Tool-A");
  assert.equal(smartPlacementTarget(placement, "Game-A"), copy.smartReport.processTarget("Game-A / Tool-A", 42));
  assert.equal(smartPlacementTarget(placement, "tool-a"), copy.smartReport.processTarget("Tool-A", 42));
  assert(!copy.optimization.evaluationFailed().includes("IOException"));
  console.log(`PASS Smart process facts presentation ${language.id}`);
}
assert.equal(rendered.size, 9);
