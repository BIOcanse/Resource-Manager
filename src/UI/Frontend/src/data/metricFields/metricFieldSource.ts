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
  /** 显卡地址按后端 MetricDeviceAddress.GpuAdapter：一个物理成员是 gpu_adapter，多个是 gpu_adapter_group，都带 LUID。 */
  | { readonly kind: "gpu_adapter"; readonly adapterLuid: string; readonly pnpInstanceId: string }
  | { readonly kind: "gpu_adapter_group"; readonly adapterLuid: string; readonly pnpInstanceIds: readonly string[] }
  /** 显卡引擎地址按后端 MetricDeviceAddress.GpuEngine：复制所属显卡的成员，再加 LUID 和引擎坐标；卡片名用 LUID 找显卡名。 */
  | ({ readonly kind: "gpu_engine"; readonly adapterLuid: string;
      readonly physicalIndex: number; readonly engineIndex: number; readonly engineClass: number } & MetricFieldGpuMembers);

type MetricFieldGpuMembers =
  | { readonly pnpInstanceId: string; readonly pnpInstanceIds?: never }
  | { readonly pnpInstanceIds: readonly string[]; readonly pnpInstanceId?: never };

/*
 * 字段属于哪个读取项（同一台设备的同一物理量），以及它在这个读取项里是哪个来源。
 * 合同见 docs/metric_reading_source_selection.md。这里按 wire 原样保留，不推断：
 * id 只做相等比较（同一读取项的所有来源和跨设备的显示别名共用一个 id）；sourceRank 越小越优先；
 * 本机首选由后端核定（isPreferredSource 与 preferred 引用），没有首选时两个引用为 null。
 * 显示怎么用这些事实见 presentation/readingSources.ts。
 */
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
  /** 没有读取项的字段为 null（含后端还没发这个对象时）。 */
  readonly reading: MetricFieldReading | null;
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
  /** 和同一字段的值上的 reading 相同；没有读取项为 null。 */
  readonly reading: MetricFieldReading | null;
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

/** GET /api/metrics/fields/catalog 的原样数组；订阅推送把同一形状放在信封的 fields 里（metricCatalogPush.ts）。 */
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
      // 后端以后给别的设备种类加地址时，这里还没有消费者：当作没有地址。
      return null;
  }
}


// 后端只发其中一种：单个物理成员写 pnpInstanceId，多个写 pnpInstanceIds。
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

/** 读取项可空；还没发这个对象的后端（字段上没有 reading）同样当作没有读取项。 */
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
