import { runtimeDiscoveryLabel, softwareIssueLabel, softwareIssueMessage, softwareCategorySourceLabel } from "../src/presentation/softwareClassification.ts";
import { softwareKindLabel, softwareDisplayKindLabel } from "../src/text.ts";
import { normalizeSoftwareKind } from "../src/utils.ts";
import assert from "node:assert/strict";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { buildDisplayEdidView } from "../src/features/deviceTopology/displayEdidPresentation.ts";
import type { AppCopy } from "../src/i18n/copy/index.ts";
import type { ConcreteAppLanguageMode, SettingsTextBundle } from "../src/i18n/settingsTypes.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { applyLanguage, currentLanguage, currentSettingsText, uiText } from "../src/i18n/appTextStore.ts";
import { controlCapabilityLabel, controlObjectName } from "../src/features/control/controlPresentation.ts";
import { controlReasonText } from "../src/presentation/controlReasonText.ts";
import { systemProcessResultSummary } from "../src/presentation/systemProcessResult.ts";
import { userFacingErrorMessage, userFacingMessage, userFacingState } from "../src/presentation/userFacingText.ts";
import { ApiRequestError } from "../src/api/httpTransport.ts";
import { resourceMetricBasis } from "../src/presentation/metricLabels.ts";
import { hostManagerSchedulingExclusionReasons, hostManagerSchedulingFacts } from "../src/data/optimization/hostManagerSchedulingFactCodes.ts";
import { smartFailureFactText, smartFailureReasonText } from "../src/features/optimization/smartFailurePresentation.ts";
import { flattenCopy, inspectCopy, inspectParameters, inspectText, inspectTraditional } from "./localizationCoverage.ts";
import type { LocalizationIssue } from "./localizationCoverage.ts";

