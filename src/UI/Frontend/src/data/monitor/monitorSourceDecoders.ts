import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireFiniteNumber,
  requireNonEmptyString,
  requireRecord,
  requireSafeInteger,
  requireString,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type {
  BackendMessage,
  DashboardSettingsResult,
  MetricDefinition,
  MetricSnapshot,
  MetricValue
} from "../../types.ts";
import { presentMetricDefinition, presentMetricValue } from "../../presentation/metricPresentation.ts";
import type { RawMetricIdentity } from "./metricWire.ts";

export const metricCatalogDecoder = defineResponseDecoder<MetricDefinition[]>(
  "monitor.metric-catalog.v2",
  (value) => requireArray(value, "$")
    .map((item, index) => decodeMetricDefinition(item, `$[${index}]`)));

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

export const dashboardSettingsDecoder =
  defineResponseDecoder<DashboardSettingsResult>(
    "monitor.dashboard-settings.v1",
    (value) => value as DashboardSettingsResult);

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
    scopeKey: optionalNullableString(record.scopeKey, `${path}.scopeKey`)
  });
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
