import type { ProcessMetricField } from "../../data/resourceMonitor/processFieldCatalog.ts";
import type { ResourceBarQuery } from "../../data/resourceMonitor/resourceMonitorApi.ts";
import type { ResourceBarSettings, ResourceBreakdownBar } from "../../types.ts";
import { localizedMetricLabel, resourceMetricBasis } from "../../presentation/metricLabels.ts";
import {
  automaticProcessSourceLabel,
  processMemoryQuantity,
  processSourceName,
  processResourceSources
} from "../../presentation/processMemorySources.ts";
import { currentLanguage } from "../../i18n/appTextStore.ts";
import { normalizeResourceBarScaleMode } from "./resourceBarScaleCapabilities.ts";
import { uiText } from "../../text.ts";

/*
 * 资源条先选项目、再选来源（docs/metric_item_source_choice_2026-10-25.md）。
 *
 * 项目是资源条本身（例如物理内存 memory.usage、虚拟内存提交量 virtualMemory.usage）。来源第一项是“自动选择”：
 * 资源条存 source = null，按原来的指标 id 订阅（ids=），后端按它的口径给软件分项和整机总量，并在
 * processFieldName 写出这次实际读的进程字段。其余候选是目录标了 supportsResourceFacts 的进程字段（与资源表同一出处），
 * 而且只列后端说整机比较指标（resourceFactTotalMetricId）就是这条资源条的那些，例如：
 * 物理内存条列工作集类，提交量条列私有提交量。资源条存字段名，订阅时作显式口径（measures=）只读这个字段，
 * 缩放照常按 modes=字段名:缩放 传；总量、比例和“系统/驱动保留”由后端按这个字段的整机事实给出。
 * 选了的字段读不到总量时后端不发这一条，资源条就不显示，不换成别的口径。进程明细（scope=details）
 * 同样按 measures= 订阅这个字段，后端回的明细 metricId 就是字段名。
 */

/** 资源条加上它的来源：后端那一条按项目的 id 显示，选择、展开、排序都认项目。 */
export interface ResourceBarView extends ResourceBreakdownBar {
  /** 资源条存的来源；null 是自动选择。 */
  readonly source: string | null;
  /** 名字和说明（resourceBarText）：条头、分项提示都用它。 */
  readonly label: string;
  readonly basis: string | undefined;
  /** 来源的静态说明；进程明细不能借用 bars 订阅的采样窗口。 */
  readonly sourceBasis: string | undefined;
}

/** 一条资源条能选的来源：null 是自动选择，其余是整机比较指标就是这条资源条的进程字段名；没得选时为空数组（不摆选择）。 */
export function resourceBarSourceChoices(metricId: string, fields: readonly ProcessMetricField[]): readonly (string | null)[] {
  const sources = processResourceSources([metricId], fields);
  return sources.length === 0 ? [] : [null, ...sources.map((field) => field.name)];
}

/** 已保存的显式字段始终只请求自身；目录暂缺时也不能暗改为自动选择。 */
export function resourceBarSource(bar: Pick<ResourceBarSettings, "metricId" | "source">): string | null {
  return bar.source || null;
}

/**
 * 物理内存条的“系统/驱动保留”在后端的软件 id。物理内存类的条（自动选择或选了来源，整机比较指标都是 memory.usage）
 * 都把保留量放在这一行，分项是它下面 PID 为 -1 的行；进程明细（scope=details）带上这个 id 时，后端另外给出页帧明细帧。
 */
export const physicalMemoryResidualSoftwareId = "resource-residual:memory.usage";

/**
 * 资源条订阅（scope=bars）里要列出进程行的软件：有物理内存条时带上它的保留量，条上的提示才能列出分项。
 * 每条资源条无论 processDetails 怎样都带全部软件行；processDetails 只决定哪些软件行同时列出进程（分项），
 * 其余软件行保留 processCount、进程数组为空（权威 Bar software rows and process lists）。
 * 后端只在 scope=details 时读页帧明细，资源条订阅带这个 id 只多发保留量下面的计数器分项，不触发页帧读取。
 */