interface FunctionCase<T> {
  path: string;
  render: (copy: T) => string;
  parameters: readonly string[];
  requiresTranslation: boolean;
}
const appCases: FunctionCase<AppCopy>[] = [
  // 插值是拉丁字母开头时，中文和日文在边界上空一格（"正在加载 GPU 调度数据"）。
  { path: "observationNotice.loading", render: copy => copy.observationNotice.loading("GPU scheduling"), parameters: ["GPU scheduling"], requiresTranslation: true },
  { path: "resourceBreakdown.etwResidualProcess", render: copy => copy.resourceBreakdown.etwResidualProcess("worker.exe"), parameters: ["worker.exe", "DXGKrnl/VidMm"], requiresTranslation: true },
  { path: "smartReport.processTarget", render: copy => copy.smartReport.processTarget("Tool-A", 42), parameters: ["Tool-A", "42"], requiresTranslation: true },
  { path: "optimization.evaluationFailed", render: copy => copy.optimization.evaluationFailed(), parameters: [], requiresTranslation: true },
  { path: "optimization.rulesUnavailable", render: copy => copy.optimization.rulesUnavailable(3), parameters: ["3"], requiresTranslation: true },
  { path: "optimizationReport.strayProcess.title", render: copy => copy.optimizationReport.strayProcess.title("Tool-A"), parameters: ["Tool-A"], requiresTranslation: true },
  { path: "optimizationReport.strayProcess.summary", render: copy => copy.optimizationReport.strayProcess.summary("Tool-A", "51 MiB", "17 min"), parameters: ["Tool-A", "51 MiB", "17 min"], requiresTranslation: true },
  { path: "softwareActions.processActionResult", render: copy => copy.softwareActions.processActionResult(7, 19), parameters: ["7", "19"], requiresTranslation: true },
  { path: "control.needsComponent", render: copy => copy.control.needsComponent("NVAPI"), parameters: ["NVAPI"], requiresTranslation: true },
  { path: "control.instances.firstSeen", render: copy => copy.control.instances.firstSeen("2030-01-02"), parameters: ["2030-01-02"], requiresTranslation: true },
  { path: "control.accessLevel.confirmTitle", render: copy => copy.control.accessLevel.confirmTitle("root"), parameters: ["root"], requiresTranslation: true },
  { path: "control.takeoverCoversOthers", render: copy => copy.control.takeoverCoversOthers("Fan-A"), parameters: ["Fan-A"], requiresTranslation: true },
  { path: "control.presentation.fanName", render: copy => copy.control.presentation.fanName("92"), parameters: ["92"], requiresTranslation: true },
  { path: "control.presentation.gpuFanControllerName", render: copy => copy.control.presentation.gpuFanControllerName("14"), parameters: ["14"], requiresTranslation: true },
  { path: "control.presentation.readbackMismatch", render: copy => copy.control.presentation.readbackMismatch(31, 27, "W"), parameters: ["31", "27", "W"], requiresTranslation: true },
  { path: "control.presentation.clockRangeRejected", render: copy => copy.control.presentation.clockRangeRejected(123, 456, "MHz"), parameters: ["123", "456", "MHz"], requiresTranslation: true },
  { path: "control.presentation.presetNameLimit", render: copy => copy.control.presentation.presetNameLimit(64), parameters: ["64"], requiresTranslation: true },
  { path: "control.presentation.presetCountLimit", render: copy => copy.control.presentation.presetCountLimit(32), parameters: ["32"], requiresTranslation: true },
  { path: "diskUsage.scanFailed", render: copy => copy.diskUsage.scanFailed, parameters: [], requiresTranslation: true },
  { path: "diskUsage.freeOfTotal", render: copy => copy.diskUsage.freeOfTotal("31 GiB", "512 GiB"), parameters: ["31 GiB", "512 GiB"], requiresTranslation: true },
  { path: "diskUsage.scanTotals", render: copy => copy.diskUsage.scanTotals("29 GiB", 13, 7), parameters: ["29 GiB", "13", "7"], requiresTranslation: true },
  { path: "shell.documentTitle", render: copy => copy.shell.documentTitle("Test-page", "Resource Manager"), parameters: ["Test-page", "Resource Manager"], requiresTranslation: false }
];
const componentProviderStateCases = [
  ["active", "available"],
  ["installed", "installed"],
  ["installed_unverified", "installedUnverified"],
  ["payload_missing", "payloadMissing"],
  ["update_available", "updateAvailable"],
  ["ready_to_install", "readyToInstall"],
  ["manual_download_required", "manualDownload"],
  ["provider_missing", "unavailable"],
  ["missing", "missing"],
  ["runtime_available", "available"],
  ["driver_missing", "missing"],
  ["runtime_active", "available"],
  ["readyToInstall", "readyToInstall"],
  ["manualDownloadRequired", "manualDownload"],
  ["runtimeAvailable", "available"]
] as const;
for (const count of [0, 1, 37]) {
  appCases.push(
    { path: `diskUsage.fileCount(${count})`, render: copy => copy.diskUsage.fileCount(count), parameters: [String(count)], requiresTranslation: true },
    { path: `diskUsage.omitted(${count})`, render: copy => copy.diskUsage.omitted(count), parameters: [String(count)], requiresTranslation: true },
    { path: `shell.taskCenterActive(${count})`, render: copy => copy.shell.taskCenterActive(count), parameters: [String(count)], requiresTranslation: true },
    { path: `kernelLeakReport.unavailableHolders(${count})`, render: copy => copy.kernelLeakReport.unavailableHolders(count), parameters: [String(count)], requiresTranslation: true },
    { path: `smartFailure.exclusionTotal(${count})`, render: copy => copy.smartFailure.exclusionTotal(count), parameters: [String(count)], requiresTranslation: true }
  );
}
// 增长速率的单位写法：数字已按界面语言格式化，各语言只给“每秒”的写法（多数语言与英文同为 /s）。
appCases.push(
  { path: "kernelLeakReport.ratePerSecond", render: copy => copy.kernelLeakReport.ratePerSecond("+0,42"), parameters: ["+0,42"], requiresTranslation: false },
  { path: "kernelLeakReport.observation.reachedOfRequired", render: copy => copy.kernelLeakReport.observation.reachedOfRequired("0:02:30", "0:10:00"), parameters: ["0:02:30", "0:10:00"], requiresTranslation: true },
  { path: "kernelLeakReport.actions.riskText", render: copy => copy.kernelLeakReport.actions.riskText("Level-A", "Risk-B."), parameters: ["Level-A", "Risk-B."], requiresTranslation: true },
  { path: "kernelLeakReport.actions.riskLabel", render: copy => copy.kernelLeakReport.actions.riskLabel("Action-A"), parameters: ["Action-A"], requiresTranslation: true },
  { path: "smartFailure.nativePlanStatus", render: copy => copy.smartFailure.nativePlanStatus("Status-A"), parameters: ["Status-A"], requiresTranslation: true },
  { path: "smartFailure.detailSummary", render: copy => copy.smartFailure.detailSummary(64, 130), parameters: ["64", "130"], requiresTranslation: true }
);
// 内存与 CPU 取舍：档位号、已保存/默认档位、选项标题、重启前后两个值和秒数都经参数化文案拼出。
appCases.push(
  { path: "memoryCpuTier.tierLabel", render: copy => copy.memoryCpuTier.tierLabel(4), parameters: ["4"], requiresTranslation: true },
  { path: "memoryCpuTier.savedTier", render: copy => copy.memoryCpuTier.savedTier(2), parameters: ["2"], requiresTranslation: true },
  { path: "memoryCpuTier.defaultTier", render: copy => copy.memoryCpuTier.defaultTier(3), parameters: ["3"], requiresTranslation: true },
  { path: "memoryCpuTier.optionsTitle", render: copy => copy.memoryCpuTier.optionsTitle(5), parameters: ["5"], requiresTranslation: true },
  { path: "memoryCpuTier.currentlyRunning", render: copy => copy.memoryCpuTier.currentlyRunning("2 MiB"), parameters: ["2 MiB"], requiresTranslation: true },
  { path: "memoryCpuTier.afterRestart", render: copy => copy.memoryCpuTier.afterRestart("4 MiB"), parameters: ["4 MiB"], requiresTranslation: true },
  { path: "memoryCpuTier.seconds", render: copy => copy.memoryCpuTier.seconds("30"), parameters: ["30"], requiresTranslation: false }
);
const settingsCases: FunctionCase<SettingsTextBundle>[] = [
  { path: "updates.confirmPrepare", render: copy => copy.updates.confirmPrepare("4.7.9-beta.8"), parameters: ["4.7.9-beta.8"], requiresTranslation: true },
  { path: "systemIntegration.trustedAccess.requestExpired", render: copy => copy.systemIntegration.trustedAccess.requestExpired, parameters: [], requiresTranslation: true },
  { path: "systemIntegration.trustedAccess.reviewRequests", render: copy => copy.systemIntegration.trustedAccess.reviewRequests, parameters: [], requiresTranslation: true },
  { path: "systemIntegration.trustedAccess.defaultAccessLossWarning", render: copy => copy.systemIntegration.trustedAccess.defaultAccessLossWarning, parameters: [], requiresTranslation: true },
  { path: "systemIntegration.trustedAccess.scopeLabels.keys.manage", render: copy => copy.systemIntegration.trustedAccess.scopeLabels["keys.manage"], parameters: [], requiresTranslation: true },
  { path: "updates.intervalLabel", render: copy => copy.updates.intervalLabel(24, 168), parameters: ["24", "168"], requiresTranslation: true },
  { path: "updates.noticeMessage", render: copy => copy.updates.noticeMessage("4.7.9-beta.8"), parameters: ["4.7.9-beta.8"], requiresTranslation: true },
  { path: "updates.noticeReadyMessage", render: copy => copy.updates.noticeReadyMessage("4.7.9-beta.8"), parameters: ["4.7.9-beta.8"], requiresTranslation: true }
];

