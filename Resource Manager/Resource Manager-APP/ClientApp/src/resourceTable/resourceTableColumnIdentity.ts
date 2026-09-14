import type { ResourceTableColumn } from "../types";

export function resourceTableColumnsEqual(
  left: readonly ResourceTableColumn[],
  right: readonly ResourceTableColumn[]
) {
  if (left === right) {
    return true;
  }
  if (left.length !== right.length) {
    return false;
  }
  return left.every((column, index) => {
    const candidate = right[index];
    return candidate !== undefined
      && column.id === candidate.id
      && column.label === candidate.label
      && column.unit === candidate.unit
      && column.visible === candidate.visible
      && column.sortable === candidate.sortable
      && column.width === candidate.width;
  });
}
