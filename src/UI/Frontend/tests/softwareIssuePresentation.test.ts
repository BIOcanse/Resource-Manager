import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { softwareIssueLabel, softwareIssueMessage, softwareIssueReferenceLabel } from "../src/presentation/softwareClassification.ts";
import { applyLanguage, currentLanguage, uiText } from "../src/text.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { presentOptimizationReport } from "../src/features/optimization/optimizationPresentation.ts";
import { optimizationResourceLabel, optimizationSeverityLabel } from "../src/features/optimization/optimizationPresentation.ts";
import { browserRuntimeKindLabel, browserRuntimeSourceLabel } from "../src/features/browserRuntimes/browserRuntimePresentation.ts";
import { cpuMaximumOccupancyOptions, runtimeSwitchMethodOptions } from "../src/components/softwarePlacementOptions.ts";
import { startupTargetGpuOptionsForSoftwarePolicy } from "../src/components/gpuPlacementTargetOptions.ts";
import type { SoftwareIssueTag, OptimizationReportItem } from "../src/types.ts";

const wireLabelBaseline = JSON.parse(readFileSync(
  new URL("./fixtures/softwareIssueLabels.json", import.meta.url), "utf8")) as Record<string, Record<string, string>>;
const issueKinds = [
  "abnormal_memory_usage",
  "long_system_interrupts",
  "excessive_system_interrupts",
  "known_security_vulnerability",
  "dependency_issue",
  "compatibility_issue",
  "known_malware"
] as const;
const optimizationResources = [
  ["cpu", "cpu"], ["gpu", "gpu"], ["memory", "memory"], ["vram", "vram"], ["disk", "disk"],
  ["disk_write", "diskWrite"], ["network", "network"], ["network_activity", "networkActivity"],
  ["software_footprint", "softwareFootprint"], ["power_profile", "powerProfile"], ["device_driver", "deviceDriver"],
  ["disk_health", "diskHealth"], ["disk_link", "diskLink"], ["disk_latency", "diskLatency"], ["disk_power", "diskPower"],
  ["cpu_thermal", "cpuThermal"], ["system_interrupt", "systemInterrupt"]
] as const;
const browserRuntimeKinds = [
  ["web_view2_runtime", "webView2Runtime"], ["chromium_browser", "chromiumBrowser"], ["gecko_browser", "geckoBrowser"]
] as const;
const browserRuntimeSources = [
  ["system_machine", "systemMachine"], ["system_user", "systemUser"], ["managed", "managed"],
  ["browser_registration", "browserRegistration"], ["known_install", "knownInstall"]
] as const;

