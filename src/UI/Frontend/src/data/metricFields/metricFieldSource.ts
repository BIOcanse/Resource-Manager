import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireFiniteNumber,
  requireNonEmptyString,
  requireNonNegativeSafeInteger,
  requireNullable,
  requireOneOf,
  requireRecord,
  requireString,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";

/* Match devices by structured address, never by parsing identity strings. Non-current values are null, and 64-bit integer text becomes bigint without precision loss. */

export type MetricFieldAddress =
  | { readonly kind: "cpu_logical_processor"; readonly processorGroup: number; readonly groupRelativeIndex: number }
  | { readonly kind: "numa_node"; readonly nodeNumber: number }
  | { readonly kind: "npu"; readonly pnpInstanceId: string }
  /** GPU addresses identify one adapter or an adapter group by LUID. */
  | { readonly kind: "gpu_adapter"; readonly adapterLuid: string; readonly pnpInstanceId: string }
  | { readonly kind: "gpu_adapter_group"; readonly adapterLuid: string; readonly pnpInstanceIds: readonly string[] }
  /** Engine addresses retain adapter members plus LUID and engine coordinates; LUID selects the card label. */
  | ({ readonly kind: "gpu_engine"; readonly adapterLuid: string;
      readonly physicalIndex: number; readonly engineIndex: number; readonly engineClass: number } & MetricFieldGpuMembers);

type MetricFieldGpuMembers =
  | { readonly pnpInstanceId: string; readonly pnpInstanceIds?: never }
  | { readonly pnpInstanceIds: readonly string[]; readonly pnpInstanceId?: never };

/* Preserve backend reading identity and source preference. IDs support equality only; lower sourceRank wins. Preferred references are null when no source is preferred. */
export const metricFieldSourceAvailabilities = ["available", "unsupported", "unverified"] as const;
export type MetricFieldSourceAvailability = typeof metricFieldSourceAvailabilities[number];

export interface MetricFieldReading {
  readonly id: string;
  readonly quantity: string;
  readonly sourceRank: number;
  readonly sourceAvailability: MetricFieldSourceAvailability;
  readonly isPreferredSource: boolean;
  readonly preferredDeviceIdentity: string | null;
  readonly preferredFieldName: string | null;
}

export interface MetricFieldValue {
  readonly name: string;
  readonly unit: string;
  readonly valueKind: string;
  /** Null when no reading is declared, including an omitted reading object. */
  readonly reading: MetricFieldReading | null;
  /** Current float value, or null when unavailable or of another type. */
  readonly numericValue: number | null;
  /** Current integer value, or null when unavailable or of another type. */
  readonly integerValue: bigint | null;
  /** Current flag value, or null when unavailable or of another type. */
  readonly booleanValue: boolean | null;
  readonly observedAt: string | null;
}

/** The same device descriptor appears in both catalogs and value frames. */
export interface MetricFieldDeviceInfo {
  readonly deviceIdentity: string;
  /** Backend device kind, such as cpu_logical_processor, gpu_adapter, storage or fan. */
  readonly kind: string;
  /** Null for absent or unsupported addresses; such devices cannot match existing objects. */
  readonly address: MetricFieldAddress | null;
  /** Device-reported name, or null for a label derived from kind and index. */
  readonly displayName: string | null;
  /** Stable display order within a device kind. */
  readonly ordinal: number;
}

export interface MetricFieldDevice extends MetricFieldDeviceInfo {
  readonly fields: readonly MetricFieldValue[];
}

/** UI-consumed field metadata; labels and descriptions are localized by field name. */
export interface MetricFieldDefinition {
  readonly name: string;
  readonly unit: string;
  readonly valueKind: string;
  /** Same reading as the field value; null when absent. */
  readonly reading: MetricFieldReading | null;
}

export interface MetricFieldCatalogDevice extends MetricFieldDeviceInfo {
  readonly fields: readonly MetricFieldDefinition[];
}

export interface MetricFieldQuery {
  readonly fields: readonly string[];
}

/** Backend SamplingObservationStatus.Current. */
const currentStatus = 1;

export function normalizeMetricFieldQuery(query: MetricFieldQuery): MetricFieldQuery {
  return { fields: Object.freeze([...new Set(query.fields.map((field) => field.trim()).filter(Boolean))].sort()) };
}

