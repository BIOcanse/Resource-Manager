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
import { controlCapabilityLabel, controlDisplayText } from "../src/features/control/controlPresentation.ts";
import { systemProcessResultSummary } from "../src/presentation/systemProcessResult.ts";
import { userFacingErrorMessage, userFacingMessage } from "../src/presentation/userFacingText.ts";
import { ApiRequestError } from "../src/api/httpTransport.ts";
import { resourceMetricBasis } from "../src/presentation/metricLabels.ts";
import { flattenCopy, inspectCopy, inspectParameters, inspectText, inspectTraditional } from "./localizationCoverage.ts";
import type { LocalizationIssue } from "./localizationCoverage.ts";

interface FunctionCase<T> {
  path: string;
  render: (copy: T) => string;
  parameters: readonly string[];
  requiresTranslation: boolean;
}
const appCases: FunctionCase<AppCopy>[] = [
  { path: "optimizationReport.strayProcess.title", render: copy => copy.optimizationReport.strayProcess.title("Tool-A"), parameters: ["Tool-A"], requiresTranslation: true },
  { path: "optimizationReport.strayProcess.summary", render: copy => copy.optimizationReport.strayProcess.summary("Tool-A", "51 MiB", "17 min"), parameters: ["Tool-A", "51 MiB", "17 min"], requiresTranslation: true },
  { path: "softwareActions.processActionResult", render: copy => copy.softwareActions.processActionResult(7, 19), parameters: ["7", "19"], requiresTranslation: true },
  { path: "control.needsComponent", render: copy => copy.control.needsComponent("NVAPI"), parameters: ["NVAPI"], requiresTranslation: true },
  { path: "control.instances.firstSeen", render: copy => copy.control.instances.firstSeen("2030-01-02"), parameters: ["2030-01-02"], requiresTranslation: true },
  { path: "control.accessLevel.confirmTitle", render: copy => copy.control.accessLevel.confirmTitle("root"), parameters: ["root"], requiresTranslation: true },
  { path: "control.takeoverCoversOthers", render: copy => copy.control.takeoverCoversOthers("Fan-A"), parameters: ["Fan-A"], requiresTranslation: true },
  { path: "control.presentation.fanName", render: copy => copy.control.presentation.fanName("92"), parameters: ["92"], requiresTranslation: true },
  { path: "control.presentation.gpuFanControllerName", render: copy => copy.control.presentation.gpuFanControllerName("14"), parameters: ["14"], requiresTranslation: true },
  { path: "diskUsage.scanFailed", render: copy => copy.diskUsage.scanFailed, parameters: [], requiresTranslation: true },
  { path: "diskUsage.freeOfTotal", render: copy => copy.diskUsage.freeOfTotal("31 GiB", "512 GiB"), parameters: ["31 GiB", "512 GiB"], requiresTranslation: true },
  { path: "diskUsage.scanTotals", render: copy => copy.diskUsage.scanTotals("29 GiB", 13, 7), parameters: ["29 GiB", "13", "7"], requiresTranslation: true },
  { path: "shell.documentTitle", render: copy => copy.shell.documentTitle("Test-page", "Resource Manager"), parameters: ["Test-page", "Resource Manager"], requiresTranslation: false }
];
for (const count of [0, 1, 37]) {
  appCases.push(
    { path: `diskUsage.fileCount(${count})`, render: copy => copy.diskUsage.fileCount(count), parameters: [String(count)], requiresTranslation: true },
    { path: `diskUsage.omitted(${count})`, render: copy => copy.diskUsage.omitted(count), parameters: [String(count)], requiresTranslation: true },
    { path: `shell.taskCenterActive(${count})`, render: copy => copy.shell.taskCenterActive(count), parameters: [String(count)], requiresTranslation: true }
  );
}
const settingsCases: FunctionCase<SettingsTextBundle>[] = [
  { path: "updates.confirmPrepare", render: copy => copy.updates.confirmPrepare("4.7.9-beta.8"), parameters: ["4.7.9-beta.8"], requiresTranslation: true }
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
    applyLanguage(language);
    // Bundles are already cached; one microtask settles the production store.
    await Promise.resolve();
    assert.equal(currentLanguage(), language);
    const unclassified = { kind: "Unclassified" } as import("../src/types.ts").SoftwareIssueTag;
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
    assert.deepEqual(flattenCopy(uiText), flattenCopy(app), `${language}: active UI differs from the loaded bundle.`);
    assert.deepEqual(flattenCopy(currentSettingsText()), flattenCopy(settings));
    assert.equal(documentState.lang, language);
    assert.equal(documentState.dir, /^(?:ar|he)-/.test(language) ? "rtl" : "ltr");
    assert.deepEqual(hostMessages.at(-1), { type: "shell.language", language });
    issues.push(...inspectCopy(language, "app", uiText, englishApp));
    issues.push(...inspectCopy(language, "settings", currentSettingsText(), englishSettings, true));
    const edidFixture = JSON.parse(readFileSync(new URL("./fixtures/displayEdid.json", import.meta.url), "utf8")).snapshot.ports[0].display;
    const edid = buildDisplayEdidView(edidFixture, app); const englishEdid = buildDisplayEdidView(edidFixture, englishApp);
    for (let index = 0; index < edid.current.length; index++)
      issues.push(...inspectText(language, "consumer.edid.current." + index, edid.current[index].label, englishEdid.current[index].label, true));
    const unknownEdid = buildDisplayEdidView({ ...edidFixture, modeCapabilityDeclared: null, edidRawDeclaredBase64: null }, app);
    issues.push(...inspectText(language, "consumer.edid.unavailable", unknownEdid.raw, englishApp.deviceTopology.edid.unavailable, true));
    if (edid.modes[1].rows.find(row => row.label === app.deviceTopology.edid.frameRate)?.value !== "1978/33 Hz")
      throw new Error(language + ": exact EDID rate changed during localization");
    inspectFunctions(language, "app", uiText, englishApp, appCases);
    inspectFunctions(language, "settings", currentSettingsText(), englishSettings, settingsCases);
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
      ["consumer.resourceMetricBasis(memory-bar)", resourceMetricBasis("memory.usage"), "整机：物理内存实际用量 / 物理容量。进程分项：私有驻留字节；共享页和系统用量留在未归属部分。读取不可用时不编造进程分项。"],
      ["consumer.resourceMetricBasis(memory-table)", resourceMetricBasis("memory", "table"), "整机：物理内存实际用量 / 物理容量。进程：驻留工作集，含共享页；跨进程求和可能重复计算共享页。"],
      ["consumer.userFacingMessage(uninstaller)", userFacingMessage("已向用户桌面请求启动 Windows 注册表提供的卸载器；需要提升时由 Windows 提示。完成后请刷新软件清单。", "fallback"), "已向用户桌面请求启动卸载器；需要提升时由 Windows 提示。完成后请刷新软件清单。"],
      ["consumer.userFacingMessage(uninstaller-session)", userFacingMessage("The requesting user session is no longer available.", "fallback"), "用户会话已退出或改变，未启动卸载器。"],
      ["consumer.systemProcessResultSummary", systemProcessResultSummary({ message: "已结束 1 / 2 个进程。", items: [{ processId: 1, processName: "owned", state: "Succeeded", message: "" }, { processId: 2, processName: "owned", state: "Skipped", message: "" }] }).message, "已完成 1 / 2 个进程操作。"],
      ["consumer.userFacingErrorMessage(process-elevation)", userFacingErrorMessage(new ApiRequestError("raw backend error", { kind: "http", status: 403, payload: { code: "ProcessActionRequiresElevation" }, retryable: false }), "fallback"), "此进程操作需要管理员授权。"],
      ["consumer.controlDisplayText(fan-detection-0)", controlDisplayText("风扇控制接口缺少 SetSoftware 方法。"), "风扇控制接口缺少 SetSoftware 方法。"],
      ["consumer.controlDisplayText(fan-detection-1)", controlDisplayText("风扇控制接口缺少 SetDefault 方法。"), "风扇控制接口缺少 SetDefault 方法。"],
      ["consumer.controlDisplayText(fan-detection-2)", controlDisplayText("仅自定义电源模式下生效（联想笔记本电脑）。固件的风扇表按电源模式分开存，EC 只在自定义模式下执行它；切过去之后重新检测硬件即可解锁。"), "仅自定义电源模式下生效（联想笔记本电脑）。固件的风扇表按电源模式分开存，EC 只在自定义模式下执行它；切过去之后重新检测硬件即可解锁。"],
      ["consumer.controlCapabilityLabel(fan.curve)", controlCapabilityLabel({ id: "fan.curve", label: "转速曲线" }), "转速曲线"],
      ["consumer.controlDisplayText(reason)", controlDisplayText("转速为监测值。"), "转速为监测值。"],
      ["consumer.controlDisplayText(fan)", controlDisplayText("风扇 2"), "风扇 2"],
      ["consumer.controlDisplayText(missing-runtime)", controlDisplayText("组件缺少 .NET 10 运行时，请安装运行时或使用自带运行时的新版组件。"), "组件缺少 .NET 10 运行时，请安装运行时或使用自带运行时的新版组件。"],
      ["consumer.controlDisplayText(helper-startup)", controlDisplayText("组件运行时未能启动，请更新或修复该组件。"), "组件运行时未能启动，请更新或修复该组件。"]
    ] as const;
    assert.equal(resourceMetricBasis("memory.usage"), app.resourceBreakdown.memoryBasis, `${language}: bar uses the wrong memory basis.`);
    for (const id of ["disk.io", "disk.read", "disk.write", "disk"]) {
      assert.equal(resourceMetricBasis(id, id === "disk" ? "table" : "bar"), app.resourceBreakdown.diskBasis, `${language}: ${id} basis was not projected.`);
    }
    assert.ok(userFacingMessage("ETW DiskIO 采样窗口不完整。", uiText.status.state.warming).startsWith(uiText.status.state.warming));
    assert.equal(resourceMetricBasis("memory", "table"), app.resourceBreakdown.memoryTableBasis, `${language}: table uses the wrong memory basis.`);
    assert.notEqual(resourceMetricBasis("memory.usage"), resourceMetricBasis("memory", "table"));
    for (const reason of [
      "风扇组件不支持原表恢复，请先更新组件。",
      "未确认此机型固件支持 GPU 功耗控制，不能调整。",
      "无法读取或保存功率预算恢复基线，未继续调整。",
      "无法读取或保存风扇曲线恢复基线，未继续调整。",
      "固件模式、表布局或回读不符，风扇曲线尚未恢复。",
      "固件表或曲线包含无效值，未继续调整。",
      "整机固件预算只用于唯一确认的内置 NVIDIA 独显；外接或身份不明的显卡不可调整。",
      "无法将所选核显与 Intel 驱动设备唯一对应，未读取或写入其他显卡。",
      "读不到 Intel 显卡驱动自带的控制库，装上/更新显卡驱动之后才能调。",
      "写下去了，但回读的值没有变。",
      "这块核显不接受超频设置。",
      "驱动拒绝了这次写入。",
      "需要先同意超频免责声明。"
    ]) {
      const actual = controlDisplayText(reason);
      if (language !== "zh-CN" && actual === reason) issues.push({ language, path: `consumer.IntelGpu(${reason})`, kind: "consumer", actual });
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

const reportPath = fileURLToPath(new URL("../../../../.local/test-results/localization-coverage.json", import.meta.url));
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
