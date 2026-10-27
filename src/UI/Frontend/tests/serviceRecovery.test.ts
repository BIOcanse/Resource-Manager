import assert from "node:assert/strict";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { recoveryPresentation } from "../src/features/settings/serviceRecoveryPresentation.ts";
import { decodeServiceRecovery } from "../src/data/appSettings/serviceRecoveryDecoder.ts";
import { defaultAppSettings, normalizeAppSettings } from "../src/stores/settingsStore.ts";

const defaults = defaultAppSettings().systemIntegration!;
assert.equal(defaultAppSettings().version, "1.0.37");
assert.equal(defaults.restartAfterCrashEnabled, true);
assert.equal(defaults.crashRestartDelaySeconds, 10);
assert.equal(defaults.crashRestartCount, 3);
for (const [delay, count, expectedDelay, expectedCount] of [[0, 0, 1, 1], [9999, 99, 3600, 10], [42, 7, 42, 7]]) {
  const settings = normalizeAppSettings({ systemIntegration: { ...defaults,
    restartAfterCrashEnabled: false, crashRestartDelaySeconds: delay, crashRestartCount: count } });
  assert.equal(settings.systemIntegration!.restartAfterCrashEnabled, false);
  assert.equal(settings.systemIntegration!.crashRestartDelaySeconds, expectedDelay);
  assert.equal(settings.systemIntegration!.crashRestartCount, expectedCount);
}

const actual = {
  state: "mismatch",
  configuredResetPeriodSeconds: 86400,
  actual: { resetPeriodSeconds: 172800,
    actions: [{ type: 1, delayMilliseconds: 37000 }, { type: 1, delayMilliseconds: 37000 }, { type: 0, delayMilliseconds: 0 }],
    failureActionsOnNonCrashFailures: false },
  skipReason: null, writeError: null, readError: null
};
const diagnostic = decodeServiceRecovery(actual);
assert.ok(Object.isFrozen(diagnostic.actual!.actions));
assert.throws(() => decodeServiceRecovery({ ...actual, actual: { ...actual.actual, actions: null } }));
assert.throws(() => decodeServiceRecovery({ ...actual, state: "pretend-applied" }));
assert.throws(() => decodeServiceRecovery({ ...actual, actual: { ...actual.actual, resetPeriodSeconds: -1 } }));
const selected = languageOptions.filter(option => option.id !== "system");
assert.equal(selected.length, 9);
const titles = new Set<string>();
for (const { id } of selected) {
  const copy = (await loadSettingsText(id)).systemIntegration;
  titles.add(copy.crashRecoveryTitle);
  assert.doesNotMatch(copy.crashRecoveryDescription, /SCM|ChangeService|SERVICE_CONFIG/);
  const view = recoveryPresentation(diagnostic, copy);
  assert.equal(view.status, copy.crashRecoveryMismatch);
  assert.equal(view.actualCount, "2");
  assert.equal(view.actualDelays, "37, 37");
  assert.equal(view.actualResetDays, "2");
  assert.ok(view.resetDescription.includes("1"), id);
  assert.ok(!view.resetDescription.includes("{days}"), id);
  assert.equal(recoveryPresentation(undefined, copy).actualCount, "—");
  for (const [state, status] of [
    ["applied", copy.crashRecoveryApplied], ["failed", copy.crashRecoveryFailed],
    ["skipped", copy.crashRecoverySkipped], ["notApplied", copy.crashRecoveryUnavailable]
  ] as const) assert.equal(recoveryPresentation({ ...diagnostic, state }, copy).status, status);
  assert.equal(recoveryPresentation({ ...diagnostic, actual: { ...diagnostic.actual!, actions: [{ type: 1, delayMilliseconds: 10 }] } }, copy).actualCount, "∞");
}
assert.equal(titles.size, 9, "Every selectable language needs its own title");
console.log("Service recovery defaults, bounds, actual-configuration decoder and nine-language presentation passed.");
