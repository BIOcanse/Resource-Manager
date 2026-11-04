import type { ProcessMemoryQuantity, ProcessMetricField } from "../data/resourceMonitor/processFieldCatalog.ts";
import { uiText } from "../text.ts";

/* Tables and bars share catalog-ordered sources supporting resource facts and a comparable total metric. Use reading-specific memory/GPU labels, including GPU engine scope; fall back to field names. Automatic labels include the actual backend processFieldName when available. */

const quantityNames = (): Readonly<Partial<Record<ProcessMemoryQuantity, string>>> => uiText.resourceTable.memoryQuantities;

/** Candidate membership belongs to the catalog, independently of resource/quantity labels. */
export function processResourceSources(totalMetricIds: readonly string[], fields: readonly ProcessMetricField[]): readonly ProcessMetricField[] {
  return fields.filter(field => field.supportsResourceFacts && field.resourceFactTotalMetricId !== null
    && totalMetricIds.includes(field.resourceFactTotalMetricId));
}

/** Source label for a process field; fall back to its field name when absent or unnamed. */
export function processSourceName(fieldName: string, fields: readonly ProcessMetricField[]): string {
  const field = fields.find((candidate) => candidate.name === fieldName);
  if (!field) return fieldName;
  const text = uiText.resourceTable;
  if (field?.reading?.resource === "memory") {
    const quantity = processMemoryQuantity(fieldName, fields);
    return (quantity && quantityNames()[quantity]) || text.processFieldNames[fieldName] || fieldName;
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

/** Memory reading kind, or null when absent or non-memory. */
export function processMemoryQuantity(fieldName: string, fields: readonly ProcessMetricField[]): ProcessMemoryQuantity | null {
  const reading = fields.find((field) => field.name === fieldName)?.reading;
  return reading?.resource === "memory" && Object.hasOwn(quantityNames(), reading.quantity)
    ? reading.quantity as ProcessMemoryQuantity : null;
}

/** True only when the catalog explicitly declares process values non-additive. */
export function processSourceIsNonAdditive(fieldName: string | null, fields: readonly ProcessMetricField[]): boolean {
  return fieldName !== null && fields.find((field) => field.name === fieldName)?.additiveAcrossProcesses === false;
}

/** Automatic-source label includes the actual backend field when available; otherwise only the generic label. */
export function automaticProcessSourceLabel(actualFieldName: string | null | undefined, fields: readonly ProcessMetricField[]): string {
  return uiText.metricPicker.sourceAutomatic(actualFieldName ? processSourceName(actualFieldName, fields) : null);
}
