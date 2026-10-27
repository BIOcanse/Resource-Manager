import { liveText } from "./liveLocator.js";
import { productUpdateCopy } from "../../src/features/settings/productUpdatePresentation.ts";
import { componentFixture } from "./componentFixtures.js";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { checkTypeXDisplayDialog, checkType3DisplayDialog, checkRecordedDisplayDialog } from "./displayEdidDialogProbe.js";
import { checkDeclarationDisplayDialog } from "./displayEdidDeclarationDialogProbe.js";
import usabilityProbe from "./usabilityProbe.js";
import { checkCpuMetricFields, checkDeviceMetricFields, checkFieldCardDisplay, enableMetricFields, seedFieldCards } from "./metricFieldsProbe.js";
import { checkResourceTableSourceText } from "./resourceTableSourceChoiceJourney.js";
import { checkResourceBarSourceText } from "./resourceBarSourceChoiceJourney.js";
import { checkNetworkConnectionStates } from "./deviceTopologyConnectionProbe.js";
import { checkVolumeFreeSpace } from "./deviceTopologyStorageProbe.js";
import { helpHintText } from "./tooltipProbe.js";

// 九语旅程按这个顺序逐个切换语言：每种语言都从前一种切过来，第一种是初始的 zh-CN。
const initialLanguage = "zh-CN";
export function localizationLanguageOrder() {
  return [...languageOptions.filter(option => option.id !== "system" && option.id !== "en-US"),
    languageOptions.find(option => option.id === "en-US")].map(option => option.id);
}

