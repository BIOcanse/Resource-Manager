import type { ProcessMemoryQuantity, ProcessMetricField } from "../../data/resourceMonitor/processFieldCatalog.ts";
import type { ResourceTableColumnSettings } from "../../types.ts";
import { resourceMetricBasis, resourceTableColumnLabel } from "../../presentation/metricLabels.ts";
import { uiText } from "../../text.ts";

/*
 * 资源表一列先选项目、再选来源（docs/metric_item_source_choice_2026-10-25.md）。
 *
 * 列是项目（如“内存”）。来源第一项是“自动选择”：列存 source = null，资源事实请求列原来的指标
 * （memory.usage，后端按它的口径给进程值和整机总量）。其余每项是后端进程字段目录里同一资源的一个口径
 * （字段的 reading.resource 相同），列存字段名，资源事实只请求这个字段；它读不到时这一列显示不可用，
 * 不换成别的口径。总计行、比例和“系统/驱动保留”行都由后端随这个字段一起给出（totalMetricId 指向的整机事实）。
 *
 * 资源事实接受哪些进程字段，后端目录没有说；合同（docs/metric_reading_source_selection.md「进程内存读取项与资源事实」）
 * 写明接受工作集、私有工作集、私有提交量，峰值提交量本轮不进资源事实。这里按合同列出，等后端目录给出这一事实后改为读目录。
 */
const resourceFactQuantityList = ["working_set_including_shared", "private_working_set", "private_commit"] as const;
export type ResourceFactMemoryQuantity = typeof resourceFactQuantityList[number];
const resourceFactQuantities: ReadonlySet<ProcessMemoryQuantity> = new Set(resourceFactQuantityList);
const isResourceFactQuantity = (quantity: ProcessMemoryQuantity | undefined): quantity is ResourceFactMemoryQuantity =>
  quantity !== undefined && resourceFactQuantities.has(quantity);

/** 有来源可选的列及其资源（进程字段 reading.resource）。其余列没有来源选择。 */
const columnResources: Readonly<Record<string, "memory">> = { memory: "memory" };

/** 列在“自动选择”下请求的指标。 */
const automaticMetricIds: Readonly<Record<string, string>> = {
  cpu: "cpu.usage", memory: "memory.usage", disk: "disk.io", network: "network.traffic"
};

export interface ResourceColumnSourceChoice {
  /** 选这一项时列存的来源：null 是自动选择，其余是进程字段名。 */
  readonly source: string | null;
  readonly field: ProcessMetricField | null;
}

/** 一列能选的来源；没有可选来源时为空数组（不摆选择）。 */
export function resourceColumnSourceChoices(columnId: string, fields: readonly ProcessMetricField[]): readonly ResourceColumnSourceChoice[] {
  const resource = columnResources[columnId];
  if (!resource) return [];
  const sources = fields.filter((field) => field.reading?.resource === resource && isResourceFactQuantity(field.reading.quantity));
  return sources.length === 0 ? [] : [{ source: null, field: null }, ...sources.map((field) => ({ source: field.name, field }))];
}

/** 列（含它的来源）在资源事实里对应的指标 id；不是数值列时为 null。 */
export function resourceTableMetricId(column: Pick<ResourceTableColumnSettings, "id" | "source">): string | null {
  if (column.source && columnResources[column.id]) return column.source;
  return automaticMetricIds[column.id] ?? (/^gpu\.\d+\.(usage|vram)$/.test(column.id) ? column.id : null);
}

/** 这一列是不是数值列（有资源事实指标）。 */
export function isResourceTableMetricColumn(columnId: string): boolean {
  return resourceTableMetricId({ id: columnId }) !== null;
}

/** 表头和说明：自动选择用列原来的名字和说明；选了来源就写出这个口径，整机总量的说明跟着口径走。 */
export interface ResourceTableColumnText {
  readonly label: string;
  readonly basis: string | undefined;
}

export function resourceTableColumnText(
  column: Pick<ResourceTableColumnSettings, "id" | "source">,
  fields: readonly ProcessMetricField[]
): ResourceTableColumnText {
  if (!column.source || !columnResources[column.id]) {
    return { label: resourceTableColumnLabel(column.id), basis: resourceMetricBasis(column.id, "table") };
  }
  const quantity = fields.find((field) => field.name === column.source)?.reading?.quantity;
  const text = uiText.resourceTable;
  // 目录还没到（或字段已不在目录里）时写出字段名本身，不冒用自动选择的名字。
  return isResourceFactQuantity(quantity)
    ? { label: text.memoryColumnBySource(text.memoryQuantities[quantity]),
      // 私有工作集就是自动选择现在的口径，说明和自动选择共用一句。
      basis: quantity === "private_working_set" ? resourceMetricBasis(column.id, "table") : text.memorySourceBasis[quantity] }
    : { label: text.memoryColumnBySource(column.source), basis: undefined };
}

/** 来源选项的显示名。 */
export function resourceColumnSourceLabel(choice: ResourceColumnSourceChoice): string {
  const quantity = choice.field?.reading?.quantity;
  return isResourceFactQuantity(quantity) ? uiText.resourceTable.memoryQuantities[quantity] : uiText.resourceTable.sourceAutomatic;
}
