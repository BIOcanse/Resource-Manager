import assert from "node:assert/strict";
import { applyLanguage, currentLanguage, uiText } from "../src/text.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { softwareDescription, softwareSourceLabels, softwareUninstallDescription, softwareRecordName } from "../src/presentation/softwareFacts.ts";
import { decodeSoftwareRecord } from "../src/features/management/softwareRecordDecoder.ts";
import type { SoftwareRecord } from "../src/types.ts";

const expectedEndpoint = {
  "zh-CN": "在线", "zh-TW": "線上", "en-US": "Online", "ja-JP": "オンライン", "ko-KR": "온라인",
  "fr-FR": "En ligne", "de-DE": "Verbunden", "es-ES": "En línea", "ru-RU": "Подключено"
};
const base: SoftwareRecord = {
  id: "software", name: "Vendor application", kind: "adapted", state: "active", sourceFacts: [{ kind: "adapter" }],
  registrationFacts: { kind: "adapter", processCount: 2, serviceCount: 3, endpointState: "online" },
  rootPaths: [], identityConfirmed: true, requiresRootPathConfirmation: false,
  operations: { canUninstall: false, uninstallKind: "None", uninstallConstraint: "missing_strategy" }
};
for (const language of languageOptions) {
  if (language.id === "system") continue;
  applyLanguage(language.id);
  await Promise.all([loadAppCopy(language.id), loadSettingsText(language.id)]);
  await Promise.resolve();
  assert.equal(currentLanguage(), language.id);
  const copy = uiText.softwareFacts;
  assert.equal(copy.endpointState.online, expectedEndpoint[language.id as keyof typeof expectedEndpoint]);
  for (const count of [0, 2, 10]) {
    const record = { ...base, registrationFacts: { ...base.registrationFacts!, processCount: count, serviceCount: count } };
    assert.ok(softwareDescription(record).includes(copy.processCount(count)), `${language.id}: process count ${count}`);
    assert.ok(softwareDescription(record).includes(copy.serviceCount(count)), `${language.id}: service count ${count}`);
  }
  for (const endpointState of ["online", "unreachable", "unsupported"] as const) {
    assert.ok(softwareDescription({ ...base, registrationFacts: { ...base.registrationFacts!, endpointState } }).includes(copy.endpointState[endpointState]));
  }
  const portable = { ...base, executablePaths: ["C:/game.exe"], registrationFacts: { kind: "portable", processCount: 0, serviceCount: 0 } } as SoftwareRecord;
  assert.ok(softwareDescription(portable).includes(copy.executableCount(1)));
  assert.ok(softwareDescription(portable).includes(copy.rootConfirmed));
  assert.ok(softwareDescription({ ...portable, requiresRootPathConfirmation: true }).includes(uiText.managementPage.rootNeedsCheck));
  for (const kind of Object.keys(copy.source) as Array<keyof typeof copy.source>) {
    assert.deepEqual(softwareSourceLabels({ ...base, sourceFacts: [{ kind }] }), [copy.source[kind]]);
  }
  const catalog = softwareSourceLabels({ ...base, sourceFacts: [{ kind: "identity_catalog", catalogVersion: "1.2.3", catalogSource: "vendor" }] })[0];
  assert.ok(catalog.includes("1.2.3") && catalog.includes("vendor"));
  const policy = softwareDescription({ ...base, sourceFacts: [{ kind: "policy", policyGroupId: "group", policyGroupName: "My group" }] });
  assert.ok(policy.includes(`${copy.source.policy}: My group`));
  for (const constraint of ["none", "empty_managed_root", "missing_uninstall_entry", "manual_registration", "missing_strategy", "self", "portable"] as const) {
    const result = softwareUninstallDescription({ ...base, operations: { canUninstall: constraint === "none", uninstallKind: "WindowsUninstaller", uninstallConstraint: constraint } });
    assert.ok(result.trim(), `${language.id}: uninstall ${constraint}`);
  }
  assert.equal(softwareUninstallDescription(base), uiText.backendMessage.software.adaptedNoUninstall);
  assert.equal(softwareRecordName({ ...base, id: "dependency:shared-webview2-runtime", sourceFacts: [{ kind: "dependency" }] }), uiText.status.component.names.sharedWebView2Runtime);
}

const decoded = decodeSoftwareRecord(base);
assert.deepEqual(decoded.registrationFacts, base.registrationFacts);
for (const field of ["sources", "message", "messageCode", "displayKind"]) {
  assert.throws(() => decodeSoftwareRecord({ ...base, [field]: "BACKEND_PROSE" }), /removed presentation field/);
}
for (const field of ["uninstallLabel", "uninstallMessage", "uninstallLabelCode", "uninstallMessageCode"]) {
  assert.throws(() => decodeSoftwareRecord({ ...base, operations: { ...base.operations, [field]: "BACKEND_PROSE" } }), /removed presentation field/);
}
assert.throws(() => decodeSoftwareRecord({ ...base, sourceFacts: [{ kind: "unknown" }] }), /sourceFacts\[0\].kind/);
assert.throws(() => decodeSoftwareRecord({ ...base, registrationFacts: { ...base.registrationFacts, processCount: -1 } }), /processCount/);
assert.throws(() => decodeSoftwareRecord({ ...base, operations: { ...base.operations, uninstallConstraint: "unknown" } }), /uninstallConstraint/);
console.log("Software facts render in every selectable language and reject presentation wire fields.");