const englishApp = await loadAppCopy("en-US");
const englishSettings = await loadSettingsText("en-US");
const englishAppLeaves = flattenCopy(englishApp);
const englishSettingsLeaves = flattenCopy(englishSettings);
const issues: LocalizationIssue[] = [];
const languageResults: Array<{ language: string; appLeaves: number; settingsLeaves: number; functionCases: number; issues: number }> = [];
const languages = languageOptions.map(option => option.id).filter((id): id is ConcreteAppLanguageMode => id !== "system");
assert(languages.length > 0, "No selectable languages were checked.");
assert.equal(new Set(languages).size, languages.length, "Duplicate language option.");

// Check the real document-language and native-host notifications without opening
// the installed application or changing the user's persisted settings.
const documentState = { lang: "", dir: "" };
const hostMessages: unknown[] = [];
const oldDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
const oldWindow = Object.getOwnPropertyDescriptor(globalThis, "window");
Object.defineProperty(globalThis, "document", { configurable: true, value: { documentElement: documentState } });
Object.defineProperty(globalThis, "window", { configurable: true, value: { chrome: { webview: { postMessage: (message: unknown) => hostMessages.push(message) } } } });

function inspectFunctions<T>(language: string, surface: string, copy: T, english: T, cases: FunctionCase<T>[]) {
  for (const fixture of cases) {
    const path = `${surface}.${fixture.path}`;
    const actual = fixture.render(copy);
    issues.push(...inspectText(language, path, actual, fixture.render(english), fixture.requiresTranslation));
    issues.push(...inspectParameters(language, path, actual, fixture.parameters));
  }
}

