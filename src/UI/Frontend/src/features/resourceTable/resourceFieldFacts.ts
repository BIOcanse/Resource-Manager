import { requireArray, requireBoolean, requireFiniteNumber, requireNonEmptyString, requireNullable, requireRecord,
  requireSafeInteger, requireString, ResponseDecodeError } from "../../frontendRuntime/request/ResponseDecoder.ts";
import type { ResourceTableValue } from "../../types.ts";
import { resourceTableCellText } from "../../presentation/metricLabels.ts";
import { requireTimestamp } from "../../api/resourceMetricFactsWire.ts";

export interface ResourceFieldFacts {
  field: string;
  unit: string;
  sourceId: string;
  capturedAt: string | null;
  additiveAcrossProcesses: boolean | null;
  processes: { identity: { processId: number; processStartKey?: string | null }; value: ResourceTableValue }[];
  software: { softwareId: string; value: ResourceTableValue }[];
}

export function decodeResourceFieldFacts(value: unknown, processes: readonly { processId: number; processStartKey?: string | null }[], softwareIds: ReadonlySet<string>): ResourceFieldFacts[] {
  // Recorded v2 frames from before field readings remain readable.
  if (value === undefined) return [];
  return requireArray(value, "$.fields").map((item, index) => {
    const path = `$.fields[${index}]`, field = requireRecord(item, path);
    const name = requireNonEmptyString(field.field, `${path}.field`);
    const unit = requireString(field.unit, `${path}.unit`);
    function cell(item: unknown, path: string): ResourceTableValue {
      const record = requireRecord(item, path), reading = requireRecord(record.value, `${path}.value`);
      if (requireString(reading.field, `${path}.value.field`) !== name) throw new ResponseDecodeError(`${path}.value.field`, name);
      const integerValue = requireNullable(reading.integerValue, `${path}.value.integerValue`, (value, path) => {
        const integer = requireString(value, path);
        if (!/^-?\d+$/.test(integer)) throw new ResponseDecodeError(path, "exact decimal integer");
        return integer;
      });
      const numeric = requireNullable(reading.numericValue, `${path}.value.numericValue`, requireFiniteNumber);
      const text = requireNullable(reading.stringValue, `${path}.value.stringValue`, requireString);
      if ([integerValue, numeric, text].filter(value => value !== null).length > 1) throw new ResponseDecodeError(path, "one typed value");
      return { unit, integerValue, value: numeric ?? (integerValue !== null ? Number(integerValue) : null),
        displayValue: text, availability: numeric === null && integerValue === null && text === null ? "Unavailable" : null,
        limit: record.limit === undefined ? null : requireNullable(record.limit, `${path}.limit`, requireFiniteNumber) };
    }
    return { field: name, unit, sourceId: requireNonEmptyString(field.sourceId, `${path}.sourceId`),
      capturedAt: requireNullable(field.capturedAt, `${path}.capturedAt`, requireTimestamp),
      additiveAcrossProcesses: requireNullable(field.additiveAcrossProcesses, `${path}.additiveAcrossProcesses`, requireBoolean),
      processes: requireArray(field.processes, `${path}.processes`).map((item, index) => {
        const valuePath = `${path}.processes[${index}]`, record = requireRecord(item, valuePath);
        const processIndex = requireSafeInteger(record.processIndex, `${valuePath}.processIndex`);
        if (processIndex < 0 || processIndex >= processes.length) throw new ResponseDecodeError(valuePath, "known process identity");
        const reading = cell(record, valuePath);
        const scopes = record.scopes === undefined ? [] : requireArray(record.scopes, `${valuePath}.scopes`).map((item, index) => {
          const scopePath = `${valuePath}.scopes[${index}]`, scope = requireRecord(item, scopePath);
          return `${requireNonEmptyString(scope.scope, `${scopePath}.scope`)}: ${resourceTableCellText(cell(scope, scopePath))}`;
        });
        return { identity: processes[processIndex], value: scopes.length > 0 ? { ...reading, displayValue: scopes.join(" · "), availability: null } : reading };
      }), software: requireArray(field.software, `${path}.software`).map((item, index) => {
        const valuePath = `${path}.software[${index}]`, record = requireRecord(item, valuePath);
        const softwareId = requireNonEmptyString(record.softwareId, `${valuePath}.softwareId`);
        if (!softwareIds.has(softwareId)) throw new ResponseDecodeError(valuePath, "known software identity");
        return { softwareId, value: cell(record, valuePath) };
      }) };
  });
}