for (const language of languageOptions) {
  if (language.id === "system") continue;
  applyLanguage(language.id);
  await Promise.all([loadAppCopy(language.id), loadSettingsText(language.id)]);
  await Promise.resolve();
  assert.equal(currentLanguage(), language.id);
  const expectedIssueLabels = wireLabelBaseline[language.id];
  const expectedRoleLabels = (wireLabelBaseline.managementRole as unknown as Record<string, Record<string, string>>)[language.id];
  for (const kind of issueKinds) {
    assert.equal(uiText.management.issueText[kind], expectedIssueLabels[kind], `${language.id}: label changed for ${kind}`);
  }
  assert.equal(uiText.managementRole.dependency, expectedRoleLabels.Dependency, `${language.id}: dependency label changed`);
  assert.equal(uiText.managementRole.support, expectedRoleLabels.Support, `${language.id}: support label changed`);
  assert.equal(optimizationSeverityLabel("info"), uiText.optimizationReport.severityInfo, `${language.id}: info severity label`);
  assert.equal(optimizationSeverityLabel("warning"), uiText.optimizationReport.severityWarning, `${language.id}: warning severity label`);
  assert.equal(optimizationSeverityLabel("critical"), uiText.optimizationReport.severityCritical, `${language.id}: critical severity label`);
  for (const [kind, copyKey] of optimizationResources) {
    assert.equal(optimizationResourceLabel(kind), uiText.optimizationReport.resource[copyKey], `${language.id}: resource ${kind}`);
  }
  for (const [kind, copyKey] of browserRuntimeKinds) {
    assert.equal(browserRuntimeKindLabel(kind), uiText.browserRuntime.kind[copyKey], `${language.id}: runtime kind ${kind}`);
  }
  for (const [source, copyKey] of browserRuntimeSources) {
    assert.equal(browserRuntimeSourceLabel(source), uiText.browserRuntime.source[copyKey], `${language.id}: runtime source ${source}`);
  }
  assert.deepEqual(
    startupTargetGpuOptionsForSoftwarePolicy(false, "system_default_gpu"),
    [
      ["system_default_gpu", uiText.gpuTarget.systemDefault],
      ["high_performance_gpu", uiText.gpuTarget.highPerformance],
      ["integrated_gpu", uiText.gpuTarget.integrated]
    ], `${language.id}: GPU target labels`);
  assert.deepEqual(
    startupTargetGpuOptionsForSoftwarePolicy(false, "gpu7")[0],
    ["gpu7", uiText.gpuTarget.needsPreciseSelection("GPU7"), true], `${language.id}: exact GPU target label`);
  assert.deepEqual(runtimeSwitchMethodOptions(), [
    ["future_frame_takeover", uiText.softwareDetail.option.futureFrameTakeover],
    ["window_rerender", uiText.softwareDetail.option.windowRerender]
  ], `${language.id}: runtime switch labels`);
  assert.deepEqual(cpuMaximumOccupancyOptions(), [
    ["single_ccd", uiText.softwareDetail.option.singleCcd],
    ["all_cores", uiText.softwareDetail.option.allCores]
  ], `${language.id}: CPU occupancy labels`);
  const issue: SoftwareIssueTag = { id: "memory", kind: "abnormal_memory_usage", severity: "warning", source: "dynamic_report",
    label: "BACKEND_PROSE", message: "BACKEND_PROSE", dynamic: true, references: [], reportIds: ["report"],
    evidence: [{ metricKind: "memory", value: 4294967296, valueUnit: "B" }] };
  const label = softwareIssueLabel(issue);
  const message = softwareIssueMessage(issue);
  assert.equal(label, uiText.management.issueText.abnormal_memory_usage);
  assert(message.includes("4") && message.includes(uiText.management.issueText.memoryAdvice), `${language.id}: memory evidence/advice`);
  assert(!message.includes("BACKEND_PROSE"));
  const driver = { ...issue, kind: "long_system_interrupts", evidence: [{ metricKind: "interrupt_maximum_single_duration_milliseconds", value: 12, valueUnit: "ms", moduleName: "driver.sys" }] };
  assert(softwareIssueMessage(driver).includes("driver.sys: 12 ms"));
  assert(softwareIssueMessage(driver).includes(uiText.management.issueText.interruptAdvice));
  const catalog = { ...issue, id: "app-notebook-fan-control-winring0x64-known-vulnerabilities", kind: "known_security_vulnerability", source: "static_catalog" };
  assert(softwareIssueMessage(catalog).includes("WinRing0x64.sys 1.2.0.5"));
  assert.equal(softwareIssueReferenceLabel({ label: "后端引用", url: "https://learn.microsoft.com/microsoft-recommended-driver-block-rules" }), uiText.management.issueText.driverReference);
  assert.equal(softwareIssueReferenceLabel({ label: "CVE-2020-14979", url: "https://nvd.nist.gov/vuln/detail/CVE-2020-14979" }), "CVE-2020-14979");
  const report = { type: "system_interrupt_pressure", title: "", message: "", target: { targetType: "system", name: "", processNames: [] },
    context: null, evidence: { resourceKind: "system_interrupt", currentValue: 12, averageValue: 11, peakValue: 12, valueUnit: "ms", durationSeconds: 0, activeSampleCount: 1 },
    severity: "warning" } as unknown as OptimizationReportItem;
  const presentation = presentOptimizationReport(report);
  assert(presentation.title.length > 0 && presentation.summary.includes("12 ms"));
  assert(presentation.summary.includes(uiText.management.issueText.interruptAdvice));
  const contextual = presentOptimizationReport({
    ...report,
    type: "background_high_usage",
    severity: "critical",
    target: { ...report.target, targetType: "software", name: "Game App" },
    context: { contextKind: "game", evidence: [], foregroundSoftwareName: "Game App" },
    evidence: { ...report.evidence, resourceKind: "cpu" }
  } as OptimizationReportItem);
  assert(contextual.title.length > 0);
  assert(JSON.stringify(contextual.details).includes("Game App"), `${language.id}: game context display`);
  const stray = { ...report, type: "suspected_stray_process", severity: "info", confidence: "high",
    target: { ...report.target, targetType: "process_instance", name: "pwsh", processIds: [42] },
    evidence: { ...report.evidence, resourceKind: "memory", currentValue: 1, valueUnit: "B",
      process: { processId: 42, processStartKey: "134000000000000000", name: "pwsh", memoryBytes: 52428800,
        idleDurationSeconds: 900, commandLine: 'pwsh -File "任务.ps1"' } }
  } as OptimizationReportItem;
  const strayPresentation = presentOptimizationReport(stray);
  assert.equal(strayPresentation.title, uiText.optimizationReport.strayProcess.title("pwsh"));
  assert(strayPresentation.summary.includes("50") && strayPresentation.summary.includes("15"));
  const rendered = JSON.stringify(strayPresentation.details);
  for (const value of ["42", "134000000000000000", "任务.ps1", uiText.optimizationReport.strayProcess.commandLine])
    assert(rendered.includes(value), `${language.id}: missing stray evidence ${value}`);
  assert(!strayPresentation.summary.includes("BACKEND_PROSE"));
  const unavailable = presentOptimizationReport({ ...stray, evidence: { ...stray.evidence, process: null } });
  assert(!unavailable.summary.includes("undefined") && !unavailable.summary.includes("null"));
  if (!["zh-CN", "zh-TW"].includes(language.id)) assert(!(label + message + softwareIssueMessage(catalog)).includes("内存占用异常"), language.id);
  console.log(`PASS software issue and interrupt report presentation ${language.id}`);
}
