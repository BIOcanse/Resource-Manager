import type { ProcessMetricField } from "../../data/resourceMonitor/processFieldCatalog.ts";
import type { ResourceBarQuery } from "../../data/resourceMonitor/resourceMonitorApi.ts";
import type { ResourceBarSettings, ResourceBreakdownBar } from "../../types.ts";
import { localizedMetricLabel, resourceMetricBasis } from "../../presentation/metricLabels.ts";
import {
  automaticProcessSourceLabel,
  processMemoryQuantity,
  processMemorySourceName,
  processMemorySources
} from "../../presentation/processMemorySources.ts";
import { normalizeResourceBarScaleMode } from "./resourceBarScaleCapabilities.ts";
import { uiText } from "../../text.ts";

/*
 * 资源条先选项目、再选来源（docs/metric_item_source_choice_2026-10-25.md）。
 *
 * 项目是资源条本身（物理内存 memory.usage、虚拟内存提交量 virtualMemory.usage）。来源第一项是“自动选择”：
 * 资源条存 source = null，按原来的指标 id 订阅（ids=），后端按它的口径给软件分项和整机总量，并在
 * processFieldName 写出这次实际读的进程字段。其余每项是进程内存的一个口径（presentation/processMemorySources.ts，
 * 和资源表内存列同一出处），资源条存字段名，订阅时作显式口径（measures=）只读这个字段；总量、比例和
 * “系统/驱动保留”由后端按这个字段的整机事实给出，工作集类对物理内存已用量、私有提交量对系统提交量。
 * 选了的字段读不到总量时后端不发这一条，资源条就不显示，不换成别的口径。
 *
 * 显式口径在后端只按容量比较（不接受 modes=），所以选了来源的资源条缩放固定为容量；
 * 进程明细（scope=details）也不接受显式口径，选了来源的资源条没有进程明细，面板写明不可用。
 */

/** 资源条加上它的来源：后端那一条按项目的 id 显示，选择、展开、排序都认项目。 */
export interface ResourceBarView extends ResourceBreakdownBar {
  /** 资源条存的来源；null 是自动选择。 */
  readonly source: string | null;
  /** 名字和说明（resourceBarText）：条头、分项提示都用它。 */
  readonly label: string;
  readonly basis: string | undefined;
}

/** 有来源可选的资源条：两条整机内存。 */
const memoryBarIds: ReadonlySet<string> = new Set(["memory.usage", "virtualMemory.usage"]);

/** 一条资源条能选的来源：null 是自动选择，其余是进程字段名；没得选时为空数组（不摆选择）。 */
export function resourceBarSourceChoices(metricId: string, fields: readonly ProcessMetricField[]): readonly (string | null)[] {
  if (!memoryBarIds.has(metricId)) return [];
  const sources = processMemorySources(fields);
  return sources.length === 0 ? [] : [null, ...sources.map((field) => field.name)];
}

/** 资源条实际用的来源：只有内存资源条认存的来源，其余为 null。 */
export function resourceBarSource(bar: Pick<ResourceBarSettings, "metricId" | "source">): string | null {
  return bar.source && memoryBarIds.has(bar.metricId) ? bar.source : null;
}

/** 资源条在订阅里的样子：自动选择按指标 id 和缩放；选了来源是这个字段的显式口径（缩放固定为容量）。 */
export function resourceBarQuery(bar: ResourceBarSettings): ResourceBarQuery {
  const source = resourceBarSource(bar);
  return source
    ? { metricId: bar.metricId, scaleMode: "capacity", source }
    : { metricId: bar.metricId, scaleMode: normalizeResourceBarScaleMode(bar.metricId, bar.scaleMode), source: null };
}

/** 这条资源条能选的缩放：选了来源只有容量。 */
export function resourceBarScaleModes(bar: Pick<ResourceBarSettings, "metricId" | "source">): readonly ("capacity" | "active")[] {
  if (resourceBarSource(bar)) return ["capacity"];
  return normalizeResourceBarScaleMode(bar.metricId, "capacity") === "capacity" ? ["capacity", "active"] : ["active"];
}

/**
 * 按资源条设置的顺序，把后端的条接到项目上：自动选择找指标 id 相同的那条，选了来源找这个字段那条。
 * 后端没发的（例如总量读不到）就没有这一条。
 */
export function resourceBarViews(
  snapshotBars: readonly ResourceBreakdownBar[],
  settings: readonly ResourceBarSettings[],
  fields: readonly ProcessMetricField[]
): ResourceBarView[] {
  const byId = new Map(snapshotBars.map((bar) => [bar.metricId, bar]));
  const views: ResourceBarView[] = [];
  for (const setting of settings) {
    const source = resourceBarSource(setting);
    const bar = byId.get(source ?? setting.metricId);
    if (!bar) continue;
    const text = resourceBarText({ metricId: setting.metricId, source, processFieldName: bar.processFieldName ?? null }, fields);
    views.push({ ...bar, metricId: setting.metricId, source, label: text.label, basis: text.basis });
  }
  return views;
}

export interface ResourceBarText {
  readonly label: string;
  readonly basis: string | undefined;
}

/** 资源条的名字和说明：自动选择用原来的名字，说明前写出后端这次实际读的字段；选了来源写出口径和它的整机比较。 */
export function resourceBarText(bar: Pick<ResourceBarView, "metricId" | "source" | "processFieldName">, fields: readonly ProcessMetricField[]): ResourceBarText {
  if (!bar.source) {
    const basis = resourceMetricBasis(bar.metricId);
    return { label: localizedMetricLabel(bar.metricId),
      basis: bar.processFieldName ? [automaticProcessSourceLabel(bar.processFieldName, fields), basis].filter(Boolean).join("\n") : basis };
  }
  const quantity = processMemoryQuantity(bar.source, fields);
  return {
    label: uiText.resourceTable.memoryColumnBySource(processMemorySourceName(bar.source, fields)),
    basis: quantity === "working_set_including_shared" ? uiText.resourceTable.memorySourceBasis.working_set_including_shared
      : quantity === "private_working_set" ? uiText.resourceBreakdown.memoryBasis
        : quantity === "private_commit" ? uiText.resourceBreakdown.commitBasis : undefined
  };
}

/** 编辑时来源选项的名字；自动选择写出这条资源条现在实际读的字段（没有显示或后端没说时只写“自动选择”）。 */
export function resourceBarSourceLabel(source: string | null, fields: readonly ProcessMetricField[], automaticField: string | null): string {
  return source ? processMemorySourceName(source, fields) : automaticProcessSourceLabel(automaticField, fields);
}
