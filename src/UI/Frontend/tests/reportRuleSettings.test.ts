import { frontendRefreshIntervals } from "./fixtures/frontendRefreshIntervalFixture.ts";
import assert from "node:assert/strict";
import type { AppReportSettings } from "../src/types.ts";
import { reportRuleSettingsDecoder, decodeReportSettings } from "../src/data/appSettings/reportRuleSettingsDecoder.ts";
import { appSettingsResultDecoder } from "../src/data/appSettings/appSettingsResultDecoder.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";
import { emptyReportRuleValues, parseExecutableNames, reportRuleNumberField,
  updateReportRuleField, parseReportRuleRejection } from "../src/features/settings/reportRuleSettingsModel.ts";
import { reportRules } from "./fixtures/reportRuleSettings.ts";

assert.deepEqual(reportRuleSettingsDecoder.decode({ rules: reportRules }), { rules: reportRules });
for (const patch of [
  { scale: 0 }, { scale: Infinity }, { step: -1 }, { comparison: "greater" }, { displayUnit: "bytes" },
  { thresholdMaximum: 1 }, { countMaximum: 1.5 }, { editableFields: ["other"] },
  { editableFields: ["clearThreshold", "clearThreshold"] },
  { defaults: { ...reportRules[0].defaults, activationThreshold: null } },
  { zeroActivityMaximum: -1 }, { defaults: { ...reportRules[0].defaults, clearThreshold: NaN } },
  { defaults: { ...reportRules[0].defaults, requiredConsecutiveHits: "6" } }
]) assert.throws(() => reportRuleSettingsDecoder.decode({ rules: [{ ...reportRules[0], ...patch }] }), ResponseDecodeError);
assert.throws(() => reportRuleSettingsDecoder.decode({ rules: [reportRules[0], reportRules[0]] }), ResponseDecodeError);
assert.throws(() => reportRuleSettingsDecoder.decode({ rules: null }), ResponseDecodeError);
const entry = { ...emptyReportRuleValues, rule: "cpu_throttle_temperature", activationThreshold: 92 };
assert.deepEqual(decodeReportSettings({ ruleOverrides: [entry] }, "$.reports"), { ruleOverrides: [entry] });
assert.deepEqual(appSettingsResultDecoder.decode({ frontendRefreshIntervals, settings: { version: "1", reports: { ruleOverrides: [entry] } },
  revision: "1" }).settings.reports, { ruleOverrides: [entry] });
for (const reports of [null, { ruleOverrides: null }, { ruleOverrides: [{ ...entry, activationThreshold: "92" }] },
  { ruleOverrides: [{ ...entry, executableNames: [3] }] }, { ruleOverrides: [{ rule: "incomplete" }] },
  { ruleOverrides: [{ ...entry, clearThreshold: Infinity }] }]) {
  assert.throws(() => appSettingsResultDecoder.decode({ frontendRefreshIntervals, settings: { version: "1", reports }, revision: "1" }), ResponseDecodeError);
}

const above = reportRuleNumberField(reportRules[0], emptyReportRuleValues, "clearThreshold");
assert.equal(above.max, 90);
const below = reportRuleNumberField(reportRules[1], { ...emptyReportRuleValues, activationThreshold: 87 }, "clearThreshold");
assert.equal(below.min, 87);
assert.equal(below.max, 200);
for (const [rule, value] of [[reportRules[2], 4], [reportRules[3], 50], [reportRules[4], 15]] as const) {
  assert.equal(reportRuleNumberField(rule, emptyReportRuleValues, "activationThreshold").value, value);
}
assert.equal(5 * reportRuleNumberField(reportRules[2], emptyReportRuleValues, "activationThreshold").scale, 5368709120);
assert.equal(reportRuleNumberField(reportRules[4], emptyReportRuleValues, "zeroActivityThreshold").step, 0.1);
const other = { ...entry, rule: "other" };
let settings: AppReportSettings = { ruleOverrides: [entry, other] };
settings = updateReportRuleField(settings, entry.rule, "clearThreshold", 84);
settings = updateReportRuleField(settings, entry.rule, "activationThreshold", null);
assert.equal(settings.ruleOverrides[0].clearThreshold, 84);
assert.deepEqual(settings.ruleOverrides[1], other);
settings = updateReportRuleField(settings, entry.rule, "clearThreshold", null);
assert.deepEqual(settings.ruleOverrides, [other]);
settings = updateReportRuleField(settings, "stray_process_idle", "executableNames", []);
assert.deepEqual(settings.ruleOverrides[1].executableNames, []);
assert.deepEqual(parseExecutableNames(" node \n\n NODE\r\n pwsh "), ["NODE", "pwsh"]);
assert.equal(parseReportRuleRejection("report-rule-invalid:cpu_throttle_frequency:clearThreshold:clear-on-wrong-side")?.reason, "clear-on-wrong-side");
assert.equal(parseReportRuleRejection("different:code"), null);
console.log("Report rule decoders and editing model passed.");
