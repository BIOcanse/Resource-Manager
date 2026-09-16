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

export const metricCatalogDecoder = defineResponseDecoder<MetricDefinition[]>(
  "monitor.metric-catalog.v1",
  (value) => requireArray(value, "$")
    .map((item, index) => decodeMetricDefinition(item, `$[${index}]`)));

export const metricSnapshotDecoder = defineResponseDecoder<MetricSnapshot>(
  "monitor.metric-snapshot.v4",
  (value) => {
    const record = requireRecord(value);
    const version = requireSafeInteger(record.version, "$.version");
    if (version !== 4) {
      throw new ResponseDecodeError("$.version", "4");
    }
    const itemsRecord = requireRecord(record.items, "$.items");
    const items = Object.fromEntries(Object.entries(itemsRecord).map(([key, item]) => [
        key,
        decodeMetricValue(item, `$.items.${key}`)
      ]));
    const capturedAt = requireNullableTimestamp(record.capturedAt, "$.capturedAt");
    return {
      version: 4,
      capturedAt,
      items
    };
  });

export const dashboardSettingsDecoder =
  defineResponseDecoder<DashboardSettingsResult>(
    "monitor.dashboard-settings.v1",
    (value) => value as DashboardSettingsResult);

function decodeMetricDefinition(value: unknown, path: string): MetricDefinition {
  const record = requireRecord(value, path);
  return {
    id: requireNonEmptyString(record.id, `${path}.id`),
    label: requireNonEmptyString(record.label, `${path}.label`),
    group: requireNonEmptyString(record.group, `${path}.group`),
    unit: requireString(record.unit, `${path}.unit`),
    preferredSlot: requireNonEmptyString(record.preferredSlot, `${path}.preferredSlot`),
    detail: optionalNullableString(record.detail, `${path}.detail`),
    requiredComponentId: optionalNullableString(
      record.requiredComponentId,
      `${path}.requiredComponentId`),
    requiredComponentName: optionalNullableString(
      record.requiredComponentName,
      `${path}.requiredComponentName`),
    selectable: optionalBoolean(record.selectable, `${path}.selectable`),
    disabledReason: optionalBackendMessage(record.disabledReason, `${path}.disabledReason`),
    scopeKind: optionalNullableString(record.scopeKind, `${path}.scopeKind`),
    scopeKey: optionalNullableString(record.scopeKey, `${path}.scopeKey`)
  };
}

function decodeMetricValue(value: unknown, path: string): MetricValue {
  const record = requireRecord(value, path);
  return {
    id: optionalString(record.id, `${path}.id`),
    label: optionalString(record.label, `${path}.label`),
    group: optionalString(record.group, `${path}.group`),
    displayValue: requireString(record.displayValue, `${path}.displayValue`),
    numericValue: optionalNullableNumber(record.numericValue, `${path}.numericValue`),
    unit: optionalString(record.unit, `${path}.unit`),
    total: optionalNullableNumber(record.total, `${path}.total`),
    percent: optionalNullableNumber(record.percent, `${path}.percent`),
    detail: optionalNullableString(record.detail, `${path}.detail`)
  };
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
