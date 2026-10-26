import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireFiniteNumber,
  requireNonEmptyString,
  requireOneOf,
  requireRecord,
  requireSafeInteger,
  requireString,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type {
  BackendMessage,
  MetricDefinition,
  MetricSnapshot,
  MetricSourceCandidate,
  MetricValue
} from "../../types.ts";
import { presentMetricDefinition, presentMetricValue } from "../../presentation/metricPresentation.ts";
import type { RawMetricIdentity } from "./metricWire.ts";
import { decodeReading, metricFieldSourceAvailabilities } from "../metricFields/metricFieldSource.ts";

export const metricCatalogDecoder = defineResponseDecoder<MetricDefinition[]>(
  "monitor.metric-catalog.v2",
  (value) => decodeMetricCatalog(value, "$"));

/** GET /api/metrics/catalog 的数组；目录订阅信封的 metrics 是同一形状（metricCatalogPush.ts）。 */
export function decodeMetricCatalog(value: unknown, path: string): MetricDefinition[] {
  return requireArray(value, path)
    .map((item, index) => decodeMetricDefinition(item, `${path}[${index}]`));
}

export const metricSnapshotDecoder = defineResponseDecoder<MetricSnapshot>(
  "monitor.metric-snapshot.v5",
  (value) => {
    const record = requireRecord(value);
    const version = requireSafeInteger(record.version, "$.version");
    if (version !== 5) {
      throw new ResponseDecodeError("$.version", "5");
    }
    const itemsRecord = requireRecord(record.items, "$.items");
    const items = Object.fromEntries(Object.entries(itemsRecord).map(([key, item]) => [
        key,
        decodeMetricValue(item, `$.items.${key}`)
      ]));
    const capturedAt = requireNullableTimestamp(record.capturedAt, "$.capturedAt");
    return {
      version: 5,
      capturedAt,
      catalogRevision: requireNonEmptyString(record.catalogRevision, "$.catalogRevision"),
      items
    };
  });

function decodeMetricDefinition(value: unknown, path: string): MetricDefinition {
  const record = requireRecord(value, path);
  return presentMetricDefinition({
    id: requireNonEmptyString(record.id, `${path}.id`),
    unit: requireString(record.unit, `${path}.unit`),
    ...decodeIdentity(record, path),
    requiredComponentId: optionalNullableString(
      record.requiredComponentId,
      `${path}.requiredComponentId`),
    selectable: optionalBoolean(record.selectable, `${path}.selectable`),
    disabledReason: optionalBackendMessage(record.disabledReason, `${path}.disabledReason`),
    scopeKind: optionalNullableString(record.scopeKind, `${path}.scopeKind`),
    scopeKey: optionalNullableString(record.scopeKey, `${path}.scopeKey`),
    candidateSources: requireArray(record.candidateSources, `${path}.candidateSources`)
      .map((item, index) => decodeCandidateSource(item, `${path}.candidateSources[${index}]`))
  });
}

// 部件地址的候选来源，按后端排序原样保留（docs/metric_reading_source_selection.md「部件目录的候选来源」）。
function decodeCandidateSource(value: unknown, path: string): MetricSourceCandidate {
  const record = requireRecord(value, path);
  return {
    fieldName: requireNonEmptyString(record.fieldName, `${path}.fieldName`),
    deviceIdentity: requireNonEmptyString(record.deviceIdentity, `${path}.deviceIdentity`),
    sourceId: requireString(record.sourceId, `${path}.sourceId`),
    reading: decodeReading(record.reading, `${path}.reading`),
    sourceAvailability: requireOneOf(record.sourceAvailability, `${path}.sourceAvailability`, metricFieldSourceAvailabilities),
    isSelected: requireBoolean(record.isSelected, `${path}.isSelected`)
  };
}

function decodeMetricValue(value: unknown, path: string): MetricValue {
  const record = requireRecord(value, path);
  return presentMetricValue({
    id: requireNonEmptyString(record.id, `${path}.id`),
    unit: requireString(record.unit, `${path}.unit`),
    ...decodeIdentity(record, path),
    numericValue: record.numericValue === null ? null : requireFiniteNumber(record.numericValue, `${path}.numericValue`),
    integerValue: optionalInteger(record.integerValue, `${path}.integerValue`),
    bytesValue: optionalNullableString(record.bytesValue, `${path}.bytesValue`),
    booleanValue: record.booleanValue === null ? null : optionalBoolean(record.booleanValue, `${path}.booleanValue`),
    status: requireSafeInteger(record.status, `${path}.status`),
    observedAt: record.observedAt === undefined ? undefined : requireNullableTimestamp(record.observedAt, `${path}.observedAt`),
    sampleDurationMilliseconds: optionalNullableNumber(record.sampleDurationMilliseconds, `${path}.sampleDurationMilliseconds`),
    total: optionalNullableNumber(record.total, `${path}.total`),
    percent: optionalNullableNumber(record.percent, `${path}.percent`)
  });
}

function decodeIdentity(record: Record<string, unknown>, path: string): RawMetricIdentity {
  return {
    fieldCode: record.fieldCode === undefined ? undefined : requireSafeInteger(record.fieldCode, `${path}.fieldCode`),
    fieldName: optionalNullableString(record.fieldName, `${path}.fieldName`),
    deviceIdentity: optionalNullableString(record.deviceIdentity, `${path}.deviceIdentity`),
    sourceId: optionalNullableString(record.sourceId, `${path}.sourceId`),
    valueKind: optionalNullableString(record.valueKind, `${path}.valueKind`)
  };
}

function optionalInteger(value: unknown, path: string): string | null | undefined {
  if (value === null || value === undefined) return value;
  const text = requireString(value, path);
  if (!/^-?\d{1,39}$/.test(text)) throw new ResponseDecodeError(path, "signed64/unsigned128 decimal integer string");
  const integer = BigInt(text);
  if (integer < -(1n << 63n) || integer >= (1n << 128n)) {
    throw new ResponseDecodeError(path, "signed64/unsigned128 decimal integer string");
  }
  return text;
}

function optionalString(value: unknown, path: string): string | undefined {
  if (value === undefined) {
    return undefined;
  }
  return requireString(value, path);
}

function optionalNullableString(
  value: unknown,
  path: string
): string | null | undefined {
  return value === null ? null : optionalString(value, path);
}

// 后端的消息码：域/码是 byte，参数只放事实。认不出形状就当没有，界面回落到通用说明。
function optionalBackendMessage(
  value: unknown,
  path: string
): BackendMessage | null | undefined {
  if (value === null || value === undefined) {
    return value as null | undefined;
  }

  const record = requireRecord(value, path);
  return {
    domain: requireSafeInteger(record.domain, `${path}.domain`),
    code: requireSafeInteger(record.code, `${path}.code`),
    args: requireArray(record.args ?? [], `${path}.args`)
      .map((item, index) => requireString(item, `${path}.args[${index}]`))
  };
}

function optionalBoolean(value: unknown, path: string): boolean | undefined {
  return value === undefined ? undefined : requireBoolean(value, path);
}

function optionalNullableNumber(
  value: unknown,
  path: string
): number | null | undefined {
  return value === null || value === undefined
    ? value
    : requireFiniteNumber(value, path);
}

function requireNullableTimestamp(value: unknown, path: string): string | null {
  if (value === null) {
    return null;
  }
  const text = requireNonEmptyString(value, path);
  if (!Number.isFinite(Date.parse(text))) {
    throw new ResponseDecodeError(path, "valid timestamp string or null");
  }
  return text;
}
