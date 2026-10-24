import assert from "node:assert/strict";
import { softwareIssueLabel, softwareIssueMessage, softwareIssueReferenceLabel } from "../src/presentation/softwareClassification.ts";
import { applyLanguage, currentLanguage, uiText } from "../src/text.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { presentOptimizationReport } from "../src/features/optimization/optimizationPresentation.ts";
import type { SoftwareIssueTag, OptimizationReportItem } from "../src/types.ts";

for (const language of languageOptions) {
  if (language.id === "system") continue;
  applyLanguage(language.id);
  await Promise.all([loadAppCopy(language.id), loadSettingsText(language.id)]);
  await Promise.resolve();
  assert.equal(currentLanguage(), language.id);
  const issue: SoftwareIssueTag = { id: "memory", kind: "AbnormalMemoryUsage", severity: "Warning", source: "DynamicReport",
    label: "BACKEND_PROSE", message: "BACKEND_PROSE", dynamic: true, references: [], reportIds: ["report"],
    evidence: [{ metricKind: "Memory", value: 4294967296, valueUnit: "B" }] };
  const label = softwareIssueLabel(issue);
  const message = softwareIssueMessage(issue);
  assert.equal(label, uiText.management.issueText.AbnormalMemoryUsage);
  assert(message.includes("4") && message.includes(uiText.management.issueText.memoryAdvice), `${language.id}: memory evidence/advice`);
  assert(!message.includes("BACKEND_PROSE"));
  const driver = { ...issue, kind: "LongSystemInterrupts", evidence: [{ metricKind: "InterruptMaximumSingleDurationMilliseconds", value: 12, valueUnit: "ms", moduleName: "driver.sys" }] };
  assert(softwareIssueMessage(driver).includes("driver.sys: 12 ms"));
  assert(softwareIssueMessage(driver).includes(uiText.management.issueText.interruptAdvice));
  const catalog = { ...issue, id: "app-notebook-fan-control-winring0x64-known-vulnerabilities", kind: "KnownSecurityVulnerability" };
  assert(softwareIssueMessage(catalog).includes("WinRing0x64.sys 1.2.0.5"));
  assert.equal(softwareIssueReferenceLabel({ label: "后端引用", url: "https://learn.microsoft.com/microsoft-recommended-driver-block-rules" }), uiText.management.issueText.driverReference);
  assert.equal(softwareIssueReferenceLabel({ label: "CVE-2020-14979", url: "https://nvd.nist.gov/vuln/detail/CVE-2020-14979" }), "CVE-2020-14979");
  const report = { type: "SystemInterruptPressure", title: "", message: "", target: { targetType: "System", displayName: "", processNames: [] },
    context: null, evidence: { resourceKind: "SystemInterrupt", currentValue: 12, averageValue: 11, peakValue: 12, valueUnit: "ms", durationSeconds: 0, activeSampleCount: 1 },
    severity: "Warning" } as unknown as OptimizationReportItem;
  const presentation = presentOptimizationReport(report);
  assert(presentation.title.length > 0 && presentation.summary.includes("12 ms"));
  assert(presentation.summary.includes(uiText.management.issueText.interruptAdvice));
  if (!["zh-CN", "zh-TW"].includes(language.id)) assert(!(label + message + softwareIssueMessage(catalog)).includes("内存占用异常"), language.id);
  console.log(`PASS software issue and interrupt report presentation ${language.id}`);
}
