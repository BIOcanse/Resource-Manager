import type { AppReportSettings, ReportRuleField, ReportRuleOverride, ReportRuleSetting, ReportRuleValues } from "../../types.ts";
import { reportRuleFields } from "../../data/appSettings/reportRuleSettingsDecoder.ts";

export const emptyReportRuleValues: ReportRuleValues = {
  activationThreshold: null, clearThreshold: null, requiredConsecutiveHits: null,
  requiredConsecutiveMisses: null, zeroActivityThreshold: null, executableNames: null
};

export function normalizeReportSettings(settings?: AppReportSettings): AppReportSettings {
  return { ruleOverrides: (settings?.ruleOverrides ?? []).map(entry => ({ ...emptyReportRuleValues, ...entry }))
    .filter(entry => reportRuleFields.some(field => entry[field] !== null)) };
}

export function updateReportRuleField<K extends ReportRuleField>(
  settings: AppReportSettings, rule: string, field: K, value: ReportRuleValues[K]
): AppReportSettings {
  const entries = settings.ruleOverrides;
  const existing = entries.find(entry => entry.rule === rule);
  const next: ReportRuleOverride = { ...emptyReportRuleValues, ...existing, rule, [field]: value };
  return normalizeReportSettings({ ruleOverrides: existing
    ? entries.map(entry => entry === existing ? next : entry) : [...entries, next] });
}

export function parseExecutableNames(text: string): string[] {
  return [...new Map(text.split(/\r?\n/).map(name => name.trim()).filter(Boolean)
    .map(name => [name.toLowerCase(), name])).values()];
}

export function reportRuleNumberField(rule: ReportRuleSetting, values: ReportRuleValues,
  field: Exclude<ReportRuleField, "executableNames">) {
  const count = field === "requiredConsecutiveHits" || field === "requiredConsecutiveMisses";
  const activity = field === "zeroActivityThreshold";
  const scale = count || activity ? 1 : rule.scale;
  let min = count ? 1 : activity ? 0 : rule.thresholdMinimum / scale;
  let max = count ? rule.countMaximum : activity ? rule.zeroActivityMaximum! : rule.thresholdMaximum / scale;
  if (field === "clearThreshold") {
    const activation = (values.activationThreshold ?? rule.defaults.activationThreshold)! / scale;
    if (rule.comparison === "atOrAbove") max = Math.min(max, activation);
    else min = Math.max(min, activation);
  }
  return { value: (values[field] ?? rule.defaults[field])! / scale,
    defaultValue: rule.defaults[field]! / scale, min, max, scale,
    step: count ? 1 : activity ? 0.1 : rule.step,
    unit: count ? "count" as const : activity ? "percent" as const : rule.displayUnit };
}

export function parseReportRuleRejection(code?: string | null) {
  const match = /^report-rule-invalid:([^:]+):([^:]+):(unknown-rule|duplicate-rule|not-editable|out-of-range|clear-on-wrong-side|invalid-executable-names)$/.exec(code ?? "");
  return match ? { rule: match[1], field: match[2], reason: match[3] as
    "unknown-rule" | "duplicate-rule" | "not-editable" | "out-of-range" | "clear-on-wrong-side" | "invalid-executable-names" } : null;
}