export function resourceBarDetailSoftwareIds(bars: readonly Pick<ResourceBarQuery, "metricId">[]): string[] {
  return bars.some((bar) => bar.metricId === "memory.usage") ? [physicalMemoryResidualSoftwareId] : [];
}

/** 资源条在订阅里的样子：指标 id、缩放和来源（自动选择为 null，按 ids= 订阅；选了来源按这个字段的 measures= 订阅）。 */
export function resourceBarQuery(bar: ResourceBarSettings): ResourceBarQuery {
  return { metricId: bar.metricId, scaleMode: normalizeResourceBarScaleMode(bar.metricId, bar.scaleMode), source: resourceBarSource(bar) };
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
    const text = resourceBarText({ metricId: setting.metricId, source, processFieldName: bar.processFieldName }, fields, bar.additiveAcrossProcesses);
    const window = bar.windowMilliseconds === null ? null
      : uiText.resourceTable.factsWindow((bar.windowMilliseconds / 1000).toLocaleString(currentLanguage(), { maximumFractionDigits: 3 }));
    views.push({ ...bar, metricId: setting.metricId, source, label: text.label, sourceBasis: text.basis,
      basis: [text.basis, window].filter(Boolean).join("\n") || undefined });
  }
  return views;
}

export interface ResourceBarText {
  readonly label: string;
  readonly basis: string | undefined;
}

/**
 * 资源条的名字和说明：自动选择用原来的名字，说明前写出后端这次实际读的字段；选了来源写出口径和它的整机比较。
 * additive 由调用方给出：显示中的资源条用它自己这一帧的 additiveAcrossProcesses，编辑时的来源选项用目录的声明
 * （processSourceAdditivity）；false 时写明不可相加，null 表示没有声明。
 */
export function resourceBarText(bar: Pick<ResourceBarView, "metricId" | "source" | "processFieldName">, fields: readonly ProcessMetricField[],
  additive: boolean | null): ResourceBarText {
  if (!bar.source) {
    const quantity = bar.processFieldName ? processMemoryQuantity(bar.processFieldName, fields) : null;
    const basis = withAdditivity(additive, quantity === "working_set_including_shared"
      ? uiText.resourceTable.memorySourceBasis.working_set_including_shared : resourceMetricBasis(bar.metricId));
    return { label: localizedMetricLabel(bar.metricId),
      basis: bar.processFieldName ? [automaticProcessSourceLabel(bar.processFieldName, fields), basis].filter(Boolean).join("\n") : basis };
  }
  const quantity = processMemoryQuantity(bar.source, fields);
  return {
    label: quantity ? uiText.resourceTable.memoryColumnBySource(processSourceName(bar.source, fields))
      : `${localizedMetricLabel(bar.metricId)} · ${processSourceName(bar.source, fields)}`,
    basis: withAdditivity(additive, quantity === "working_set_including_shared" ? uiText.resourceTable.memorySourceBasis.working_set_including_shared
      : quantity === "private_working_set" ? uiText.resourceBreakdown.memoryBasis
        : quantity === "private_commit" ? uiText.resourceBreakdown.commitBasis : undefined)
  };
}

/** 不可相加时在口径说明前写明，口径说明照常保留（与资源表表头相同）。 */
function withAdditivity(additive: boolean | null, basis: string | undefined): string | undefined {
  return additive === false ? [uiText.resourceTable.nonAdditiveBasis, basis].filter(Boolean).join("\n") : basis;
}

/** 编辑时来源选项的名字；自动选择写出这条资源条现在实际读的字段（没有显示或后端没说时只写“自动选择”）。 */
export function resourceBarSourceLabel(source: string | null, fields: readonly ProcessMetricField[], automaticField: string | null): string {
  return source ? processSourceName(source, fields) : automaticProcessSourceLabel(automaticField, fields);
}

/** 编辑时一个来源选项的可加性：目录对这个字段的声明；自动选择或目录没有这个字段为 null。 */
export function processSourceAdditivity(source: string | null, fields: readonly ProcessMetricField[]): boolean | null {
  return source === null ? null : fields.find((field) => field.name === source)?.additiveAcrossProcesses ?? null;
}
