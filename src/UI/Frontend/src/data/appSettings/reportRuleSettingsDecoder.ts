import type { AppReportSettings, ReportRuleValues, ReportRuleSettingsResponse } from "../../types.ts";
import { defineResponseDecoder, requireArray, requireFiniteNumber, requireNonEmptyString,
  requireNullable, requireOneOf, requireRecord, requireSafeInteger, requireStringArray,
  ResponseDecodeError } from "../../frontendRuntime/request/ResponseDecoder.ts";

export const reportRuleFields = ["activationThreshold", "clearThreshold", "requiredConsecutiveHits",
  "requiredConsecutiveMisses", "zeroActivityThreshold", "executableNames"] as const;

function decodeValues(value: unknown, path: string): ReportRuleValues {
  const record = requireRecord(value, path);
  return {
    activationThreshold: requireNullable(record.activationThreshold, `${path}.activationThreshold`, requireFiniteNumber),
    clearThreshold: requireNullable(record.clearThreshold, `${path}.clearThreshold`, requireFiniteNumber),
    requiredConsecutiveHits: requireNullable(record.requiredConsecutiveHits, `${path}.requiredConsecutiveHits`, requireSafeInteger),
    requiredConsecutiveMisses: requireNullable(record.requiredConsecutiveMisses, `${path}.requiredConsecutiveMisses`, requireSafeInteger),
    zeroActivityThreshold: requireNullable(record.zeroActivityThreshold, `${path}.zeroActivityThreshold`, requireFiniteNumber),
    executableNames: requireNullable(record.executableNames, `${path}.executableNames`, requireStringArray)
  };
}

export function decodeReportSettings(value: unknown, path: string): AppReportSettings {
  const record = requireRecord(value, path);
  return { ruleOverrides: requireArray(record.ruleOverrides, `${path}.ruleOverrides`).map((item, index) => {
    const itemPath = `${path}.ruleOverrides[${index}]`;
    const entry = requireRecord(item, itemPath);
    return { rule: requireNonEmptyString(entry.rule, `${itemPath}.rule`), ...decodeValues(entry, itemPath) };
  }) };
}

function positive(value: unknown, path: string): number {
  const number = requireFiniteNumber(value, path);
  if (number <= 0) throw new ResponseDecodeError(path, "positive number");
  return number;
}

export const reportRuleSettingsDecoder = defineResponseDecoder<ReportRuleSettingsResponse>(
  "app.settings.report-rules.v1", value => {
    const record = requireRecord(value);
    const names = new Set<string>();
    return { rules: requireArray(record.rules, "$.rules").map((item, index) => {
      const path = `$.rules[${index}]`;
      const rule = requireRecord(item, path);
      const name = requireNonEmptyString(rule.name, `${path}.name`);
      if (names.has(name)) throw new ResponseDecodeError(`${path}.name`, "unique rule name");
      names.add(name);
      const defaults = decodeValues(rule.defaults, `${path}.defaults`);
      const editableFields = requireArray(rule.editableFields, `${path}.editableFields`).map((field, i) =>
        requireOneOf(field, `${path}.editableFields[${i}]`, reportRuleFields));
      if (new Set(editableFields).size !== editableFields.length) {
        throw new ResponseDecodeError(`${path}.editableFields`, "unique fields");
      }
      for (const field of editableFields) {
        if (defaults[field] === null) throw new ResponseDecodeError(`${path}.defaults.${field}`, "editable default");
      }
      const thresholdMinimum = requireFiniteNumber(rule.thresholdMinimum, `${path}.thresholdMinimum`);
      const thresholdMaximum = requireFiniteNumber(rule.thresholdMaximum, `${path}.thresholdMaximum`);
      if (thresholdMaximum < thresholdMinimum) throw new ResponseDecodeError(`${path}.thresholdMaximum`, "maximum >= minimum");
      const countMaximum = requireSafeInteger(rule.countMaximum, `${path}.countMaximum`);
      if (countMaximum < 1) throw new ResponseDecodeError(`${path}.countMaximum`, "positive integer");
      const zeroActivityMaximum = requireNullable(rule.zeroActivityMaximum, `${path}.zeroActivityMaximum`, requireFiniteNumber);
      if ((zeroActivityMaximum !== null && zeroActivityMaximum < 0)
        || (editableFields.includes("zeroActivityThreshold") && zeroActivityMaximum === null)) {
        throw new ResponseDecodeError(`${path}.zeroActivityMaximum`, "non-negative maximum for editable activity threshold");
      }
      return { name, reportType: requireNonEmptyString(rule.reportType, `${path}.reportType`),
        comparison: requireOneOf(rule.comparison, `${path}.comparison`, ["atOrAbove", "atOrBelow"]),
        displayUnit: requireOneOf(rule.displayUnit, `${path}.displayUnit`,
          ["celsius", "percent", "milliseconds", "minutes", "mebibytes", "gibibytes"]),
        scale: positive(rule.scale, `${path}.scale`), step: positive(rule.step, `${path}.step`),
        thresholdMinimum, thresholdMaximum, countMaximum, zeroActivityMaximum, editableFields, defaults };
    }) };
  });
