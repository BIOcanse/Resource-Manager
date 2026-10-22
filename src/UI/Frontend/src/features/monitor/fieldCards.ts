import type {
  DashboardCardSettings,
  DashboardMetricBinding,
  MetricDefinition,
  MetricValue
} from "../../types.ts";
import type {
  MetricFieldCatalogDevice,
  MetricFieldDevice,
  MetricFieldDeviceInfo
} from "../../data/metricFields/metricFieldSource.ts";
import type { ByteQuantityKind } from "../../presentation/byteUnits.ts";
import { formatFieldValue } from "../../presentation/fieldValueFormat.ts";
import { readingSourceDisplays } from "../../presentation/readingSources.ts";
import { cardExcludedFields } from "./canonicalFields.ts";
import { uiText } from "../../text.ts";

/*
 * 监视控制台卡片上的规范字段。
 *
 * 选择器：字段目录里每台设备的每个可放卡片的字段是一项。id 为「字段名@设备身份」，
 * 绑定 { scopeKind: "device-field", scopeKey: 设备身份 }。分组是设备名，按设备种类、再按序号排。
 * 每项带当前读数来源设置下的显示（readingSource）：选择器只列 listed 的项，卡片和选择器按 mark 加标记。
 * 卡片：从绑定得到设备，从 id 去掉「@设备身份」后缀得到字段名，不解析设备身份；
 * 按字段订阅后取这台设备的值。设备不在目录里时写「设备不在」，设置保留，设备回来后照常显示；
 * 值不可用写「不可用」，不当 0。
 */

export const deviceFieldScopeKind = "device-field";

export interface FieldCardSlot {
  readonly metricId: string;
  readonly fieldName: string;
  readonly deviceIdentity: string;
}

/** 选择器里的种类顺序（后端目录的全部种类）；没列出的种类排在后面，按种类名排。 */
const deviceKindOrder = [
  "host",
  "cpu",
  "cpu_package",
  "cpu_die",
  "cpu_core",
  "cpu_logical_processor",
  "cpu_platform",
  "numa_node",
  "memory",
  "virtual_memory",
  "gpu_adapter",
  "gpu_engine",
  "gpu_fan_tuning_group",
  "npu",
  "npu_engine",
  "storage",
  "network_interface",
  "fan",
  "battery"
];

/** 后端约定的单例种类：整机只有一个，名字不带序号。 */
const singletonKinds = new Set(["host", "cpu", "memory", "virtual_memory"]);

/** 容量类字节值属于存储还是内存，按设备种类定。 */
const storageKinds = new Set(["storage", "network_interface"]);

export function fieldCardId(fieldName: string, deviceIdentity: string): string {
  return `${fieldName}@${deviceIdentity}`;
}

/** 一个槽位是不是设备字段：绑定是 device-field，且 id 以「@设备身份」结尾。 */
export function fieldCardSlot(
  metricId: string | null | undefined,
  binding: DashboardMetricBinding | null | undefined
): FieldCardSlot | null {
  if (!metricId || binding?.scopeKind !== deviceFieldScopeKind || !binding.scopeKey) {
    return null;
  }
  const suffix = `@${binding.scopeKey}`;
  return metricId.length > suffix.length && metricId.endsWith(suffix)
    ? { metricId, fieldName: metricId.slice(0, -suffix.length), deviceIdentity: binding.scopeKey }
    : null;
}

export function fieldCardSlots(cards: readonly DashboardCardSettings[]): FieldCardSlot[] {
  const slots: FieldCardSlot[] = [];
  for (const card of cards) {
    const main = fieldCardSlot(card.main, card.mainBinding);
    if (main) slots.push(main);
    (card.small ?? []).forEach((metricId, index) => {
      const slot = fieldCardSlot(metricId, card.smallBindings?.[index]);
      if (slot) slots.push(slot);
    });
  }
  return slots;
}

export function fieldCardDefinitions(catalog: readonly MetricFieldCatalogDevice[]): MetricDefinition[] {
  const cardFields = (device: MetricFieldCatalogDevice) =>
    device.fields.filter((field) => !Object.hasOwn(cardExcludedFields, field.name));
  const readingSource = readingSourceDisplays(catalog.flatMap((device) => cardFields(device).map((field) => field.reading)));
  return [...catalog].sort(compareDevices).flatMap((device) => {
    const group = deviceLabel(device, catalog);
    return cardFields(device)
      .map((field) => ({
        id: fieldCardId(field.name, device.deviceIdentity),
        label: fieldLabel(field.name),
        group,
        unit: field.unit,
        preferredSlot: "small",
        detail: fieldHint(field.name),
        selectable: true,
        scopeKind: deviceFieldScopeKind,
        scopeKey: device.deviceIdentity,
        readingSource: readingSource(field.reading)
      }));
  });
}

