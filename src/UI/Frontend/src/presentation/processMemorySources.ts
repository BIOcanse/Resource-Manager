import type { ProcessMemoryQuantity, ProcessMetricField } from "../data/resourceMonitor/processFieldCatalog.ts";
import { uiText } from "../text.ts";

/*
 * 进程来源候选及其名称（docs/metric_item_source_choice_2026-10-25.md）：资源表和资源条共用。
 *
 * 候选是 supportsResourceFacts 且 resourceFactTotalMetricId 在项目可比较的整机指标里的字段，保持目录顺序，不另列字段白名单；
 * 项目可比较哪些整机指标由项目自己说（资源条是它自己的指标，资源表列见 resourceTableSources.ts）。
 * 名称全部按目录事实取：reading.resource 为 memory 的取内存口径名，为 gpu 的取 GPU 口径名
 * （engine_busy_percent 按目录的引擎范围和类别写出是哪一个引擎）；没有 reading 的字段按字段名取名称；
 * 都没有名字的写字段名本身，不冒用别的口径的名字。
 *
 * “自动选择”时后端在资源事实和资源条上写出这次实际读的字段（processFieldName），这里把它写成
 * “自动选择（当前为私有工作集）”，和选指标弹窗的自动选择同一句。
 */

const quantityNames = (): Readonly<Partial<Record<ProcessMemoryQuantity, string>>> => uiText.resourceTable.memoryQuantities;

/** Candidate membership belongs to the catalog, independently of resource/quantity labels. */
export function processResourceSources(totalMetricIds: readonly string[], fields: readonly ProcessMetricField[]): readonly ProcessMetricField[] {
  return fields.filter(field => field.supportsResourceFacts && field.resourceFactTotalMetricId !== null
    && totalMetricIds.includes(field.resourceFactTotalMetricId));
}

/** 一个进程字段作来源时的名称；目录里没有这个字段或它没有名字时为字段名。 */
export function processSourceName(fieldName: string, fields: readonly ProcessMetricField[]): string {
  const field = fields.find((candidate) => candidate.name === fieldName);
  const text = uiText.resourceTable;
  if (field?.reading?.resource === "memory") {
    const quantity = processMemoryQuantity(fieldName, fields);
    return (quantity && quantityNames()[quantity]) || fieldName;
  }
  if (field?.reading?.resource === "gpu") {
    if (field.reading.quantity === "engine_busy_percent" && field.gpuEngine) {
      const engineClass = field.engineClass === null ? null : text.gpuEngineClasses[field.engineClass] ?? field.engineClass;
      return text.gpuEngineBusy(field.gpuEngine.engineIndex, field.gpuEngine.physicalIndex, engineClass);
    }
    return text.gpuQuantities[field.reading.quantity] ?? fieldName;
  }
  return text.processFieldNames[fieldName] ?? fieldName;
}

/** 这个字段的内存口径（目录里没有或不是内存口径为 null）。 */
export function processMemoryQuantity(fieldName: string, fields: readonly ProcessMetricField[]): ProcessMemoryQuantity | null {
  const reading = fields.find((field) => field.name === fieldName)?.reading;
  return reading?.resource === "memory" && Object.hasOwn(quantityNames(), reading.quantity)
    ? reading.quantity as ProcessMemoryQuantity : null;
}

/** 目录说这个字段的进程值不能相加时为 true；目录没有声明（null）或没有这个字段时为 false。 */
export function processSourceIsNonAdditive(fieldName: string | null, fields: readonly ProcessMetricField[]): boolean {
  return fieldName !== null && fields.find((field) => field.name === fieldName)?.additiveAcrossProcesses === false;
}

/** “自动选择”这一项的名字：写出后端这次实际读的字段；后端还没说（或这一项不是进程字段）时只写“自动选择”。 */
export function automaticProcessSourceLabel(actualFieldName: string | null | undefined, fields: readonly ProcessMetricField[]): string {
  return uiText.metricPicker.sourceAutomatic(actualFieldName ? processSourceName(actualFieldName, fields) : null);
}
