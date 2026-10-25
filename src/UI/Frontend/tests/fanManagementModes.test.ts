import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { controlReasonText } from "../src/presentation/controlReasonText.ts";

const modes = ["firmware-management", "firmware-write", "software-management"];
const terms = ["windows", "nvidia", "amd", "intel", "oem", "unknown", ...modes, "takeover-all-fans", "portable", "fixed", "cpu", "gpu", "intake", "case",
  "uniwill-wmi-ec", "acer-gaming-wmi", "asus-atk-wmi", "hp-wmi-bios", "lenovo-legion-wmi", "desktop-superio", "hwmon"];
const backend = readFileSync(new URL("../../../../src/Core/Infrastructure/Control/Writers/FanControlWriter.cs", import.meta.url), "utf8");
const facts = backend.slice(backend.indexOf('public const string MixedManagementModes'), backend.indexOf('public async Task<bool> RestoreAsync'));
const reasons = [...new Set([...facts.matchAll(/"(fan-[a-z-]+)"/g)].map(match => match[1])), "fan-restore-protocol-required"];
assert.ok(reasons.includes("fan-management-modes-mixed"));
const english = (await loadAppCopy("en-US")).control;
let languages = 0, checks = 0;
for (const { id } of languageOptions) {
  if (id === "system") continue;
  languages++;
  const copy = (await loadAppCopy(id)).control;
  for (const term of terms) {
    const text = copy.term[term];
    assert.ok(text?.trim(), `${id}: missing term ${term}`);
    if (!["intel"].includes(term)) assert.notEqual(text, term, `${id}: raw term ${term}`);
    if (id !== "en-US" && !["windows", "nvidia", "amd", "intel"].includes(term) && !(id === "de-DE" && term === "portable")) assert.notEqual(text, english.term[term], `${id}: English term ${term}`);
    checks++;
  }
  for (const mode of modes) {
    const hint = copy.fanModeHint[mode as keyof typeof copy.fanModeHint];
    assert.ok(hint?.trim(), `${id}: missing mode hint ${mode}`);
    if (id !== "en-US") assert.notEqual(hint, english.fanModeHint[mode as keyof typeof english.fanModeHint], `${id}: English mode hint ${mode}`);
    checks++;
  }
  const unavailable = copy.fanModeUnavailable("M", "R");
  assert.ok(unavailable.startsWith("M") && unavailable.endsWith("R") && unavailable.length > 2, `${id}: unavailable line ${unavailable}`);
  const takeover = copy.takeoverCoversOthers("X");
  assert.ok(takeover.includes("X") && takeover.toLocaleLowerCase(id).includes(copy.term["software-management"].toLocaleLowerCase(id)),`${id}: takeover note names the software management mode`);
  checks += 2;
  for (const code of reasons) {
    const text = copy.presentation.text[code];
    assert.ok(text?.trim(), `${id}: missing mode reason ${code}`);
    assert.equal(controlReasonText({ code }, copy), text, `${id}: reason consumer ${code}`);
    assert.notEqual(text, code);
    if (id !== "en-US") assert.notEqual(text, english.presentation.text[code], `${id}: English reason ${code}`);
    checks++;
  }
}
assert.equal(languages, 9);
console.log(`fan management localization: ${languages} languages, ${checks} checks passed`);

