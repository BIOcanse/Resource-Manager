import type { ResourceTableColumn, ResourceTableRow, ResourceTableValue, ResourceTableViewMode } from "../../types.ts";
import { uiText } from "../../text.ts";

/**
 * Search-hidden peers are grouped in one "filtered" row so the visible rows still account for the total.
 * Parent rows already include collapsed children, so only peers of the current view count. A column is summed
 * only when its metric declares additiveAcrossProcesses in this frame; a non-additive column has no sum and says so.
 */
export function accountResourceTableVisibility(all: ResourceTableRow[], shown: ResourceTableRow[], mode: ResourceTableViewMode,
  columns: readonly ResourceTableColumn[]): ResourceTableRow[] {
  const isPeer = (row: ResourceTableRow) => mode === "process"
    ? row.kind === "process" || row.kind === "attribution" : row.kind === "software";
  const shownIds = new Set(shown.filter(isPeer).map(row => row.id));
  const hidden = all.filter(row => isPeer(row) && !shownIds.has(row.id));
  if (hidden.length === 0) return shown;
  const aggregate: ResourceTableRow = { id: "filtered:hidden", kind: "filtered", name: uiText.resourceTable.filteredRows,
    status: "", depth: 0, processCount: 0, impactScore: 0, values: {} };
  for (const column of columns) {
    if (column.additiveAcrossProcesses === false) {
      aggregate.values[column.id] = { value: null, percent: null, unit: column.unit, basis: uiText.resourceTable.nonAdditiveBasis };
      continue;
    }
    if (column.additiveAcrossProcesses !== true) continue;
    let sum: ResourceTableValue | undefined;
    for (const row of hidden) {
      const value = row.values[column.id];
      if (value?.value == null) continue;
      sum = { value: (sum?.value ?? 0) + value.value, unit: column.unit,
        percent: value.percent == null ? sum?.percent ?? null : (sum?.percent ?? 0) + value.percent };
    }
    if (sum) aggregate.values[column.id] = sum;
  }
  return [...shown, aggregate];
}
