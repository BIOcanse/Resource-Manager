import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { applyLanguage, currentLanguage, uiText } from "../src/text.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { componentDisplayName, componentPurpose, userFacingState } from "../src/presentation/userFacingText.ts";
import { componentCapabilityLabel, componentInstallNote, componentNeedsUpdate, componentVersionFailure, componentVerifyLabel } from "../src/presentation/componentPresentation.ts";
import { isSettledDependencyMessage, renderBackendMessage } from "../src/presentation/backendMessage.ts";
import { inspectCopy, inspectTraditional } from "./localizationCoverage.ts";
import { decodeManagedComponent } from "../src/features/management/componentDecoder.ts";
import type { ManagedComponent } from "../src/types.ts";

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
} satisfies ManagedComponent;
assert.deepEqual(decodeManagedComponent(componentFacts), componentFacts);
const missingPayload = { ...componentFacts, state: "payload_missing", installed: true,
  canDownload: false, canInstall: false, message: { domain: 1, code: 25, args: [] } };
assert.deepEqual(decodeManagedComponent(missingPayload), missingPayload);
assert.equal(componentNeedsUpdate(missingPayload), false);
assert.equal(isSettledDependencyMessage(missingPayload.message), false);
const missingPayloadUpdate = { ...missingPayload, canInstall: true, canVerify: false,
  message: { domain: 1, code: 26, args: [] } };
assert.deepEqual(decodeManagedComponent(missingPayloadUpdate), missingPayloadUpdate);
assert.equal(componentNeedsUpdate(missingPayloadUpdate), true);
const updateAvailable = { ...componentFacts, state: "update_available", installed: true,
  canDownload: false, canInstall: true, message: { domain: 1, code: 26, args: [] } };
assert.deepEqual(decodeManagedComponent(updateAvailable), updateAvailable);
assert.equal(componentNeedsUpdate(updateAvailable), true);
assert.equal(componentNeedsUpdate({ ...componentFacts, state: "installed", installed: false, canInstall: true }), false);
assert.equal(componentNeedsUpdate({ ...componentFacts, state: "update_available", installed: false, canInstall: false }), true);
assert.equal(isSettledDependencyMessage(updateAvailable.message), false);
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
  assert.equal(userFacingState("payload_missing"), uiText.status.state.payloadMissing);
  assert.equal(userFacingState("update_available"), uiText.status.state.updateAvailable);
  assert.equal(componentVerifyLabel("payload_missing"), uiText.componentCatalog.verifyAndRepair);
  assert.equal(componentVerifyLabel("installed"), uiText.management.verify);
  assert.equal(renderBackendMessage(missingPayload.message), uiText.backendMessage.dependency.payloadMissing);
  assert.equal(renderBackendMessage(updateAvailable.message), uiText.backendMessage.dependency.updateAvailable);
  assert.ok(uiText.componentCatalog.update.length > 0, `${language.id}: missing update action copy`);
  assert.ok(uiText.componentAcquisition.agreeAndUpdate.length > 0, `${language.id}: missing update terms copy`);
}
// The actual acquisition surface and detail consumer render frontend-owned text.
const dialog = readFileSync(new URL("../src/features/management/components/ComponentAcquisitionDialog.tsx", import.meta.url), "utf8");
assert.match(dialog, /componentDisplayName\(request.component.definition\?\.id\)/);
assert.match(dialog, /componentVersionFailure\(option.unavailableReasonCode\)/);
assert.match(dialog, /componentNeedsUpdate\(request\.component\)/);
assert.doesNotMatch(dialog, /request\.component\.state === "update_available"/);
assert.match(dialog, /agreeAndUpdate/);
assert.doesNotMatch(dialog, /definition\?\.name/);
const managementPage = readFileSync(new URL("../src/features/management/components/ManagementPage.tsx", import.meta.url), "utf8");
assert.match(managementPage, /componentNeedsUpdate\(props\.component\)/);
assert.match(managementPage, /if \(componentNeedsUpdate\(component\)\)/);
const managementStore = readFileSync(new URL("../src/stores/managementStore.ts", import.meta.url), "utf8");
assert.match(managementStore, /if \(componentNeedsUpdate\(component\)\)/);
assert.match(managementStore, /componentNeedsUpdate\(request\.component\) \? "verified"/);
const detail = readFileSync(new URL("../src/app/useSoftwareActions.ts", import.meta.url), "utf8");
assert.match(detail, /componentInstallNote\(definition.id\)/);
assert.match(detail, /componentCapabilityLabel\(item.id\)/);
assert.doesNotMatch(detail, /component.stateLabel/);
console.log("Component catalog presentation passed for every selectable language.");