export function buildMetricFieldSubscriptionUrl(query: MetricFieldQuery, intervalMs: number): string {
  const params = new URLSearchParams();
  params.set("fields", normalizeMetricFieldQuery(query).fields.join(","));
  params.set("intervalMs", String(Math.max(1, Math.round(intervalMs))));
  return `/api/metrics/subscribe?${params.toString()}`;
}

export const metricFieldDevicesDecoder = defineResponseDecoder<readonly MetricFieldDevice[]>(
  "metric-field-devices",
  (value) => requireArray(value, "$").map((entry, index) => decodeDevice(entry, `$[${index}]`)));

function decodeDevice(value: unknown, path: string): MetricFieldDevice {
  const record = requireRecord(value, path);
  return {
    ...decodeDeviceInfo(record, path),
    fields: requireArray(record.fields, `${path}.fields`)
      .map((field, index) => decodeValue(field, `${path}.fields[${index}]`))
  };
}

export function buildMetricFieldCatalogSubscriptionUrl(intervalMs: number): string {
  const params = new URLSearchParams({ intervalMs: String(Math.max(1, Math.round(intervalMs))) });
  return `/api/metrics/fields/catalog/subscribe?${params.toString()}`;
}

/** Raw field-catalog array; subscription envelopes carry this shape in fields. */
export const metricFieldCatalogDecoder = defineResponseDecoder<readonly MetricFieldCatalogDevice[]>(
  "metric-field-catalog",
  (value) => decodeMetricFieldCatalog(value, "$"));

export function decodeMetricFieldCatalog(value: unknown, path: string): readonly MetricFieldCatalogDevice[] {
  return requireArray(value, path).map((entry, index) => {
    const devicePath = `${path}[${index}]`;
    const record = requireRecord(entry, devicePath);
    return {
      ...decodeDeviceInfo(record, devicePath),
      fields: requireArray(record.fields, `${devicePath}.fields`).map((field, fieldIndex) => {
        const fieldPath = `${devicePath}.fields[${fieldIndex}]`;
        const definition = requireRecord(field, fieldPath);
        return {
          name: requireString(definition.name, `${fieldPath}.name`),
          unit: requireString(definition.unit, `${fieldPath}.unit`),
          valueKind: requireString(definition.valueKind, `${fieldPath}.valueKind`),
          reading: decodeReading(definition.reading, `${fieldPath}.reading`)
        };
      })
    };
  });
}

function decodeDeviceInfo(record: Record<string, unknown>, path: string): MetricFieldDeviceInfo {
  const device = requireRecord(record.device, `${path}.device`);
  return {
    deviceIdentity: requireString(record.deviceIdentity, `${path}.deviceIdentity`),
    kind: requireString(device.kind, `${path}.device.kind`),
    address: device.address === null || device.address === undefined
      ? null
      : decodeAddress(device.address, `${path}.device.address`),
    displayName: typeof device.displayName === "string" && device.displayName.trim()
      ? device.displayName.trim()
      : null,
    ordinal: requireNonNegativeSafeInteger(device.ordinal, `${path}.device.ordinal`)
  };
}

function decodeAddress(value: unknown, path: string): MetricFieldAddress | null {
  const record = requireRecord(value, path);
  const kind = requireString(record.kind, `${path}.kind`);
  switch (kind) {
    case "cpu_logical_processor":
      return {
        kind,
        processorGroup: requireNonNegativeSafeInteger(record.processorGroup, `${path}.processorGroup`),
        groupRelativeIndex: requireNonNegativeSafeInteger(record.groupRelativeIndex, `${path}.groupRelativeIndex`)
      };
    case "numa_node":
      return { kind, nodeNumber: requireNonNegativeSafeInteger(record.nodeNumber, `${path}.nodeNumber`) };
    case "npu":
      return { kind, pnpInstanceId: requireString(record.pnpInstanceId, path + ".pnpInstanceId") };
    case "gpu_adapter":
      return { kind, adapterLuid: requireString(record.adapterLuid, `${path}.adapterLuid`),
        pnpInstanceId: requireString(record.pnpInstanceId, `${path}.pnpInstanceId`) };
    case "gpu_adapter_group":
      return { kind, adapterLuid: requireString(record.adapterLuid, `${path}.adapterLuid`),
        pnpInstanceIds: decodeGpuMemberList(record.pnpInstanceIds, `${path}.pnpInstanceIds`) };
    case "gpu_engine":
      return { kind, adapterLuid: requireString(record.adapterLuid, `${path}.adapterLuid`),
        physicalIndex: requireNonNegativeSafeInteger(record.physicalIndex, `${path}.physicalIndex`),
        engineIndex: requireNonNegativeSafeInteger(record.engineIndex, `${path}.engineIndex`),
        engineClass: requireNonNegativeSafeInteger(record.engineClass, `${path}.engineClass`),
        ...decodeEngineMembers(record, path) };
    default:
      // Unsupported device kinds have no address consumer yet, so retain no address.
      return null;
  }
}


