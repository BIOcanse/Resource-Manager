import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { checkTypeXDisplayDialog, checkType3DisplayDialog, checkRecordedDisplayDialog } from "./displayEdidDialogProbe.js";
import { checkDeclarationDisplayDialog } from "./displayEdidDeclarationDialogProbe.js";
import usabilityProbe from "./usabilityProbe.js";
import { checkCpuMetricFields, checkDeviceMetricFields, checkFieldCardDisplay, enableMetricFields, seedFieldCards } from "./metricFieldsProbe.js";

export default async function localizationJourney(page, baseUrl) {
  const failures = [];
  const typeXChecks = [];
  const declarationChecks = [];
  page.on("pageerror", error => failures.push(String(error)));
  page.on("console", message => {
    const expectedFailure = failProtectionSave && message.location().url.includes("/api/optimization/protection/software/");
    if (message.type() === "error" && !expectedFailure) failures.push(message.text());
  });
  const scenario = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ runtimeEffectOwners: true, gpuPlacementEnabled: true })
  });
  if (!scenario.ok) throw new Error("Unable to enable the mock runtime controls");
  await enableMetricFields(baseUrl);
  await seedFieldCards(baseUrl);
  const metricFieldChecks = [];
  const fieldCardChecks = [];
  let verificationFails = false;
  let verificationRequests = 0;
  await page.route("**/api/components", route => route.fulfill({ json: [{
    definition: { id: "fan-control-core", name: "FanControlCore", managementRole: "Dependency", capabilities: [] },
    installed: false, canInstall: true, canVerify: true, providers: [], message: { domain: 1, code: 9, args: [] }
  }] }));
  await page.route("**/api/components/fan-control-core/verify", async route => {
    if (route.request().method() !== "POST") throw new Error("Verification must use the existing POST endpoint");
    verificationRequests++;
    await new Promise(resolve => setTimeout(resolve, 200));
    await route.fulfill({ json: {
      state: verificationFails ? "runtimeFilesUnavailable" : "Installed",
      message: { domain: 1, code: verificationFails ? 24 : 13, args: [] }
    } });
  });
  let protectedSoftware = null;
  let failProtectionSave = false;
  await page.route("**/api/optimization/protection", route => route.fulfill({ json: protectedSoftware ? [{
    softwareId: protectedSoftware, targetKey: `software:${protectedSoftware}`, protectionLevel: 2
  }] : [] }));
  await page.route("**/api/optimization/protection/software/*", async route => {
    if (route.request().method() !== "PUT") throw new Error("Protection must use PUT");
    const enabled = route.request().postDataJSON().enabled;
    if (failProtectionSave) { await route.fulfill({ status: 503, body: "fixture" }); return; }
    protectedSoftware = enabled ? decodeURIComponent(new URL(route.request().url()).pathname.split("/").at(-1)) : null;
    await route.fulfill({ json: { softwareId: protectedSoftware } });
  });
  await page.route("**/api/targeted-recordings", route => route.fulfill({ json: [] }));
  let stage = "idle";
  const history = [
    { choice: "latest", version: "0.3.9", channel: "preview", selectable: true },
    { choice: "latestStable", version: "0.3.8", channel: "stable", selectable: true },
    { choice: "version:0.3.9", version: "0.3.9", series: "0.3", channel: "preview", selectable: true },
    { choice: "version:0.3.8", version: "0.3.8", series: "0.3", channel: "stable", selectable: true },
    { choice: "version:0.3.1", version: "0.3.1", series: "0.3", channel: "stable", selectable: false,
      unavailableReason: "只能选择高于当前安装版本的发行版。" }
  ];
  await page.route("**/api/updates/product/versions", route => route.fulfill({ json: {
    installedVersion: "0.3.5", status: "loaded", complete: true, checkedAt: "2026-10-01T12:00:00Z", options: history
  } }));
  await page.route("**/api/updates/product/status", route => route.fulfill({ json: { stage } }));
  await page.setViewportSize({ width: 1180, height: 850 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  let previous = "zh-CN";
  const checked = [];
  // RM_LOCALIZATION_LANGUAGES（逗号分隔的语言 id）只跑其中几种，用于把九语旅程拆成几个进程并行；不设就跑全部。
  const languageFilter = process.env.RM_LOCALIZATION_LANGUAGES?.split(",").map(id => id.trim()).filter(Boolean);
  const offered = [...languageOptions.filter(option => option.id !== "system" && option.id !== "en-US"),
    languageOptions.find(option => option.id === "en-US")]
    .filter(option => !languageFilter?.length || languageFilter.includes(option.id));
  if (offered.length === 0) throw new Error(`RM_LOCALIZATION_LANGUAGES matched no language: ${process.env.RM_LOCALIZATION_LANGUAGES}`);
  for (const option of offered) {
    const oldApp = await loadAppCopy(previous);
    const oldSettings = await loadSettingsText(previous);
    await page.getByRole("navigation").getByRole("button", { name: oldApp.page.settings, exact: true }).click();
    await page.getByRole("button", { name: oldSettings.sections.appearance, exact: true }).click();
    await page.getByRole("button", { name: oldSettings.appearance.languageSelectLabel, exact: true }).click();
    await page.getByRole("option", { name: `${option.nativeLabel} · ${option.label}`, exact: true }).click();
    const save = page.getByRole("button", { name: oldSettings.actions.save, exact: true });
    if (option.id !== previous) await save.click();
    await page.waitForFunction(language => document.documentElement.lang === language, option.id);
    const app = await loadAppCopy(option.id);
    const settings = await loadSettingsText(option.id);
    await page.getByText(settings.appearance.settingsLanguageDescription, { exact: true }).waitFor();
    await page.getByRole("button", { name: settings.appearance.languageSelectLabel, exact: true }).waitFor();

    await page.getByRole("button", { name: settings.sections.performance, exact: true }).click();
    await page.getByText(settings.performance.gpuOverflowTitle, { exact: true }).waitFor();
    await page.getByText(settings.performance.gpuOverflowDescription, { exact: true }).waitFor();
    for (const [label, value] of [
      [settings.performance.gpuOverflowUsageLabel, "95"],
      [settings.performance.gpuOverflowDedicatedMemoryLabel, "80"]
    ]) {
      const threshold = page.getByRole("spinbutton", { name: label, exact: true });
      if (await threshold.inputValue() !== value || await threshold.getAttribute("min") !== "1"
        || await threshold.getAttribute("max") !== "100")
        throw new Error(`${option.id}: localized GPU overflow setting has incorrect value or bounds: ${label}`);
    }

    const scenarioResponse = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ displayEdid: "ready" }) });
    if (!scenarioResponse.ok) throw new Error(option.id + ": EDID fixture unavailable");
    await page.getByRole("navigation").getByRole("button", { name: app.page.details, exact: true }).click();
    const topology = page.locator(".device-topology-panel"); await topology.waitFor();
    await topology.locator(".device-port-item").first().click();
    await checkRecordedDisplayDialog(page, app);
    for (const variant of ["type3", "type3Inactive", "type3Unknown", "type3"]) {
      const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ displayEdid: variant }) });
      if (!response.ok) throw new Error(option.id + ": TypeIII fixture unavailable");
      await checkType3DisplayDialog(page, app, variant);
    }
    const typeXResponse = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ displayEdid: "typex" }) });
    if (!typeXResponse.ok) throw new Error(option.id + ": TypeX real wire input unavailable");
    typeXChecks.push({ language: option.id, ...await checkTypeXDisplayDialog(page, app) });
    for (const variant of ["typeVVi", "typeVViClock", "adaptiveSync", "adaptiveSyncZero", "dynamicRange"]) {
      const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ displayEdid: variant }) });
      if (!response.ok) throw new Error(option.id + ": " + variant + " fixture unavailable");
      const result = await checkDeclarationDisplayDialog(page, app, variant);
      declarationChecks.push({ language: option.id, variant, declarations: result.declarations, openRefresh: result.openRefresh });
    }
    const original = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ displayEdid: "ready" }) });
    if (!original.ok) throw new Error(option.id + ": original EDID fixture unavailable");
    await checkRecordedDisplayDialog(page, app);
    // 规范字段的新显示：CPU 拓扑的 NUMA 行与有效频率，设备详情的 NPU／AMD NPU 两节。
    await checkCpuMetricFields(page, app, option.id);
    metricFieldChecks.push(await checkDeviceMetricFields(page, app, option.id));
    // 监视控制台的设备字段卡片：字段名、设备种类名、「设备不在」。
    fieldCardChecks.push(await checkFieldCardDisplay(page, app, option.id));
    await page.getByRole("navigation").getByRole("button", { name: app.page.control, exact: true }).click();
    await page.locator(".control-group h3").filter({ hasText: app.control.kind.gpu }).waitFor();
    await page.locator(".control-readings dt").filter({ hasText: app.control.presentation.labels["gpu.temperature-limit"] }).waitFor();
    await page.locator(".control-capability-actual").filter({ hasText: app.control.presentation.toggleOff }).first().waitFor();
    await page.getByRole("radio", { name: app.control.ownership.app, exact: true }).waitFor();
    // X80：开关的三选一（交还固件／开启／关闭）按当前语言显示，交还固件带说明。
    await page.getByRole("radio", { name: app.control.ownership.app, exact: true }).click();
    const boost = page.getByRole("radiogroup", { name: app.control.presentation.labels["gpu.dynamic-boost-enabled"], exact: true });
    for (const label of [app.control.toggleChoice.firmware, app.control.presentation.toggleOn, app.control.presentation.toggleOff])
      await boost.getByRole("radio", { name: label, exact: true }).waitFor();
    if (await boost.getByRole("radio", { name: app.control.toggleChoice.firmware, exact: true }).getAttribute("title") !== app.control.toggleChoice.firmwareHint)
      throw new Error(`${option.id}: Return-to-firmware choice lost its localized explanation`);
    await page.getByRole("radio", { name: app.control.ownership.firmware, exact: true }).click();

    await page.getByRole("navigation").getByRole("button", { name: app.page.components, exact: true }).click();
    await page.getByRole("button", { name: `${app.managementRole.Dependency} 1`, exact: true }).click();
    const verify = page.getByRole("button", { name: app.management.verify, exact: true });
    const beforeRequests = verificationRequests;
    verificationFails = false;
    await verify.click();
    if (!await verify.isDisabled()) throw new Error(`${option.id}: verification allows duplicate clicks while pending`);
    await page.getByText(app.backendMessage.dependency.componentFilesUnverified, { exact: true }).waitFor();
    await verify.waitFor({ state: "visible" });
    await page.waitForFunction(() => !document.querySelector('.component-actions button:disabled'));
    verificationFails = true;
    await verify.click();
    await page.getByText(app.backendMessage.dependency.runtimeFilesUnavailable, { exact: true }).waitFor();
    if (verificationRequests !== beforeRequests + 2) throw new Error(`${option.id}: wrong verification request count`);
    await page.getByRole("navigation", { name: app.management.categoryNav })
      .getByRole("button", { name: new RegExp(app.softwareKind.other) }).click();
    const card = page.locator(".software-item").filter({ hasText: "示例软件" }).first();
    await card.getByRole("button", { name: app.management.softwareSettings, exact: true }).click();
    const details = page.getByRole("dialog", { name: "示例软件" });
    const policyTab = details.getByRole("tab", { name: app.softwareDetail.tab.policy, exact: true });
    await policyTab.click();
    // 保护与三个“不参与”开关同在策略页“调度参与”组，按当前语言显示，互不联动。
    await details.getByRole("heading", { name: app.softwareDetail.participation.title, exact: true }).waitFor();
    const protectionGroup = details.getByRole("group", { name: app.softwareDetail.protection.title, exact: true });
    await protectionGroup.getByText(app.softwareDetail.protection.hint, { exact: true }).waitFor();
    for (const [label, hint] of [
      [app.softwareDetail.participation.cpu, app.softwareDetail.participation.cpuHint],
      [app.softwareDetail.participation.memory, app.softwareDetail.participation.memoryHint],
      [app.softwareDetail.participation.gpu, app.softwareDetail.participation.gpuHint]]) {
      await details.getByRole("checkbox", { name: label, exact: true }).waitFor();
      await details.getByText(hint, { exact: true }).waitFor();
    }
    const protection = protectionGroup.getByRole("checkbox", { name: app.softwareDetail.protection.enabled, exact: true });
    await page.waitForFunction(() => !document.querySelector('.policy-toggle input')?.disabled);
    await protection.check();
    await page.waitForFunction(() => !document.querySelector('.policy-toggle input')?.disabled);
    if (!await protection.isChecked()) throw new Error(`${option.id}: protection not enabled`);
    await details.locator('button.icon-button').first().click();
    await card.getByRole("button", { name: app.management.softwareSettings, exact: true }).click();
    await policyTab.click();
    await page.waitForFunction(() => !document.querySelector('.policy-toggle input')?.disabled);
    if (!await protection.isChecked()) throw new Error(`${option.id}: protection did not reload`);
    if (await details.getByRole("checkbox", { name: app.softwareDetail.participation.cpu, exact: true }).isChecked())
      throw new Error(`${option.id}: protection changed an opt-out switch`);
    await protection.uncheck();
    await page.waitForFunction(() => !document.querySelector('.policy-toggle input')?.disabled);
    if (await protection.isChecked()) throw new Error(`${option.id}: protection not removed`);
    failProtectionSave = true;
    // A failed save immediately reverts the switch. check() itself requires
    // the final state to stay checked, so click and assert the actual error result.
    await protection.click();
    await details.getByRole("alert").getByText(app.softwareDetail.protection.failed, { exact: true }).waitFor();
    if (await protection.isChecked()) throw new Error(`${option.id}: failed save changed displayed protection`);
    failProtectionSave = false;
    await details.locator('button.icon-button').first().click();

    await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
    stage = "idle";
    await page.getByRole("button", { name: settings.sections.updates, exact: true }).click();
    await page.getByRole("checkbox", { name: settings.updates.autoUpdateTitle, exact: true }).waitFor();
    await page.getByText(settings.updates.versionsDescription, { exact: true }).waitFor();
    await page.getByText(settings.updates.latest, { exact: true }).waitFor();
    await page.getByText(settings.updates.latestStable, { exact: true }).waitFor();
    const oldVersion = page.locator('input[value="version:0.3.1"]');
    if (await oldVersion.count() !== 0) throw new Error(`${option.id}: minor versions were not collapsed`);
    await page.locator(".version-selector-expand").click();
    if (!await oldVersion.isDisabled()) throw new Error(`${option.id}: an old version can be selected`);
    await page.getByText(settings.updates.unavailableReasons["只能选择高于当前安装版本的发行版。"], { exact: true }).waitFor();
    await page.locator('input[value="latest"]').check();
    const confirmation = page.waitForEvent("dialog");
    const click = page.getByRole("button", { name: settings.updates.prepare, exact: true }).click();
    const dialog = await confirmation;
    if (dialog.message() !== settings.updates.confirmPrepare("0.3.9")) throw new Error(`${option.id}: confirmation uses the wrong language`);
    await dialog.dismiss();
    await click;
    stage = "verifying";
    await page.getByRole("button", { name: settings.sections.appearance, exact: true }).click();
    await page.getByRole("button", { name: settings.sections.updates, exact: true }).click();
    await page.getByRole("status").filter({ hasText: settings.updates.stages.verifying }).waitFor();
    const layout = await usabilityProbe(page);
    if (layout.document.scrollWidth > layout.document.clientWidth
      || (layout.shell && layout.shell.scrollWidth > layout.shell.clientWidth)
      || layout.visibleUnnamedInputs.length > 0) {
      throw new Error(`${option.id}: translated UI overflows or contains unnamed visible controls`);
    }
    checked.push(option.id);
    previous = option.id;
  }
  if (failures.length) throw new Error(failures.join("\n"));
  return { typeXChecks, declarationChecks, metricFieldChecks, fieldCardChecks, languages: checked, actualSavedSwitching: true, translatedControlAndUpdateUi: true,
    translatedComponentVerificationSuccessAndFailure: true,
    translatedProtectionEnableRemoveReloadAndFailure: true,
    foldedHistory: true, downgradeDisabled: true, localizedConsentCancelled: true };
}
