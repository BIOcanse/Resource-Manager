import assert from "node:assert/strict";
import { decodePhysicalMemoryResidualFrame } from "../src/api/resourceBreakdownWire.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { physicalMemoryResidualReason, physicalMemoryResidualView } from "../src/presentation/physicalMemoryResidual.ts";
import { applyLanguage, uiText } from "../src/text.ts";

// The complete emitted reason vocabulary of docs/runtime_process_attribution_and_io_network_telemetry.md,
// section "Residual reason codes" (backend r3, dev 33425ec5). The frontend copy must match it exactly.
const readyReason = "process-private-below-attributed";
const unavailableReasons = [
  "attributed-private-exceeds-used", "components-exceed-residual", "unsupported-platform", "invalid-physical-ranges",
  "range-count-limit", "page-count-limit", "capture-time-limit", "invalid-pfn-response", "available-exceeds-total",
  "invalid-superfetch-layout", "invalid-superfetch-response", "arithmetic-overflow", "capture-output-missing",
  "resource-breakdown-dataset-sample-failed"
];
const factsReason = "system-total-unavailable";
const fixedCodes = [readyReason, ...unavailableReasons, factsReason];
// Each parameterized family with two sample suffixes: the family is translated, the suffix is data.
const families = [
  ["superfetch-17", "0xC0000004", "0xC000009A"],
  ["superfetch-6", "0xC0000004", "0xC000009A"],
  ["profile-privilege", "0xC0000061", "0xC0000022"],
  ["privilege-restore", "0xC0000022", "0xC0000061"],
  ["native-error", "5", "1314"]
] as const;
// Codes outside the vocabulary: counter/tag diagnostics are logs only, and anything else is not guessed.
const unknownCodes = [
  "pool-tags:overflow", "pool-tags:0xC0000023", "performance:0xC0000004:16", "ready", "not-captured",
  "resource-breakdown-dataset-output-missing", "superfetch-79:0xC0000004", "superfetch-17:0xc0000004",
  "superfetch-17:0xC000004", "native-error:-5", "native-error:0x5", "profile-privilege:5", "future-reason"
];

const frame = (status: "ready" | "unavailable", reasonCode: string | null) => status === "ready"
  ? { version: 1, source: "pfn", status, reasonCode, capturedAt: "2026-10-26T06:00:00Z",
      usedBytes: 12, attributedProcessPrivateBytes: 4, residualBytes: 8, unclassifiedBytes: 8, parts: [] }
  : { version: 1, source: "pfn", status, reasonCode, capturedAt: "2026-10-26T06:00:00Z",
      usedBytes: null, attributedProcessPrivateBytes: null, residualBytes: null, unclassifiedBytes: null, parts: [] };

// Decoder: a ready frame carries null or the one ready reason; every PFN reason, family and unknown code is
// unavailable; the ready reason and the facts-only reason never arrive as an unavailable PFN reason.
decodePhysicalMemoryResidualFrame(frame("ready", null));
decodePhysicalMemoryResidualFrame(frame("ready", readyReason));
for (const code of [...unavailableReasons, factsReason, ...families.map(([family, suffix]) => `${family}:${suffix}`), ...unknownCodes]) {
  assert.throws(() => decodePhysicalMemoryResidualFrame(frame("ready", code)), undefined, `ready frame with ${code}`);
}
for (const code of [...unavailableReasons, ...families.map(([family, suffix]) => `${family}:${suffix}`), ...unknownCodes]) {
  assert.equal(decodePhysicalMemoryResidualFrame(frame("unavailable", code)).reasonCode, code);
}
for (const code of [readyReason, factsReason]) {
  assert.throws(() => decodePhysicalMemoryResidualFrame(frame("unavailable", code)), undefined, `unavailable frame with ${code}`);
}
// Its own capture time and status: an unavailable frame decodes with its own capturedAt and no readings.
assert.equal(decodePhysicalMemoryResidualFrame(frame("unavailable", "capture-time-limit")).capturedAt, "2026-10-26T06:00:00Z");
assert.throws(() => decodePhysicalMemoryResidualFrame({ ...frame("unavailable", "capture-time-limit"), capturedAt: null }));
assert.throws(() => decodePhysicalMemoryResidualFrame({ ...frame("unavailable", "capture-time-limit"), status: undefined }));

const languages = languageOptions.filter(option => option.id !== "system");
assert.equal(languages.length, 9);
for (const language of languages) {
  await applyLanguage(language.id);
  const copy = uiText.physicalMemoryResidual;
  // Exactly the fixed vocabulary: no copy for codes the backend does not send, none missing.
  assert.deepEqual(Object.keys(copy.reasons).sort(), [...fixedCodes].sort(), language.id);

  const texts = new Set<string>();
  for (const code of fixedCodes) {
    const text = physicalMemoryResidualReason(code);
    assert.ok(text && text.trim() && !text.includes(code) && !/[{}]|undefined|null/.test(text), `${language.id}: ${code} -> ${text}`);
    texts.add(text);
  }
  assert.equal(texts.size, fixedCodes.length, `${language.id}: every code has its own description`);

  const familyTexts = new Set<string>();
  for (const [family, first, second] of families) {
    const a = physicalMemoryResidualReason(`${family}:${first}`);
    const b = physicalMemoryResidualReason(`${family}:${second}`);
    assert.ok(a && b && a.includes(first) && b.includes(second), `${language.id}: ${family} shows its suffix`);
    assert.ok(!a.includes(family), `${language.id}: ${family} is translated, not echoed`);
    // The suffix is data, not a key: the two samples differ only by the suffix.
    assert.equal(a.replace(first, "#"), b.replace(second, "#"), `${language.id}: ${family}`);
    familyTexts.add(a.replace(first, "#"));
  }
  assert.equal(familyTexts.size, families.length, `${language.id}: each family has its own description`);

  for (const code of unknownCodes) assert.equal(physicalMemoryResidualReason(code), null, `${language.id}: ${code} is not guessed`);

  // An unknown code is shown as itself: the view has no description and keeps the code as sent.
  const unknown = physicalMemoryResidualView(decodePhysicalMemoryResidualFrame(frame("unavailable", "future-reason")));
  assert.deepEqual(unknown, { state: "unavailable", capturedAt: "2026-10-26T06:00:00Z", reason: null, reasonCode: "future-reason" });
  assert.ok(copy.panel.reasonCode("future-reason").includes("future-reason"));
}

process.stdout.write("physicalMemoryResidual reason contract tests passed\n");