// options.languages：只检查其中几种语言（语言 id 数组），给浏览器门按语言拆成并行任务用；不给就检查全部。
// 每种被检查的语言仍从它在完整顺序里的前一种语言切过来：当前语言不是那一种时，先只切换到那一种
// （不做它的检查），所以每次「旧语言 → 新语言」的切换和完整运行时完全相同。
export default async function localizationJourney(page, baseUrl, options = {}) {
  const failures = [];
  const typeXChecks = [];
  const declarationChecks = [];
  page.on("pageerror", error => failures.push(String(error)));
  page.on("console", message => {
    const expectedFailure = failProtectionSave && message.location().url.includes("/api/optimization/protection/software/");
    if (message.type() === "error" && !expectedFailure) failures.push(message.text());
  });
  await page.route("**/api/settings/report-rules", route => route.fulfill({ json: { rules: [] } }));
  const scenario = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ runtimeEffectOwners: true, gpuPlacementEnabled: true })
  });
  if (!scenario.ok) throw new Error("Unable to enable the mock runtime controls");
  await enableMetricFields(baseUrl);
  await seedFieldCards(page);
  const metricFieldChecks = [];
  const volumeFreeSpaceChecks = [];
  const fieldCardChecks = [];
  const resourceTableSourceChecks = [];
  const resourceBarSourceChecks = [];
  let verificationFails = false;
  let verificationRequests = 0;
  const pendingVerificationReleases = [];
  const verificationStartWaiters = [];
  await page.route("**/api/components", route => route.fulfill({ json: [componentFixture()] }));
  await page.route("**/api/components/hardware-bridge/verify", async route => {
    if (route.request().method() !== "POST") throw new Error("Verification must use the existing POST endpoint");
    verificationRequests++;
    await new Promise(resolve => {
      const release = () => resolve();
      const waiter = verificationStartWaiters.shift();
      if (waiter) waiter(release);
      else pendingVerificationReleases.push(release);
    });
    await route.fulfill({ json: {
      state: verificationFails ? "runtimeFilesUnavailable" : "installed",
      message: { domain: 1, code: verificationFails ? 24 : 13, args: [] }
    } });
  });
  async function verifyWhileResponsePending(button, assertionMessage, expectedMessage) {
    const responseRelease = new Promise(resolve => {
      const release = pendingVerificationReleases.shift();
      if (release) resolve(release);
      else verificationStartWaiters.push(resolve);
    });
    let release;
    try {
      await button.click();
      release = await responseRelease;
      if (!await button.isDisabled()) throw new Error(assertionMessage);
    } finally {
      if (release) release();
      else void responseRelease.then(resolve => resolve());
    }
    await liveText(page, expectedMessage).waitFor();
  }
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
  let errorCode = null;
  const history = [
    { choice: "latest", version: "0.3.9", channel: "preview", selectable: true },
    { choice: "latestStable", version: "0.3.8", channel: "stable", selectable: true },
    { choice: "version:0.3.9", version: "0.3.9", series: "0.3", channel: "preview", selectable: true },
    { choice: "version:0.3.8", version: "0.3.8", series: "0.3", channel: "stable", selectable: true },
    { choice: "version:0.3.1", version: "0.3.1", series: "0.3", channel: "stable", selectable: false,
      eligibility: "notNewer" }
  ];
  await page.route("**/api/updates/product/versions", route => route.fulfill({ json: {
    installedVersion: "0.3.5", status: "loaded", complete: true, checkedAt: "2026-10-01T12:00:00Z", options: history
  } }));
  // 更新状态来自统一订阅通道的推送（mock 场景值 productUpdateStatus）。
  const publishUpdateStatus = async () => {
    const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ productUpdateStatus: { stage, errorCode, nextCheckAt: "2026-10-27T08:00:00Z", onlineTimeSinceLastCheckSeconds: 61 } }) });
    if (!response.ok) throw new Error(`update status scenario rejected: ${response.status}`);
  };
  // 分片从串行循环的同一轮入场：前轮已经验证成功/失败各一次，并停在 verifying 更新状态。
  // 其他持久化输入由 seedFieldCards 和各检查显式恢复（空资源条、自动来源、固件所有权、未保护）。
  const firstLanguageIndex = localizationLanguageOrder().findIndex(id => !options.languages?.length || options.languages.includes(id));
  if (firstLanguageIndex > 0) {
    verificationFails = true;
    verificationRequests = firstLanguageIndex * 2;
    stage = "verifying";
    await publishUpdateStatus();
  }
  await page.setViewportSize({ width: 1180, height: 850 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  let previous = initialLanguage;
  const checked = [];
  const enteredFrom = {};
  const enteredState = {};
  const order = localizationLanguageOrder().map(id => languageOptions.find(option => option.id === id));
  const languageFilter = options.languages;
  const unknownLanguages = (languageFilter ?? []).filter(id => !order.some(option => option.id === id));
  if (unknownLanguages.length > 0) throw new Error(`Unknown localization languages: ${unknownLanguages.join(", ")}`);
  const offered = order.filter(option => !languageFilter?.length || languageFilter.includes(option.id));
  if (offered.length === 0) throw new Error("The localization journey was given an empty language list");
  async function switchLanguage(from, option) {
    const oldApp = await loadAppCopy(from);
    const oldSettings = await loadSettingsText(from);
    await page.getByRole("navigation").getByRole("button", { name: oldApp.page.settings, exact: true }).click();
    await page.getByRole("button", { name: oldSettings.sections.appearance, exact: true }).click();
    await page.getByRole("button", { name: oldSettings.appearance.languageSelectLabel, exact: true }).click();
    await page.getByRole("option", { name: `${option.nativeLabel} · ${option.label}`, exact: true }).click();
    const save = page.getByRole("button", { name: oldSettings.actions.save, exact: true });
    if (option.id !== from) await save.click();
    await page.waitForFunction(language => document.documentElement.lang === language, option.id);
  }
  for (const option of offered) {
    const position = order.indexOf(option);
    const predecessor = position === 0 ? initialLanguage : order[position - 1].id;
    if (previous !== predecessor) {
      await switchLanguage(previous, order[position - 1]);
      previous = predecessor;
    }
    enteredFrom[option.id] = previous;
    enteredState[option.id] = { stage, verificationFails, verificationRequests, protectedSoftware };
    await switchLanguage(previous, option);
    const app = await loadAppCopy(option.id);
    const settings = await loadSettingsText(option.id);
    await liveText(page, settings.appearance.settingsLanguageDescription).waitFor();
    await page.getByRole("button", { name: settings.appearance.languageSelectLabel, exact: true }).waitFor();
    // 动画风格这一行和自动关闭开关：标题、说明和五个风格名都是这种语言的。
    await liveText(page, settings.appearance.animationStyleDescription).waitFor();
    await liveText(page, settings.appearance.autoDisableAnimationsDescription).waitFor();
    await page.getByRole("checkbox", { name: settings.appearance.autoDisableAnimationsTitle, exact: true }).waitFor();
    const styleGroup = page.getByRole("radiogroup", { name: settings.appearance.animationStyleTitle, exact: true });
    const styleLabels = (await styleGroup.getByRole("radio").allTextContents()).map(text => text.trim());
    if (JSON.stringify(styleLabels) !== JSON.stringify(settings.animationStyleOptions.map(option => option.label))) {
      throw new Error(`${option.id}: animation style options are ${JSON.stringify(styleLabels)}`);
    }

    await page.getByRole("button", { name: settings.sections.runtimePolicy, exact: true }).click();
    await liveText(page, settings.performance.gpuOverflowTitle).waitFor();
    await liveText(page, settings.performance.gpuOverflowDescription).waitFor();
    await liveText(page, settings.performance.realtimeActionBudgetDescription).waitFor();
    const budgetMode = page.getByRole("radiogroup", { name: settings.performance.realtimeActionBudgetTitle, exact: true });
    for (const budgetOption of settings.performance.realtimeActionBudgetModeOptions) {
      const radio = budgetMode.getByRole("radio", { name: budgetOption.label, exact: true });
      if (!await radio.isVisible() || await radio.isChecked() !== (budgetOption.id === "fixed"))
        throw new Error(`${option.id}: localized realtime budget mode is incorrect: ${budgetOption.id}`);
    }
    for (const [label, value, max] of [
      [settings.performance.realtimeActionBudgetCountLabel, "32", "8192"],
      [settings.performance.realtimeActionBudgetPercentLabel, "5", "100"]
    ]) {
      const input = page.getByRole("spinbutton", { name: label, exact: true });
      if (!await input.isEditable() || await input.inputValue() !== value || await input.getAttribute("min") !== "1"
        || await input.getAttribute("max") !== max) throw new Error(`${option.id}: localized budget control is incorrect: ${label}`);
    }
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
    await checkNetworkConnectionStates(page, app, option.id, baseUrl);
    volumeFreeSpaceChecks.push(await checkVolumeFreeSpace(page, app, option.id, baseUrl));
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
    // 资源表内存列的来源选择：来源名、选项、说明和选了来源后的表头。
    resourceTableSourceChecks.push(await checkResourceTableSourceText(page, app, option.id));
    // 物理内存资源条的来源选择：来源名、选项（自动选择写出实际读的字段）和说明。
    resourceBarSourceChecks.push(await checkResourceBarSourceText(page, app, option.id));
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
    // 交还固件的说明在三选一旁边的问号里（选项名一行、说明一行）。
    const firmwareHint = await helpHintText(boost.locator("xpath=following-sibling::button[contains(@class, 'help-hint')][1]"));
    if (firmwareHint !== `${app.control.toggleChoice.firmware}\n${app.control.toggleChoice.firmwareHint}`)
      throw new Error(`${option.id}: Return-to-firmware choice lost its localized explanation: ${JSON.stringify(firmwareHint)}`);
    await page.getByRole("radio", { name: app.control.ownership.firmware, exact: true }).click();

    await page.getByRole("navigation").getByRole("button", { name: app.page.components, exact: true }).click();
    const runtimeLabels = {
      "zh-CN": "运行时管理", "zh-TW": "執行時管理", "en-US": "Runtimes",
      "ja-JP": "ランタイム", "ko-KR": "런타임 관리", "fr-FR": "Environnements d’exécution",
      "de-DE": "Laufzeitumgebungen", "es-ES": "Entornos de ejecución", "ru-RU": "Среды выполнения"
    };
    await page.getByRole("tab", { name: runtimeLabels[option.id], exact: true }).waitFor();
    await page.getByRole("tab", { name: `${app.managementRole.dependency} 1`, exact: true }).click();
    const verify = page.getByRole("button", { name: app.management.verify, exact: true });
    const beforeRequests = verificationRequests;
    verificationFails = false;
    await verifyWhileResponsePending(verify, `${option.id}: verification allows duplicate clicks while pending`, app.backendMessage.dependency.componentFilesUnverified);
    await verify.waitFor({ state: "visible" });
    await page.waitForFunction(() => !document.querySelector('.component-actions button:disabled'));
    verificationFails = true;
    await verifyWhileResponsePending(verify, `${option.id}: failed verification allows duplicate clicks while pending`, app.backendMessage.dependency.runtimeFilesUnavailable);
    if (verificationRequests !== beforeRequests + 2) throw new Error(`${option.id}: wrong verification request count`);
    await page.getByRole("tablist", { name: app.management.categoryNav })
      .getByRole("tab", { name: new RegExp(app.softwareKind.other) }).click();
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
    await page.waitForFunction(() => !document.querySelector('.policy-toggle:not([data-exit-ghost] *) input')?.disabled);
    await protection.check();
    await page.waitForFunction(() => !document.querySelector('.policy-toggle:not([data-exit-ghost] *) input')?.disabled);
    if (!await protection.isChecked()) throw new Error(`${option.id}: protection not enabled`);
    await details.locator('button.icon-button').first().click();
    await card.getByRole("button", { name: app.management.softwareSettings, exact: true }).click();
    await policyTab.click();
    await page.waitForFunction(() => !document.querySelector('.policy-toggle:not([data-exit-ghost] *) input')?.disabled);
    if (!await protection.isChecked()) throw new Error(`${option.id}: protection did not reload`);
    if (await details.getByRole("checkbox", { name: app.softwareDetail.participation.cpu, exact: true }).isChecked())
      throw new Error(`${option.id}: protection changed an opt-out switch`);
    await protection.uncheck();
    await page.waitForFunction(() => !document.querySelector('.policy-toggle:not([data-exit-ghost] *) input')?.disabled);
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
    errorCode = null;
    await publishUpdateStatus();
    await page.getByRole("button", { name: settings.sections.updates, exact: true }).click();
    const modeGroup = page.getByRole("radiogroup", { name: settings.updates.modeTitle, exact: true });
    for (const mode of settings.updates.modeOptions) await modeGroup.getByRole("radio", { name: mode.label, exact: true }).waitFor();
    await page.getByRole("checkbox", { name: settings.updates.periodicTitle, exact: true }).waitFor();
    await liveText(page, settings.updates.intervalLabel(24, 168)).waitFor();
    await liveText(page, settings.updates.internetNote).waitFor();
    await liveText(page, settings.updates.manualCheckNote).waitFor();
    await page.getByRole("checkbox", { name: settings.updates.inAppNoticeTitle, exact: true }).waitFor();
    await page.getByRole("checkbox", { name: settings.updates.systemNoticeTitle, exact: true }).waitFor();
    await liveText(page, settings.updates.systemNoticeDescription).waitFor();
    await page.getByRole("checkbox", { name: settings.updates.accessRequestNoticeTitle, exact: true }).waitFor();
    await liveText(page, settings.updates.accessRequestNoticeDescription).waitFor();
    await liveText(page, `${settings.updates.nextCheck}2026-10-27 08:00 UTC`).waitFor();
    await liveText(page, `${settings.updates.connectedElapsed}0:01:01`).waitFor();
    await liveText(page, `${settings.updates.lastChecked}2026-10-01 12:00 UTC`).waitFor();
    await liveText(page, settings.updates.versionsDescription).waitFor();
    await liveText(page, settings.updates.latest).waitFor();
    await liveText(page, settings.updates.latestStable).waitFor();
    const oldVersion = page.locator('input[value="version:0.3.1"]');
    // 收起的系列：旧版本在折叠内容里，内容 inert（不能聚焦、辅助技术读不到）。
    const collapsed = async () => oldVersion.evaluate(element => element.closest("[inert]") !== null);
    if (!await collapsed()) throw new Error(`${option.id}: minor versions were not collapsed`);
    await page.locator(".version-selector-expand").click();
    if (await collapsed()) throw new Error(`${option.id}: expanding the series left its versions inert`);
    if (!await oldVersion.isDisabled()) throw new Error(`${option.id}: an old version can be selected`);
    await liveText(page, settings.updates.unavailableReasons["notNewer"]).waitFor();
    await page.locator('input[value="latest"]').check();
    const confirmation = page.waitForEvent("dialog");
    const click = page.getByRole("button", { name: settings.updates.prepare, exact: true }).click();
    const dialog = await confirmation;
    if (dialog.message() !== settings.updates.confirmPrepare("0.3.9")) throw new Error(`${option.id}: confirmation uses the wrong language`);
    await dialog.dismiss();
    await click;
    stage = "error";
    errorCode = "update.versionNotNewer";
    await publishUpdateStatus();
    await page.getByRole("button", { name: settings.sections.appearance, exact: true }).click();
    await page.getByRole("button", { name: settings.sections.updates, exact: true }).click();
    await page.getByRole("alert").filter({ hasText: productUpdateCopy(option.id).error(errorCode) }).waitFor();
    stage = "verifying";
    errorCode = null;
    await publishUpdateStatus();
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
  return { typeXChecks, declarationChecks, metricFieldChecks, volumeFreeSpaceChecks, fieldCardChecks, resourceTableSourceChecks, resourceBarSourceChecks, languages: checked, enteredFrom, enteredState, actualSavedSwitching: true, translatedControlAndUpdateUi: true,
    translatedComponentVerificationSuccessAndFailure: true,
    translatedProtectionEnableRemoveReloadAndFailure: true,
    foldedHistory: true, downgradeDisabled: true, localizedConsentCancelled: true };
}
