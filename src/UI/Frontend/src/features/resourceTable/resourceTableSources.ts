import type { ProcessMetricField } from "../../data/resourceMonitor/processFieldCatalog.ts";
import type { ResourceTableColumnSettings } from "../../types.ts";
import { resourceMetricBasis, resourceTableColumnLabel } from "../../presentation/metricLabels.ts";
import { uiText } from "../../text.ts";
import {
  automaticProcessSourceLabel,
  processMemoryQuantity,
  processSourceName,
  processResourceSources,
  processSourceIsNonAdditive
} from "../../presentation/processMemorySources.ts";

/*
 * 资源表一列先选项目、再选来源（docs/metric_item_source_choice_2026-10-25.md）。
 *
 * 列是项目（如“内存”）。来源第一项是“自动选择”：列存 source = null，资源事实请求列原来的指标
 * （memory.usage，后端按它的口径给进程值和整机总量，并在 processFieldName 写出这次实际读的字段，
 * 自动选择这一项和表头说明据此写出实际来源）。其余每项是后端进程字段目录里
 * supportsResourceFacts=true 且 resourceFactTotalMetricId 等于本列自动指标的字段（和资源条同一出处），
 * 列存字段名，资源事实只请求这个字段；它读不到时这一列显示不可用，不换成别的口径。
 * 总计、比例和保留量直接读该帧后端事实；前端不重新结算。
 */

/** 列在“自动选择”下请求的指标。 */
const automaticMetricIds: Readonly<Record<string, string>> = {
  cpu: "cpu.usage", memory: "memory.usage", disk: "disk.io", network: "network.traffic"
};

/**
 * 列可比较的整机指标：默认是列自己的自动指标。内存列是进程内存的全部口径（合同 docs/metric_item_source_choice_2026-10-25.md），
 * 私有提交量的整机比较是系统提交量（用户 10-25「选私有提交量时，总计行显示什么；保持相同单位」），所以内存列还收
 * resourceFactTotalMetricId 为 virtualMemory.usage 的字段，总计行照这个字段自己的 totalMetricId 显示。
 */
const comparableTotalMetricIds: Readonly<Record<string, readonly string[]>> = {
  memory: ["memory.usage", "virtualMemory.usage"]
};

export interface ResourceColumnSourceChoice {
  /** 选这一项时列存的来源：null 是自动选择，其余是进程字段名。 */
  readonly source: string | null;
  readonly field: ProcessMetricField | null;
}

/** 一列能选的来源；没有可选来源时为空数组（不摆选择）。 */
export function resourceColumnSourceChoices(columnId: string, fields: readonly ProcessMetricField[]): readonly ResourceColumnSourceChoice[] {
  const metricId = resourceTableMetricId({ id: columnId });
  if (metricId === null) return [];
  const sources = processResourceSources(comparableTotalMetricIds[columnId] ?? [metricId], fields);
  return sources.length === 0 ? [] : [{ source: null, field: null }, ...sources.map((field) => ({ source: field.name, field }))];
}

/** 列（含它的来源）在资源事实里对应的指标 id；不是数值列时为 null。 */
export function resourceTableMetricId(column: Pick<ResourceTableColumnSettings, "id" | "source">): string | null {
  const automatic = automaticMetricIds[column.id] ?? (/^gpu\.\d+\.(usage|vram)$/.test(column.id) ? column.id : null);
  return automatic === null ? null : column.source || automatic;
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
  if (!column.source || !isResourceTableMetricColumn(column.id)) {
    const quantity = actualFieldName ? processMemoryQuantity(actualFieldName, fields) : null;
    const quantityBasis = quantity === "working_set_including_shared" || quantity === "private_commit"
      ? uiText.resourceTable.memorySourceBasis[quantity] : resourceMetricBasis(column.id, "table");
    const basis = actualFieldName && processSourceIsNonAdditive(actualFieldName, fields)
      ? [uiText.resourceTable.nonAdditiveBasis, quantityBasis].filter(Boolean).join("\n") : quantityBasis;
    return { label: resourceTableColumnLabel(column.id),
      basis: actualFieldName ? [automaticProcessSourceLabel(actualFieldName, fields), basis].filter(Boolean).join("\n") : basis };
  }
  const quantity = processMemoryQuantity(column.source, fields);
  const text = uiText.resourceTable;
  const quantityBasis = quantity === "private_working_set" ? resourceMetricBasis(column.id, "table")
    : quantity === "working_set_including_shared" || quantity === "private_commit" ? text.memorySourceBasis[quantity] : undefined;
  // 目录还没到（或字段已不在目录里）时写出字段名本身，不冒用自动选择的名字。
  return {
    label: column.id === "memory" ? text.memoryColumnBySource(processSourceName(column.source, fields))
      : `${resourceTableColumnLabel(column.id)} · ${processSourceName(column.source, fields)}`,
    // 私有工作集就是自动选择现在的口径，说明和自动选择共用一句。
    basis: processSourceIsNonAdditive(column.source, fields)
      ? [text.nonAdditiveBasis, quantityBasis].filter(Boolean).join("\n") : quantityBasis
  };
}

/** 来源选项的显示名；自动选择写出后端这次实际读的字段（actualFieldName）。 */
export function resourceColumnSourceLabel(
  choice: ResourceColumnSourceChoice,
  fields: readonly ProcessMetricField[],
  actualFieldName: string | null = null
): string {
  return choice.field ? processSourceName(choice.field.name, fields) : automaticProcessSourceLabel(actualFieldName, fields);
}
