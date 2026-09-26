import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const clientRoot = fileURLToPath(new URL("../", import.meta.url));
const appRoot = fileURLToPath(new URL("../../", import.meta.url));
const uiCoreRoot = fileURLToPath(new URL("../../../../src/UI/Core/", import.meta.url));
const readClientSource = (relativePath: string) =>
  readFileSync(`${clientRoot}${relativePath}`, "utf8");
const readAppSource = (relativePath: string) =>
  readFileSync(`${appRoot}${relativePath}`, "utf8");
const readUiCoreSource = (relativePath: string) =>
  readFileSync(`${uiCoreRoot}${relativePath}`, "utf8");

const settingsSources = [
  readClientSource("src/types.ts"),
  readClientSource("src/stores/settingsStore.ts"),
  readClientSource("src/components/settings/DebugSettingsSection.tsx"),
  readClientSource("src/i18n/settingsTypes.ts"),
  readClientSource("src/i18n/settingsLocaleFactory.ts")
];

for (const source of settingsSources) {
  assert.doesNotMatch(source, /externalDebugEnabled|updateExternalDebug|externalDebugTitle/);
  assert.doesNotMatch(
    source,
    /loopbackAuthenticationDisabled|updateLoopbackAuthenticationDisabled|Allow Unauthenticated Local Debugging|允许免验证的本机调试/);
  assert.doesNotMatch(
    source,
    /smartOptimizationScoreOnlyEnabled|smartOptimizationPerformanceLogEnabled|updateSmartOptimizationScoreOnly|updateSmartOptimizationPerformanceLog/);
}

assert.match(readClientSource("src/types.ts"), /hostManagerSmartCoordinatorScoreOnlyEnabled/);
assert.match(readClientSource("src/types.ts"), /hostManagerSmartCoordinatorPerformanceLogEnabled/);
assert.match(readClientSource("src/stores/settingsStore.ts"), /version: "1\.0\.23"/);
assert.doesNotMatch(readClientSource("src/types.ts"), /samplingDispatchMode/);
assert.doesNotMatch(readClientSource("src/stores/settingsStore.ts"), /samplingDispatchMode/);
assert.match(readClientSource("src/stores/settingsStore.ts"), /updateHostManagerSmartCoordinatorScoreOnly/);
assert.match(readClientSource("src/stores/settingsStore.ts"), /updateHostManagerSmartCoordinatorPerformanceLog/);

const retiredSelfOptimizationSources = [
  readClientSource("src/types.ts"),
  readClientSource("src/stores/settingsStore.ts"),
  readClientSource("src/components/SettingsPage.tsx"),
  readClientSource("src/components/settings/PerformanceSettingsSection.tsx"),
  readClientSource("src/i18n/settingsTypes.ts"),
  readClientSource("src/i18n/settingsLoader.ts"),
  readClientSource("src/i18n/settingsLocaleFactory.ts")
];
for (const source of retiredSelfOptimizationSources) {
  assert.doesNotMatch(source, /AppSelfOptimizationSettings|selfOptimization/);
}

assert.doesNotMatch(readClientSource("src/stores/settingsStore.ts"), /debug:reload/);
assert.doesNotMatch(readUiCoreSource("Shell/MainForm.cs"), /NativeUiDebug/);
assert.doesNotMatch(readUiCoreSource("Shell/MainForm.WebMessages.cs"), /debug:reload/);
assert.equal(
  existsSync(`${uiCoreRoot}Debug/NativeUiDebugServer.cs`),
  false,
  "the Native UI must not own a dedicated debug listener");
assert.match(readAppSource("Endpoints/DebugEndpoints.cs"), /\/api\/debug\/logs/);
assert.match(readAppSource("Program.cs"), /127\.0\.0\.1:9321/);