/**
 * 卡片上设备字段的当前显示值。
 *
 * 目录还没到时不写设备在不在；设备值还没到的槽位不写，卡片按原有规则显示占位。
 */
export function fieldCardValues(
  slots: readonly FieldCardSlot[],
  catalog: readonly MetricFieldCatalogDevice[] | null,
  devices: readonly MetricFieldDevice[] | null
): Record<string, MetricValue> {
  const text = uiText.dashboard;
  const items: Record<string, MetricValue> = {};
  for (const slot of slots) {
    const known = catalog?.find((device) => device.deviceIdentity === slot.deviceIdentity);
    const label = fieldLabel(slot.fieldName);
    if (catalog && !known) {
      items[slot.metricId] = { id: slot.metricId, label, displayValue: text.deviceAbsent, detail: text.deviceAbsentHint };
      continue;
    }
    if (!devices) {
      continue;
    }
    const device = devices.find((candidate) => candidate.deviceIdentity === slot.deviceIdentity);
    if (!catalog && !device) {
      // 目录未到时，值里没有这台设备不能说明它不在，也不能说值不可用：先不写。
      continue;
    }
    const field = device?.fields.find((candidate) => candidate.name === slot.fieldName);
    const display = field ? formatFieldValue(field, byteQuantityKind(known ?? device)) : null;
    items[slot.metricId] = {
      id: slot.metricId,
      label,
      group: known && catalog ? deviceLabel(known, catalog) : undefined,
      displayValue: display ?? text.valueUnavailable,
      numericValue: field?.integerValue !== null && field?.integerValue !== undefined
        ? Number(field.integerValue)
        : field?.numericValue ?? null
    };
  }
  return items;
}

/**
 * 设备的显示名：设备自报的名字优先，没有时按种类和序号起名；单例种类只写种类名。
 * 显卡引擎没有自己的名字：目录里有同一 PnP 实例的显卡时，在序号名后带上显卡名，
 * 例如「显卡引擎 0 · NVIDIA GeForce RTX 5060 Laptop GPU」。
 */
export function deviceLabel(
  device: Pick<MetricFieldDeviceInfo, "kind" | "displayName" | "ordinal" | "address">,
  catalog: readonly Pick<MetricFieldDeviceInfo, "displayName" | "address">[]
): string {
  const kind = deviceKindLabel(device.kind);
  if (device.displayName) {
    return uiText.dashboard.namedDevice(kind, device.displayName);
  }
  const numbered = singletonKinds.has(device.kind) ? kind : uiText.dashboard.numberedDevice(kind, device.ordinal);
  const adapterName = device.address?.kind === "gpu_engine" ? gpuAdapterName(catalog, device.address.pnpInstanceId) : null;
  return adapterName ? uiText.dashboard.namedDevice(numbered, adapterName) : numbered;
}

function gpuAdapterName(catalog: readonly Pick<MetricFieldDeviceInfo, "displayName" | "address">[], pnpInstanceId: string): string | null {
  const adapter = catalog.find((device) => device.address?.kind === "gpu_adapter" && device.address.pnpInstanceId === pnpInstanceId);
  return adapter?.displayName ?? null;
}

export function deviceKindLabel(kind: string): string {
  return uiText.dashboard.deviceKind[kind] ?? kind;
}

export function fieldLabel(fieldName: string): string {
  return uiText.fieldLabel[fieldName]?.label ?? fieldName;
}

export function fieldHint(fieldName: string): string | null {
  return uiText.fieldLabel[fieldName]?.hint ?? null;
}

function byteQuantityKind(device: Pick<MetricFieldDeviceInfo, "kind"> | undefined): ByteQuantityKind {
  return device && storageKinds.has(device.kind) ? "storage" : "memory";
}

function compareDevices(left: MetricFieldCatalogDevice, right: MetricFieldCatalogDevice): number {
  return kindRank(left.kind) - kindRank(right.kind)
    || left.kind.localeCompare(right.kind)
    || left.ordinal - right.ordinal
    || left.deviceIdentity.localeCompare(right.deviceIdentity);
}

function kindRank(kind: string): number {
  const index = deviceKindOrder.indexOf(kind);
  return index < 0 ? deviceKindOrder.length : index;
}
