import { controlReasonText, controlRequestErrorText } from "../src/presentation/controlReasonText.ts";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { controlCapabilityLabel, controlDisplayText, controlOutcomeReason, controlObjectName } from "../src/features/control/controlPresentation.ts";
import { createAppCopy } from "../src/i18n/copy/index.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { applyLanguage, currentLanguage, uiText } from "../src/text.ts";

const en = createAppCopy("en-US").control.presentation;
const zh = createAppCopy("zh-CN").control.presentation;
const catalog = readFileSync(new URL("../../../../src/Core/Infrastructure/Control/WindowsControlObjectCatalog.cs", import.meta.url), "utf8");
const ids = new Set([...catalog.matchAll(/"((?:cpu|gpu|fan)\.[a-z-]+)"/g)].map(match => match[1]));
ids.delete("cpu.fanRpm");
for (const id of ids) {
  const capability = { id };
  assert.doesNotMatch(controlCapabilityLabel(capability, en), /\p{Script=Han}/u, id);
  assert.equal(controlCapabilityLabel(capability, zh), zh.labels[id], id);
}
assert.equal(controlCapabilityLabel({ id: "cpu.power-limit", channel: "intel-msr" }, en), "Sustained power limit (PL1)");
assert.equal(controlCapabilityLabel({ id: "extension.custom" }, en), "extension.custom");
assert.equal(controlObjectName({ kind: "fan", reportedName: null, ordinal: 1 }, createAppCopy("en-US").control), "Fan 2");
assert.equal(controlDisplayText("档", en), "steps");
assert.equal(controlDisplayText("My custom fan", en), "My custom fan");
assert.equal(controlReasonText({ code: "control-writer-missing" }, createAppCopy("en-US").control), "Write support is not implemented for this item.");

