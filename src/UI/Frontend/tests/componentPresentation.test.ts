import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { applyLanguage, currentLanguage, uiText } from "../src/text.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { componentDisplayName, componentPurpose, userFacingState } from "../src/presentation/userFacingText.ts";
import { componentCapabilityLabel, componentInstallNote, componentVersionFailure } from "../src/presentation/componentPresentation.ts";
import { inspectCopy, inspectTraditional } from "./localizationCoverage.ts";
import { decodeManagedComponent } from "../src/features/management/componentDecoder.ts";

const componentFacts = {
  definition: {
    id: "hardware-bridge", vendor: "Resource Manager", sourcePageUrl: "https://example.invalid",
    externalTermsUrl: null, managementRole: "dependency", isBundled: false,
    requiresElevation: false, requiresExternalTermsAcknowledgement: false,
    capabilities: [{ id: "hardware-bridge.power", providerId: "hardware-bridge", providerKind: "cpu" }]
  },
  state: "missing", installerSourceKind: "githubRelease", message: { domain: 1, code: 15, args: [] },
  installRoot: "C:\\Dependencies\\HardwareBridge", installerDirectory: "C:\\Dependencies\\Installers",
  installerPath: null, canDownload: true, canInstall: true, canVerify: true,
  installerAvailable: false, installed: false, providerActive: false,
  providers: [{ providerId: "hardware-bridge", vendor: "Resource Manager", providerKind: "cpu", state: "missing",
    message: { domain: 1, code: 15, args: [] }, runtimePath: null, capabilities: ["hardware-bridge.power"] }]
};
assert.deepEqual(decodeManagedComponent(componentFacts), componentFacts);
assert.throws(() => decodeManagedComponent({ ...componentFacts, stateLabel: "Installed" }), /stateLabel/);
assert.throws(() => decodeManagedComponent({ ...componentFacts, definition: { ...componentFacts.definition, installNote: "Old prose" } }), /installNote/);
assert.throws(() => decodeManagedComponent({ ...componentFacts, providers: [{ ...componentFacts.providers[0], message: "Old prose" }] }), /message/);

const componentIds = ["shared-webview2-runtime", "amd-smu-pawnio-provider", "intel-msr-pawnio-provider",
  "hardware-bridge", "hardware-sensors-pawnio-provider", "librehardwaremonitor-provider",
  "notebook-oem-fan-provider", "nvidia-nvml-provider", "nvidia-nvapi-provider", "amd-adlx-provider"];
const baseline = await loadAppCopy("en-US");
for (const language of languageOptions) {
  if (language.id === "system") continue;
  applyLanguage(language.id);
  await Promise.all([loadAppCopy(language.id), loadSettingsText(language.id)]);
  await Promise.resolve();
  assert.equal(currentLanguage(), language.id);
  assert.deepEqual(inspectCopy(language.id, "componentCatalog", uiText.componentCatalog, baseline.componentCatalog), []);
  for (const id of componentIds) {
    assert.notEqual(componentDisplayName(id), uiText.status.component.nameFallback, `${language.id}: ${id} name`);
    assert.notEqual(componentPurpose(id), uiText.status.component.purposeFallback, `${language.id}: ${id} purpose`);
    assert.ok(componentInstallNote(id).length > 20, `${language.id}: ${id} installation note`);
    if (language.id === "zh-TW") assert.deepEqual(inspectTraditional(id, componentInstallNote(id)), []);
  }
  assert.equal(componentCapabilityLabel("amd-smu-pawnio-provider.power"), uiText.componentCatalog.capabilities.power);
  assert.equal(componentCapabilityLabel("nvidia-gpu-cooler"), uiText.componentCatalog.capabilities.gpuCooler);
  assert.equal(componentCapabilityLabel("notebook-oem-fan.cpu-rpm"), uiText.componentCatalog.capabilities.cpuFanRpm);
  for (const code of ["network", "timeout", "invalid-data", "version-unorderable", "no-release", "no-stable-release", "catalog-failure"]) {
    assert.ok(componentVersionFailure(code)?.length, `${language.id}: ${code}`);
    assert.notEqual(componentVersionFailure(code), code);
  }
  assert.equal(componentVersionFailure(null), null);
  assert.equal(userFacingState("installed_unverified"), uiText.status.state.installedUnverified);
}
// The actual acquisition surface and detail consumer render frontend-owned text.
const dialog = readFileSync(new URL("../src/features/management/components/ComponentAcquisitionDialog.tsx", import.meta.url), "utf8");
assert.match(dialog, /componentDisplayName\(request.component.definition\?\.id\)/);
assert.match(dialog, /componentVersionFailure\(option.unavailableReasonCode\)/);
assert.doesNotMatch(dialog, /definition\?\.name/);
const detail = readFileSync(new URL("../src/app/useSoftwareActions.ts", import.meta.url), "utf8");
assert.match(detail, /componentInstallNote\(definition.id\)/);
assert.match(detail, /componentCapabilityLabel\(item.id\)/);
assert.doesNotMatch(detail, /component.stateLabel/);
console.log("Component catalog presentation passed for every selectable language.");