try {
  for (const language of languages) {
    const before = issues.length;
    const [app, settings] = await Promise.all([loadAppCopy(language), loadSettingsText(language)]);
    assert.equal(settings.language, language, `${language}: settings loader silently returned another language.`);
    await applyLanguage(language);
    assert.equal(currentLanguage(), language);
    const unclassified = { kind: "unclassified" } as import("../src/types.ts").SoftwareIssueTag;
    assert.equal(softwareIssueLabel(unclassified), app.softwareKind.Unclassified);
    assert.equal(softwareIssueMessage(unclassified), app.management.unclassifiedReminder);
    assert.equal(runtimeDiscoveryLabel("runtime_package"), app.management.discoveredPackage);
    assert.equal(runtimeDiscoveryLabel("runtime_product"), app.management.discoveredProduct);
    assert.equal(runtimeDiscoveryLabel("runtime_root"), app.management.discoveredRoot);
    assert.equal(softwareCategorySourceLabel("Windows GameConfigStore\\Children\\MatchedExeFullPath"), app.management.windowsGameList);
    assert.equal(softwareCategorySourceLabel(null), "");
    for (const kind of ["adapted", "controlled", "game", "high_performance"] as const) {
      assert.equal(normalizeSoftwareKind({ id: "catalog:test", name: "Test", kind }), kind);
    }
    for (const kind of ["adapted", "controlled", "managed", "game", "high_performance", "other",
      "windows_system", "windows_component", "windows_service", "runtime_package", "runtime_product",
      "runtime_root", "unattributed", "system_residual"] as const) {
      assert.ok(app.softwareKind[kind], `${language}: missing software kind ${kind}`);
      assert.equal(softwareKindLabel(kind), app.softwareKind[kind], `${language}: kind ${kind}`);
      assert.equal(softwareDisplayKindLabel("other", kind), app.softwareKind[kind], `${language}: display kind ${kind}`);
    }

    assert.equal(currentSettingsText().language, language);
    for (const [wireState, copyState] of componentProviderStateCases) {
      assert.equal(userFacingState(wireState), uiText.status.state[copyState], `${language}: state ${wireState}`);
    }
    assert.equal(userFacingState("InstalledUnverified"), uiText.status.unknownState,
      `${language}: state conversion must match exact wire values`);
    assert.deepEqual(flattenCopy(uiText), flattenCopy(app), `${language}: active UI differs from the loaded bundle.`);
    assert.deepEqual(flattenCopy(currentSettingsText()), flattenCopy(settings));
    assert.equal(documentState.lang, language);
    assert.equal(documentState.dir, /^(?:ar|he)-/.test(language) ? "rtl" : "ltr");
    assert.deepEqual(hostMessages.at(-1), { type: "shell.language", language });
    issues.push(...inspectCopy(language, "app", uiText, englishApp));
    issues.push(...inspectCopy(language, "settings", currentSettingsText(), englishSettings));
    const edidFixture = JSON.parse(readFileSync(new URL("./fixtures/displayEdid.json", import.meta.url), "utf8")).snapshot.ports[0].display;
    const edid = buildDisplayEdidView(edidFixture, app); const englishEdid = buildDisplayEdidView(edidFixture, englishApp);
    for (let index = 0; index < edid.current.length; index++)
      issues.push(...inspectText(language, "consumer.edid.current." + index, edid.current[index].label, englishEdid.current[index].label, true));
    const unknownEdid = buildDisplayEdidView({ ...edidFixture, modeCapabilityDeclared: null, edidRawDeclaredBase64: null }, app);
    issues.push(...inspectText(language, "consumer.edid.unavailable", unknownEdid.raw, englishApp.deviceTopology.edid.unavailable, true));
    if (edid.modes[1].rows.find(row => row.label === app.deviceTopology.edid.frameRate)?.value !== "1978/33 Hz")
      throw new Error(language + ": exact EDID rate changed during localization");
    inspectFunctions(language, "app", uiText, englishApp, appCases);
    // 调度事实排除的码表是封闭的：每个事实、每个原因（含原生计划状态码）都经展示函数取到本语言的文字，没有按码原样显示的回退。
    for (const fact of hostManagerSchedulingFacts) {
      issues.push(...inspectText(language, `consumer.smartFailureFactText(${fact})`, smartFailureFactText(fact), englishApp.smartFailure.facts[fact], true));
    }
    for (const reason of hostManagerSchedulingExclusionReasons) {
      issues.push(...inspectText(language, `consumer.smartFailureReasonText(${reason})`, smartFailureReasonText(reason), englishApp.smartFailure.reasons[reason], true));
    }
    issues.push(...inspectText(language, "app.smartFailure.field.adapterKey", uiText.smartFailure.field.adapterKey, englishApp.smartFailure.field.adapterKey, true));
    inspectFunctions(language, "settings", currentSettingsText(), englishSettings, settingsCases);
    // 动画选项的名字和说明是两三个词的短句，英文散文判定抓不到，按必须翻译逐项检查（曾有四种语言漏翻）。
    for (const option of currentSettingsText().animationStyleOptions) {
      const english = englishSettings.animationStyleOptions.find(item => item.id === option.id)!;
      issues.push(...inspectText(language, `settings.animationStyleOptions.${option.id}.label`, option.label, english.label, true));
      issues.push(...inspectText(language, `settings.animationStyleOptions.${option.id}.description`, option.description, english.description, true));
    }
    // 内存与 CPU 取舍：分区名、标记、端点说明、取值和选项标题多是两三个词，同样按必须翻译逐项检查。
    issues.push(...inspectText(language, "settings.sections.memoryCpuTier", currentSettingsText().sections.memoryCpuTier, englishSettings.sections.memoryCpuTier, true));
    const tierText = uiText.memoryCpuTier; const englishTier = englishApp.memoryCpuTier;
    for (const key of ["tierTitle", "tierLowEnd", "tierHighEnd", "measuredTitle", "notMeasured", "retry", "unknownOption", "unset",
      "valueLabel", "choiceLabel", "clear"] as const) {
      issues.push(...inspectText(language, `app.memoryCpuTier.${key}`, tierText[key], englishTier[key], true));
    }
    for (const group of ["categories", "applyModes", "sources", "choices"] as const) {
      const english = englishTier[group] as Record<string, string>;
      for (const [key, value] of Object.entries(tierText[group] as Record<string, string>)) {
        issues.push(...inspectText(language, `app.memoryCpuTier.${group}.${key}`, value, english[key], true));
      }
    }
    for (const [key, value] of Object.entries(tierText.options)) {
      const english = englishTier.options[key as keyof typeof englishTier.options];
      issues.push(...inspectText(language, `app.memoryCpuTier.options.${key}.title`, value.title, english.title, true));
      issues.push(...inspectText(language, `app.memoryCpuTier.options.${key}.description`, value.description, english.description, true));
    }
    assert.match(uiText.backendMessage.software.selfNoUninstall, /UninstallResourceManager\.exe/, `${language}: self-uninstall must direct users to the external executable.`);

    if (language === "zh-TW") {
      const samples = [
        ["app.page.settings", uiText.page.settings],
        ["app.page.monitor", uiText.page.monitor],
        ["app.control.intro", uiText.control.intro],
        ["settings.loadState.loading", currentSettingsText().loadState.loading],
        ["settings.saveState.partial", currentSettingsText().saveState.partial]
      ] as const;
      for (const [path, value] of samples) issues.push(...inspectTraditional(path, value));
    }
    // These are built-in product labels/reasons, not arbitrary vendor names.
    // The actual presentation helpers must translate them independently of
    // whether a locale file happens to be present.
    const displays = [
      ["consumer.resourceMetricBasis(disk)", resourceMetricBasis("disk.read"), "实际完成的物理磁盘读写；缓存命中和网络文件读写不计入。未识别属主的传输留在未归属部分，写缓存回写可能归到系统进程。"],
      ["consumer.userFacingMessage(disk-window)", userFacingMessage("ETW DiskIO 采样窗口不完整。", uiText.status.state.warming), "ETW DiskIO 采样窗口不完整。"],
      ["consumer.resourceMetricBasis(memory-bar)", resourceMetricBasis("memory.usage"), "整机：物理内存实际用量 / 物理容量。进程分项：私有驻留字节；共享页和系统用量留在未归属部分。读数不可用时不显示进程分项。"],
      ["consumer.resourceMetricBasis(memory-table)", resourceMetricBasis("memory", "table"), "整机：物理内存实际用量 / 物理容量。进程：私有工作集，只算该进程独占的驻留页，不含共享页；共享页和系统用量留在未归属部分，所以各进程之和小于整机用量。"],
      ["consumer.userFacingMessage(uninstaller)", userFacingMessage("已向用户桌面请求启动 Windows 注册表提供的卸载器；需要提升时由 Windows 提示。完成后请刷新软件清单。", "fallback"), "已向用户桌面请求启动卸载器；需要提升时由 Windows 提示。完成后请刷新软件清单。"],
      ["consumer.userFacingMessage(uninstaller-session)", userFacingMessage("The requesting user session is no longer available.", "fallback"), "用户会话已退出或改变，未启动卸载器。"],
      ["consumer.systemProcessResultSummary", systemProcessResultSummary({ action: "Terminate", items: [{ processId: 1, processName: "owned", state: "Succeeded", outcome: "Terminated" }, { processId: 2, processName: "owned", state: "Skipped", outcome: "ProcessExited" }] }).message, "已结束 1 / 2 个进程。"],
      ["consumer.userFacingErrorMessage(process-elevation)", userFacingErrorMessage(new ApiRequestError("raw backend error", { kind: "http", status: 403, payload: { code: "ProcessActionRequiresElevation" }, retryable: false }), "fallback"), "此进程操作需要管理员授权。"],
      ["consumer.controlCapabilityLabel(fan.curve)", controlCapabilityLabel({ id: "fan.curve" }), "转速曲线"],
      ["consumer.controlReasonText(reason)", controlReasonText({ code: "fan-rpm-read-only" }), "转速为监测值。"],
      ["consumer.controlObjectName(fan)", controlObjectName({ kind: "fan", reportedName: null, ordinal: 1 }), "风扇 2"],
      ["consumer.controlReasonText(missing-runtime)", controlReasonText({ code: "control-helper-runtime-missing" }), "组件缺少 .NET 10 运行时，请安装运行时或使用自带运行时的新版组件。"],
      ["consumer.controlReasonText(helper-startup)", controlReasonText({ code: "control-helper-startup-failed" }), "组件运行时未能启动，请更新或修复该组件。"]
    ] as const;
    assert.equal(resourceMetricBasis("memory.usage"), app.resourceBreakdown.memoryBasis, `${language}: bar uses the wrong memory basis.`);
    for (const id of ["disk.io", "disk.read", "disk.write", "disk"]) {
      assert.equal(resourceMetricBasis(id, id === "disk" ? "table" : "bar"), app.resourceBreakdown.diskBasis, `${language}: ${id} basis was not projected.`);
    }
    assert.ok(userFacingMessage("ETW DiskIO 采样窗口不完整。", uiText.status.state.warming).startsWith(uiText.status.state.warming));
    assert.equal(resourceMetricBasis("memory", "table"), app.resourceBreakdown.memoryTableBasis, `${language}: table uses the wrong memory basis.`);
    assert.notEqual(resourceMetricBasis("memory.usage"), resourceMetricBasis("memory", "table"));
    for (const reason of [
      "fan-restore-protocol-required", "uniwill-firmware-unconfirmed", "uniwill-baseline-unavailable",
      "uniwill-target-unconfirmed", "integrated-gpu.IntelTargetMissing", "integrated-gpu.IntelDriverMissing",
      "control-readback-mismatch", "integrated-gpu.WaiverRejected", "integrated-gpu.DriverRejected",
      "integrated-gpu.OverclockNotAccepted", "nvidia.M2", "nvidia.M26", "fan-firmware-prerequisite"
    ]) {
      const actual = controlReasonText({ code: reason });
      const chinese = controlReasonText({ code: reason }, (await loadAppCopy("zh-CN")).control);
      if (language !== "zh-CN" && actual === chinese) issues.push({ language, path: `consumer.IntelGpu(${reason})`, kind: "consumer", actual });
      if (language === "zh-TW") issues.push(...inspectTraditional(`consumer.IntelGpu(${reason})`, actual));
    }
    for (const [path, actual, chinese] of displays) {
      if (language !== "zh-CN" && actual === chinese) {
        issues.push({ language, path, kind: "consumer", actual });
      }
      if (path.endsWith("(fan)")) issues.push(...inspectParameters(language, path, actual, ["2"]));
    }
    languageResults.push({ language, appLeaves: flattenCopy(app).size, settingsLeaves: flattenCopy(settings).size, functionCases: appCases.length + settingsCases.length, issues: issues.length - before });
  }
} finally {
  if (oldDocument) Object.defineProperty(globalThis, "document", oldDocument);
  else Reflect.deleteProperty(globalThis, "document");
  if (oldWindow) Object.defineProperty(globalThis, "window", oldWindow);
  else Reflect.deleteProperty(globalThis, "window");
}