const sameCapability = { id: "fan.curve" };
for (const language of ["en-US", "zh-CN", "en-US"]) {
  applyLanguage(language);
  for (let attempt = 0; currentLanguage() !== language && attempt < 100; attempt++) {
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  assert.equal(currentLanguage(), language);
  assert.equal(controlCapabilityLabel(sameCapability), language === "en-US" ? "Fan speed curve" : "转速曲线");
}
// Fan curve write failures use the same existing readback reason as other
// controls. Exercise its actual consumer and active bundle in every open language.
const curveReceiptReason = "control-readback-mismatch";
const writerSource = readFileSync(new URL("../../../../src/Core/Infrastructure/Control/Writers/FanControlWriter.cs", import.meta.url), "utf8");
assert.doesNotMatch(writerSource, /下发了，但从固件里读不回这条曲线。/u);
let curveReceiptLanguages = 0;
for (const { id } of languageOptions) {
  if (id === "system") continue;
  const control = (await loadAppCopy(id)).control;
  const presentation = control.presentation;
  for (const field of ["reassertionInterval", "stateUnavailable", "retryRead", "curveUnavailable"] as const) {
    assert.ok(control[field].trim(), `${id}: ${field} is empty`);
    if (id !== "en-US") assert.notEqual(control[field], (await loadAppCopy("en-US")).control[field], `${id}: ${field} inherited English`);
  }
  const translated = presentation.text[curveReceiptReason];
  assert.ok(translated?.trim(), `${id}: curve readback reason is absent`);
  if (id !== "zh-CN") assert.notEqual(translated, curveReceiptReason, `${id}: inherited backend Chinese`);
  assert.equal(controlReasonText({ code: curveReceiptReason }, control), translated);
  applyLanguage(id);
  for (let attempt = 0; currentLanguage() !== id && attempt < 100; attempt++) {
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  assert.equal(currentLanguage(), id);
  for (const field of ["reassertionInterval", "stateUnavailable", "retryRead", "curveUnavailable"] as const) {
    assert.equal(uiText.control[field], control[field], `${id}: active-language ${field}`);
  }
  assert.equal(controlReasonText({ code: curveReceiptReason }), translated, `${id}: active-language curve failure`);
  curveReceiptLanguages++;
}
console.log(`Control presentation: ${ids.size} capability IDs; curve receipt failures and real language switching passed in ${curveReceiptLanguages} selectable languages.`);

const reasonCodes = ["control-object-missing", "control-capability-missing", "control-writer-missing",
  "control-backend-unavailable", "control-release-failed", "control-unit-mismatch", "control-write-failed",
  "control-write-rejected", "startup-profile-read-only", "control-access-level-required", "control-out-of-range"];
for (const { id } of languageOptions) {
  if (id === "system") continue;
  const copy = (await loadAppCopy(id)).control;
  for (const code of reasonCodes) {
    const outcome = { objectId: "a", capabilityId: "bad", status: "failed" as const,
      reason: { code, arguments: { requiredAccessLevel: "root" as const, minimum: -17, maximum: 83, unit: "MHz" } } };
    const rendered = controlOutcomeReason(outcome, copy)!;
    assert.ok(rendered?.trim(), `${id}/${code}`);
    assert.doesNotMatch(rendered, /RAW_DIAGNOSTIC|原始原因|undefined|\$\{/u, `${id}/${code}`);
    if (code === "control-out-of-range") for (const value of ["-17", "83", "MHz"]) assert.ok(rendered.includes(value), `${id}/${code}/${value}`);
    if (code === "control-access-level-required") assert.ok(rendered.includes(copy.accessLevel.name.root), id);
    if (id !== "en-US") assert.notEqual(rendered, controlOutcomeReason(outcome, (await loadAppCopy("en-US")).control), `${id}/${code}: English inheritance`);
  }
  assert.equal(controlOutcomeReason({ objectId: "a", capabilityId: "x", status: "failed" }, copy), copy.recovery.writeRejected);
}
console.log("Stable control reasons and typed arguments render in all nine locales.");

for (const { id } of languageOptions) {
  if (id === "system") continue;
  const copy = (await loadAppCopy(id)).control;
  const mismatch = controlReasonText({ code: "control-readback-mismatch", arguments: { requested: 31, observed: 27, unit: "W" } }, copy);
  for (const value of ["31", "27", "W"]) assert.ok(mismatch.includes(value), `${id}: ${value}`);
  const clocks = controlReasonText({ code: "nvidia.M36", arguments: { minimum: 123, maximum: 456, unit: "MHz" } }, copy);
  for (const value of ["123", "456", "MHz"]) assert.ok(clocks.includes(value), `${id}: rejected clock interval ${value}`);
  assert.equal(controlObjectName({ kind: "fan", reportedName: null, ordinal: 1 }, copy), copy.presentation.fanName("2"));
  assert.equal(controlObjectName({ kind: "gpu", reportedName: "OS-reported 名称" }, copy), "OS-reported 名称");
  for (const code of ["nvidia.M2", "nvidia.M8", "nvidia.M26", "nvidia.M63", "amd-cpu.NumberRequired", "intel-cpu.VoltageReadbackMismatch", "uniwill-firmware-managed", "fan-firmware-prerequisite"]) {
    const result = controlReasonText({ code }, copy);
    assert.notEqual(result, copy.recovery.writeRejected, `${id}: unmapped ${code}`);
    assert.ok(result.trim(), `${id}: ${code}`);
  }
}
assert.equal(controlRequestErrorText({ error: "RAW_EXCEPTION" }), null);
assert.equal(controlRequestErrorText({ reason: { code: "control-preset-name-invalid", arguments: { maximum: "64" } } }), null);
assert.ok(controlRequestErrorText({ reason: { code: "control-preset-name-invalid", arguments: { maximum: 64 } } })?.includes("64"));
assert.ok(controlRequestErrorText({ reason: { code: "control-preset-count-limit", arguments: { maximum: 32 } } })?.includes("32"));
