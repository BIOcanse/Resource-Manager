import type { ProcessMetricField } from "../../data/resourceMonitor/processFieldCatalog.ts";
import type { ResourceTableColumnSettings } from "../../types.ts";
import { resourceMetricBasis, resourceTableColumnLabel } from "../../presentation/metricLabels.ts";
import { uiText } from "../../text.ts";
import {
  automaticProcessSourceLabel,
  processMemoryQuantity,
  processMemorySourceName,
  processMemorySources
} from "../../presentation/processMemorySources.ts";

/*
 * 资源表一列先选项目、再选来源（docs/metric_item_source_choice_2026-10-25.md）。
 *
 * 列是项目（如“内存”）。来源第一项是“自动选择”：列存 source = null，资源事实请求列原来的指标
 * （memory.usage，后端按它的口径给进程值和整机总量，并在 processFieldName 写出这次实际读的字段，
 * 自动选择这一项和表头说明据此写出“现在是 私有工作集”）。其余每项是后端进程字段目录里同一资源、
 * 后端标了 supportsResourceFacts 的一个口径（presentation/processMemorySources.ts，和内存资源条同一出处），
 * 列存字段名，资源事实只请求这个字段；它读不到时这一列显示不可用，不换成别的口径。
 * 总计行、比例和“系统/驱动保留”行都由后端随这个字段一起给出（totalMetricId 指向的整机事实）。
 */

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
  if (!columnResources[columnId]) return [];
  const sources = processMemorySources(fields);
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

/**
 * actualFieldName 是自动选择时后端这次实际读的进程字段（资源事实的 processFieldName），没有为 null；
 * 选了来源的列不用它。
 */
export function resourceTableColumnText(
  column: Pick<ResourceTableColumnSettings, "id" | "source">,
  fields: readonly ProcessMetricField[],
  actualFieldName: string | null = null
): ResourceTableColumnText {
  if (!column.source || !columnResources[column.id]) {
    const basis = resourceMetricBasis(column.id, "table");
    return { label: resourceTableColumnLabel(column.id),
      basis: actualFieldName && columnResources[column.id] ? [automaticProcessSourceLabel(actualFieldName, fields), basis].filter(Boolean).join("\n") : basis };
  }
  const quantity = processMemoryQuantity(column.source, fields);
  const text = uiText.resourceTable;
  // 目录还没到（或字段已不在目录里）时写出字段名本身，不冒用自动选择的名字。
  return {
    label: text.memoryColumnBySource(processMemorySourceName(column.source, fields)),
    // 私有工作集就是自动选择现在的口径，说明和自动选择共用一句。
    basis: quantity === "private_working_set" ? resourceMetricBasis(column.id, "table")
      : quantity === "working_set_including_shared" || quantity === "private_commit" ? text.memorySourceBasis[quantity] : undefined
  };
}

/** 来源选项的显示名；自动选择写出后端这次实际读的字段（actualFieldName）。 */
export function resourceColumnSourceLabel(
  choice: ResourceColumnSourceChoice,
  fields: readonly ProcessMetricField[],
  actualFieldName: string | null = null
): string {
  return choice.field ? processMemorySourceName(choice.field.name, fields) : automaticProcessSourceLabel(actualFieldName, fields);
}
