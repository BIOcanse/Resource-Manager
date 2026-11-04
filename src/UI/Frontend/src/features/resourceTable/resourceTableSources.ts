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

/* Automatic columns request their metric; explicit choices request the declared process field. Preserve source unavailability and use backend totals, proportions, and residuals unchanged. */

/** Metric requested by automatic selection. */
const automaticMetricIds: Readonly<Record<string, string>> = {
  cpu: "cpu.usage", memory: "memory.usage", disk: "disk.io", network: "network.traffic"
};

/** Memory columns also accept fields compared against system commit; each field retains its own totalMetricId. */
const comparableTotalMetricIds: Readonly<Record<string, readonly string[]>> = {
  memory: ["memory.usage", "virtualMemory.usage"]
};

export interface ResourceColumnSourceChoice {
  /** Store null for automatic selection or the process field name. */
  readonly source: string | null;
  readonly field: ProcessMetricField | null;
}

/** Return selectable sources, or an empty array to omit the control. */
export function resourceColumnSourceChoices(columnId: string, fields: readonly ProcessMetricField[]): readonly ResourceColumnSourceChoice[] {
  const field = fields.find(field => field.name === columnId);
  if (field?.table) return [{ source: null, field: null }, ...field.table.sources.map(source => ({ source: source.fieldName,
    field: fields.find(field => field.name === source.fieldName) ?? null }))];
  const metricId = resourceTableMetricId({ id: columnId });
  if (metricId === null) return [];
  const sources = processResourceSources(comparableTotalMetricIds[columnId] ?? [metricId], fields);
  return sources.length === 0 ? [] : [{ source: null, field: null }, ...sources.map((field) => ({ source: field.name, field }))];
}

/** Return the resource-facts metric ID, or null for nonnumeric columns. */
export function resourceTableMetricId(column: Pick<ResourceTableColumnSettings, "id" | "source">): string | null {
  const automatic = automaticMetricIds[column.id] ?? (/^gpu\.\d+\.(usage|vram)$/.test(column.id) || column.id.startsWith("process_") ? column.id : null);
  return automatic === null ? null : column.source || automatic;
}

/** Whether the column has a resource-facts metric. */
export function isResourceTableMetricColumn(columnId: string): boolean {
  return resourceTableMetricId({ id: columnId }) !== null;
}

/** Explicit sources determine labels and total explanations. */
export interface ResourceTableColumnText {
  readonly label: string;
  readonly basis: string | undefined;
}

/** actualFieldName identifies the field used for automatic selection only. */
export function resourceTableColumnText(
  column: Pick<ResourceTableColumnSettings, "id" | "source">,
  fields: readonly ProcessMetricField[],
  actualFieldName: string | null = null
): ResourceTableColumnText {
  const field = fields.find(field => field.name === column.id);
  if (field?.table) return { label: [field.reading?.resource === "gpu" && field.resourceFactTotalMetricId
    ? resourceTableColumnLabel(field.resourceFactTotalMetricId) : null, processSourceName(column.id, fields)].filter(Boolean).join(" · "), basis: field.sourceId };
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
  // If a saved field is absent from the catalog, display its name without claiming automatic selection.
  return {
    label: column.id === "memory" ? text.memoryColumnBySource(processSourceName(column.source, fields))
      : `${resourceTableColumnLabel(column.id)} · ${processSourceName(column.source, fields)}`,
    // Private working set shares the automatic-source explanation.
    basis: processSourceIsNonAdditive(column.source, fields)
      ? [text.nonAdditiveBasis, quantityBasis].filter(Boolean).join("\n") : quantityBasis
  };
}

/** Include the actual field in automatic-source option labels. */
export function resourceColumnSourceLabel(
  choice: ResourceColumnSourceChoice,
  fields: readonly ProcessMetricField[],
  actualFieldName: string | null = null
): string {
  return choice.field ? processSourceName(choice.field.name, fields) : automaticProcessSourceLabel(actualFieldName, fields);
}