const reportPath = process.env.RM_LOCALIZATION_REPORT_PATH
  ?? fileURLToPath(new URL("../../../../.local/test-results/localization-coverage.json", import.meta.url));
mkdirSync(dirname(reportPath), { recursive: true });
writeFileSync(reportPath, JSON.stringify({
  scope: "Selectable frontend languages: production loaders, active stores, host/document language, representative function outputs and control display helpers. Does not certify linguistic quality, rendered UI or other EXEs.",
  functionCoverage: { appFunctions: [...englishAppLeaves.values()].filter(value => typeof value === "function").length, appCases: appCases.map(value => value.path), settingsFunctions: [...englishSettingsLeaves.values()].filter(value => typeof value === "function").length, settingsCases: settingsCases.map(value => value.path) },
  languageResults,
  issues
}, null, 2));
for (const result of languageResults) console.log(`${result.issues ? "FAIL" : "PASS"} ${result.language}: ${result.issues} coverage findings`);
console.log(`Full language/path/text report: ${reportPath}`);
if (issues.length > 0) {
  // Print one example per defect class/language; keep every finding in JSON.
  const printed = new Set<string>();
  for (const issue of issues) {
    const group = `${issue.language}:${issue.kind}`;
    if (printed.has(group)) continue;
    printed.add(group);
    console.error(`${group} ${issue.path}: ${JSON.stringify(issue.actual)}`);
  }
  process.exitCode = 1;
} else {
  console.log("Frontend localization coverage passed within the recorded scope.");
}
