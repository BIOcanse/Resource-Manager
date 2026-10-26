import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { applyLanguage, uiText } from "../src/i18n/appTextStore.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { renderBackendMessage } from "../src/presentation/backendMessage.ts";

const read = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");
const workspace = read("features/management/ManagementWorkspace.tsx");
assert.equal(workspace.match(/<ComponentAcquisitionDialog\b/g)?.length, 1);
assert.ok(workspace.indexOf("<ComponentAcquisitionDialog") > workspace.lastIndexOf("</Show>"),
  "One shared acquisition dialog must remain outside both management branches");
const store = read("stores/managementStore.ts");
// The runtime page shows its install component: entering it reads the components too.
assert.match(read("features/management/useManagementInventory.ts"),
  /options\.inventoryActive\(\) \|\| options\.browserRuntimesActive\(\)/);
const remove = store.slice(store.indexOf("async function removeManualRegistration"), store.indexOf("async function uninstallSoftware"));
assert.match(remove, /record\.registrationFacts\?\.kind !== "manual"/);
assert.ok(remove.indexOf("confirmDialog") < remove.indexOf("await removeManualSoftware"));
assert.ok(remove.indexOf("await removeManualSoftware") < remove.indexOf("await refreshSoftware"));
assert.match(read("api.ts"), /\/api\/software\/manual\/\$\{encodeURIComponent\(id\)\}/);
assert.match(read("features/management/components/ManagementPage.tsx"), /props\.software\.registrationFacts\?\.kind === "manual"/);
assert.match(read("components/SoftwareDetailModal.tsx"), /model\(\)\.manualRegistration/);
assert.match(read("components/SoftwareDetailModal.tsx"), /metadataStale/);
assert.match(read("components/SoftwareDetailModal.tsx"), /onClick=\{props\.onRetryMetadata\}/);
assert.match(read("ui/task-center/TaskCenterDialog.tsx"), /renderBackendMessage\(item\(\)\.resultMessage/);
assert.match(read("ui/task-center/TaskCenterDialog.tsx"), /renderBackendMessage\(item\(\)\.errorMessage/);

for (const option of languageOptions.filter(option => option.id !== "system")) {
  await applyLanguage(option.id);
  assert.equal(renderBackendMessage({ domain: 4, code: 13, args: [] }), uiText.managementFeedback.managedRootRemoved);
  assert.equal(renderBackendMessage({ domain: 4, code: 14, args: [] }), uiText.managementFeedback.uninstallerLaunchRequested);
  for (const [index, key] of ["networkFailure", "fileUnavailable", "accessDenied", "requestRejected", "ioFailure", "unexpectedFailure", "noExecutionEvidence", "outcomeUncertain"].entries()) {
    assert.equal(renderBackendMessage({ domain: 6, code: index + 1, args: [] }),
      uiText.managementFeedback[key as keyof typeof uiText.managementFeedback], `${option.id}: ${key}`);
  }
  assert.notEqual(renderBackendMessage({ domain: 1, code: 22, args: [] }),
    renderBackendMessage({ domain: 1, code: 21, args: [] }), `${option.id}: installer launch and verified installation must differ`);
}
console.log("Management feedback contracts and nine-language message rendering passed.");
