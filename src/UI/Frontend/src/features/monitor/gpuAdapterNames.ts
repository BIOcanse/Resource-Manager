import type { MetricFieldCatalogDevice } from "../../data/metricFields/metricFieldSource.ts";
import type { MetricDefinition } from "../../types.ts";

/**
 * 后端编号的显卡（gpu.N.*）各自的名字，键是后端发的编号 N，原样不改、不重排。
 * 用户 10-27：「后端还是用读到的，前端直接用后端读到的，不过由于有显卡名字小字所以不会错」。
 *
 * gpu.N.* 指标的 scopeKind 是 gpu、scopeKey 是这块适配器的设备身份（后端 MetricCatalog：别名指标用 GpuMetrics.IdentityKey，
 * 字段绑定用 DeviceIdentity）；名字是字段目录里同一设备身份、种类为 gpu_adapter 的设备自报的 displayName，
 * 也就是监视卡片设备名用的同一份目录（fieldCards.ts 的 deviceLabel）。deviceIdentity 不用：没绑定字段的别名指标那一项为 null。
 * 目录里没有这块设备、设备没报名字时这个编号没有名字，界面只写编号，不猜；同一编号对上两个不同的名字时也不写。
 */
export function gpuAdapterNames(
  metrics: readonly Pick<MetricDefinition, "id" | "scopeKind" | "scopeKey">[],
  devices: readonly Pick<MetricFieldCatalogDevice, "deviceIdentity" | "kind" | "displayName">[] | null
): ReadonlyMap<string, string> {
  const nameByIdentity = new Map<string, string>();
  for (const device of devices ?? []) {
    const name = device.displayName?.trim();
    if (device.kind === "gpu_adapter" && name) nameByIdentity.set(device.deviceIdentity, name);
  }
  const names = new Map<string, string>();
  const conflicting = new Set<string>();
  for (const metric of metrics) {
    const index = gpuIndexOfMetric(metric.id);
    const name = index !== null && metric.scopeKind === "gpu" && metric.scopeKey ? nameByIdentity.get(metric.scopeKey) : undefined;
    if (index === null || name === undefined || conflicting.has(index)) continue;
    const known = names.get(index);
    if (known === undefined) names.set(index, name);
    else if (known !== name) { names.delete(index); conflicting.add(index); }
  }
  return names;
}

/** gpu.N.* 的编号 N（原样的字符串）；不是按编号的显卡指标为 null。 */
export function gpuIndexOfMetric(metricId: string): string | null {
  return /^gpu\.(\d+)\./.exec(metricId)?.[1] ?? null;
}

/** 这条指标所属显卡的名字；不是按编号的显卡指标，或后端没给名字时为 null。 */
export function gpuNameForMetric(metricId: string, names: ReadonlyMap<string, string>): string | null {
  const index = gpuIndexOfMetric(metricId);
  return index === null ? null : names.get(index) ?? null;
}
