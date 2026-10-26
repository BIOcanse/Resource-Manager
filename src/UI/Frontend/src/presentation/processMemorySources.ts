import type { ProcessMemoryQuantity, ProcessMetricField } from "../data/resourceMonitor/processFieldCatalog.ts";
import { uiText } from "../text.ts";

/*
 * 进程内存的来源（docs/metric_item_source_choice_2026-10-25.md）：资源表内存列和内存资源条共用。
 *
 * 来源就是后端进程字段目录里 reading.resource 为 memory、且后端标了 supportsResourceFacts 的字段，按目录顺序；
 * 能不能作资源事实只听目录，前端不另列清单。口径的名字按 reading.quantity 取；目录给了前端没有名字的口径时
 * 写字段名本身，不冒用别的口径的名字。
 *
 * “自动选择”时后端在资源事实和资源条上写出这次实际读的字段（processFieldName），这里把它写成
 * “自动选择（当前为私有工作集）”，和选指标弹窗的自动选择同一句。
 */

const quantityNames = (): Readonly<Partial<Record<ProcessMemoryQuantity, string>>> => uiText.resourceTable.memoryQuantities;

/** 内存的全部可选来源（不含自动选择），目录顺序。 */
export function processMemorySources(fields: readonly ProcessMetricField[]): readonly ProcessMetricField[] {
  return fields.filter((field) => field.reading?.resource === "memory" && field.supportsResourceFacts);
}

/** 一个进程字段的口径名；目录里没有这个字段或口径没有名字时为字段名。 */
export function processMemorySourceName(fieldName: string, fields: readonly ProcessMetricField[]): string {
  const quantity = fields.find((field) => field.name === fieldName)?.reading?.quantity;
  return (quantity && quantityNames()[quantity]) || fieldName;
}

/** 这个字段的口径（目录里没有为 null）。 */
export function processMemoryQuantity(fieldName: string, fields: readonly ProcessMetricField[]): ProcessMemoryQuantity | null {
  return fields.find((field) => field.name === fieldName)?.reading?.quantity ?? null;
}

/** “自动选择”这一项的名字：写出后端这次实际读的字段；后端还没说（或这一项不是进程字段）时只写“自动选择”。 */
export function automaticProcessSourceLabel(actualFieldName: string | null | undefined, fields: readonly ProcessMetricField[]): string {
  return uiText.metricPicker.sourceAutomatic(actualFieldName ? processMemorySourceName(actualFieldName, fields) : null);
}