// The backend sends pnpInstanceId for one member or pnpInstanceIds for multiple members.
function decodeEngineMembers(record: Record<string, unknown>, path: string): MetricFieldGpuMembers {
  if ((record.pnpInstanceId == null) === (record.pnpInstanceIds == null)) {
    throw new ResponseDecodeError(path, "exactly one of pnpInstanceId or pnpInstanceIds");
  }
  return record.pnpInstanceId != null
    ? { pnpInstanceId: requireString(record.pnpInstanceId, `${path}.pnpInstanceId`) }
    : { pnpInstanceIds: decodeGpuMemberList(record.pnpInstanceIds, `${path}.pnpInstanceIds`) };
}

function decodeGpuMemberList(value: unknown, path: string): string[] {
  const members = requireArray(value, path).map((member, index) => requireString(member, `${path}[${index}]`));
  if (members.length < 2) throw new ResponseDecodeError(path, "two or more physical members");
  return members;
}
function decodeValue(value: unknown, path: string): MetricFieldValue {
  const record = requireRecord(value, path);
  const current = record.status === currentStatus;
  return {
    name: requireString(record.name, `${path}.name`),
    unit: requireString(record.unit, `${path}.unit`),
    valueKind: requireString(record.valueKind, `${path}.valueKind`),
    reading: decodeReading(record.reading, `${path}.reading`),
    numericValue: current ? optionalFiniteNumber(record.numericValue, `${path}.numericValue`) : null,
    integerValue: current ? optionalInteger(record.integerValue, `${path}.integerValue`) : null,
    booleanValue: current && typeof record.booleanValue === "boolean" ? record.booleanValue : null,
    observedAt: current && typeof record.observedAt === "string" ? record.observedAt : null
  };
}

/** Missing and null reading objects both mean no declared reading. */
export function decodeReading(value: unknown, path: string): MetricFieldReading | null {
  if (value === undefined || value === null) return null;
  const record = requireRecord(value, path);
  return {
    id: requireNonEmptyString(record.id, `${path}.id`),
    quantity: requireString(record.quantity, `${path}.quantity`),
    sourceRank: requireFiniteNumber(record.sourceRank, `${path}.sourceRank`),
    sourceAvailability: requireOneOf(record.sourceAvailability, `${path}.sourceAvailability`, metricFieldSourceAvailabilities),
    isPreferredSource: requireBoolean(record.isPreferredSource, `${path}.isPreferredSource`),
    preferredDeviceIdentity: requireNullable(record.preferredDeviceIdentity, `${path}.preferredDeviceIdentity`, requireString),
    preferredFieldName: requireNullable(record.preferredFieldName, `${path}.preferredFieldName`, requireString)
  };
}

function optionalFiniteNumber(value: unknown, path: string): number | null {
  if (value === null || value === undefined) return null;
  if (typeof value !== "number" || !Number.isFinite(value)) throw new ResponseDecodeError(path, "finite number or null");
  return value;
}

function optionalInteger(value: unknown, path: string): bigint | null {
  if (value === null || value === undefined) return null;
  if (typeof value !== "string" || !/^-?\d+$/.test(value)) throw new ResponseDecodeError(path, "decimal integer string or null");
  return BigInt(value);
}

/** Find a device field by address; return null if either is absent. */
export function findMetricField(
  devices: readonly MetricFieldDevice[] | null | undefined,
  matches: (address: MetricFieldAddress) => boolean,
  fieldName: string
): MetricFieldValue | null {
  for (const device of devices ?? []) {
    if (device.address !== null && matches(device.address)) {
      return device.fields.find((field) => field.name === fieldName) ?? null;
    }
  }
  return null;
}
