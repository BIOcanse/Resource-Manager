import {
  defineResponseDecoder,
  requireArray,
  requireNonNegativeSafeInteger,
  requireRecord,
  requireString,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";

/*
 * 规范字段的设备目录与设备值。
 *
 * 目录列出每台设备绑定了哪些字段；按一组字段名订阅时，后端推回所有绑定了这些字段的设备。
 * 每台设备带一个描述：种类、结构化地址、设备自报的名字和同种类内的显示顺序。
 * 消费方用地址直接对到自己已有的对象（逻辑处理器、NUMA 节点、拓扑里的 NPU／显卡），
 * 不解析设备身份字符串。后端状态不是 current 的值，这里一律记为不可用（null），
 * 不当成 0；unsigned64／signed64 的十进制字符串转成 bigint，保持精确。
 */

export type MetricFieldAddress =
  | { readonly kind: "cpu_logical_processor"; readonly processorGroup: number; readonly groupRelativeIndex: number }
  | { readonly kind: "numa_node"; readonly nodeNumber: number }
  | { readonly kind: "npu"; readonly pnpInstanceId: string }
  | { readonly kind: "gpu_adapter"; readonly pnpInstanceId: string };

export interface MetricFieldValue {
  readonly name: string;
  readonly unit: string;
  readonly valueKind: string;
  /** float64 字段的当前值；不可用或不是浮点字段时为 null。 */
  readonly numericValue: number | null;
  /** 整数字段的当前值；不可用或不是整数字段时为 null。 */
  readonly integerValue: bigint | null;
  /** 布尔（flag）字段的当前值；不可用或不是布尔字段时为 null。 */
  readonly booleanValue: boolean | null;
  readonly observedAt: string | null;
}

/** 一台设备是谁。目录和设备值里同一台设备的描述相同。 */
export interface MetricFieldDeviceInfo {
  readonly deviceIdentity: string;
  /** 设备种类，例如 cpu_logical_processor、gpu_adapter、storage、fan。 */
  readonly kind: string;
  /** 前端还没有消费者的种类，或者后端没给地址时为 null；这样的设备对不上任何已有对象。 */
  readonly address: MetricFieldAddress | null;
  /** 设备自报的名字（磁盘型号、网卡名等）；没有时为 null，由前端按种类和序号起名。 */
  readonly displayName: string | null;
  /** 同种类内稳定的显示顺序。 */
  readonly ordinal: number;
}

export interface MetricFieldDevice extends MetricFieldDeviceInfo {
  readonly fields: readonly MetricFieldValue[];
}

/** 字段定义里前端用到的部分。显示名和说明由前端按字段名维护，后端不给。 */
export interface MetricFieldDefinition {
  readonly name: string;
  readonly unit: string;
  readonly valueKind: string;
}

export interface MetricFieldCatalogDevice extends MetricFieldDeviceInfo {
  readonly fields: readonly MetricFieldDefinition[];
}

export interface MetricFieldQuery {
  readonly fields: readonly string[];
}

/** 后端 SamplingObservationStatus.Current。 */
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

export const metricFieldCatalogDecoder = defineResponseDecoder<readonly MetricFieldCatalogDevice[]>(
  "metric-field-catalog",
  (value) => requireArray(value, "$").map((entry, index) => {
    const path = `$[${index}]`;
    const record = requireRecord(entry, path);
    return {
      ...decodeDeviceInfo(record, path),
      fields: requireArray(record.fields, `${path}.fields`).map((field, fieldIndex) => {
        const fieldPath = `${path}.fields[${fieldIndex}]`;
        const definition = requireRecord(field, fieldPath);
        return {
          name: requireString(definition.name, `${fieldPath}.name`),
          unit: requireString(definition.unit, `${fieldPath}.unit`),
          valueKind: requireString(definition.valueKind, `${fieldPath}.valueKind`)
        };
      })
    };
  }));

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
    case "gpu_adapter":
      return { kind, pnpInstanceId: requireString(record.pnpInstanceId, `${path}.pnpInstanceId`) };
    default:
      // 后端以后给别的设备种类加地址时，这里还没有消费者：当作没有地址。
      return null;
  }
}

function decodeValue(value: unknown, path: string): MetricFieldValue {
  const record = requireRecord(value, path);
  const current = record.status === currentStatus;
  return {
    name: requireString(record.name, `${path}.name`),
    unit: requireString(record.unit, `${path}.unit`),
    valueKind: requireString(record.valueKind, `${path}.valueKind`),
    numericValue: current ? optionalFiniteNumber(record.numericValue, `${path}.numericValue`) : null,
    integerValue: current ? optionalInteger(record.integerValue, `${path}.integerValue`) : null,
    booleanValue: current && typeof record.booleanValue === "boolean" ? record.booleanValue : null,
    observedAt: current && typeof record.observedAt === "string" ? record.observedAt : null
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

/** 按地址找设备上的某个字段。找不到设备或字段都返回 null。 */
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
