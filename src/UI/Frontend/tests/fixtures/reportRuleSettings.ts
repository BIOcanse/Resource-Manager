import type { ReportRuleSetting } from "../../src/types.ts";
import { emptyReportRuleValues } from "../../src/features/settings/reportRuleSettingsModel.ts";

export const reportRules: ReportRuleSetting[] = [
  { name: "cpu_throttle_temperature", reportType: "CpuSustainedThermalThrottling", comparison: "atOrAbove",
    displayUnit: "celsius", scale: 1, step: 1, thresholdMinimum: 40, thresholdMaximum: 110,
    countMaximum: 1000, zeroActivityMaximum: null,
    editableFields: ["activationThreshold", "clearThreshold", "requiredConsecutiveHits", "requiredConsecutiveMisses"],
    defaults: { ...emptyReportRuleValues, activationThreshold: 90, clearThreshold: 85,
      requiredConsecutiveHits: 6, requiredConsecutiveMisses: 3 } },
  { name: "cpu_throttle_frequency", reportType: "CpuSustainedThermalThrottling", comparison: "atOrBelow",
    displayUnit: "percent", scale: 1, step: 1, thresholdMinimum: 1, thresholdMaximum: 200,
    countMaximum: 1000, zeroActivityMaximum: null,
    editableFields: ["activationThreshold", "clearThreshold"],
    defaults: { ...emptyReportRuleValues, activationThreshold: 85, clearThreshold: 90 } },
  { name: "background_memory_bytes", reportType: "BackgroundHighUsage", comparison: "atOrAbove",
    displayUnit: "gibibytes", scale: 1073741824, step: 0.25, thresholdMinimum: 268435456, thresholdMaximum: 1099511627776,
    countMaximum: 1000, zeroActivityMaximum: null, editableFields: ["activationThreshold", "clearThreshold"],
    defaults: { ...emptyReportRuleValues, activationThreshold: 4294967296, clearThreshold: 3221225472 } },
  { name: "stray_process_memory", reportType: "suspected_stray_process", comparison: "atOrAbove",
    displayUnit: "mebibytes", scale: 1048576, step: 1, thresholdMinimum: 1048576, thresholdMaximum: 68719476736,
    countMaximum: 1000, zeroActivityMaximum: null, editableFields: ["activationThreshold", "clearThreshold"],
    defaults: { ...emptyReportRuleValues, activationThreshold: 52428800, clearThreshold: 47185920 } },
  { name: "stray_process_idle", reportType: "suspected_stray_process", comparison: "atOrAbove",
    displayUnit: "minutes", scale: 60000, step: 1, thresholdMinimum: 60000, thresholdMaximum: 86400000,
    countMaximum: 1000, zeroActivityMaximum: 10,
    editableFields: ["activationThreshold", "zeroActivityThreshold", "executableNames"],
    defaults: { ...emptyReportRuleValues, activationThreshold: 900000, zeroActivityThreshold: 0,
      executableNames: ["cmd", "powershell", "node"] } }
];
