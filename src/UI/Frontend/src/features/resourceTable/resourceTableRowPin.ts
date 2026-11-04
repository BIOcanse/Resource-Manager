import type { ResourceTableRow } from "../../types";

/** Pin a context-menu row at its current index while values update; release on close and never reserve a position for a removed row. */
export function pinResourceTableRow(
  rows: readonly ResourceTableRow[],
  pinnedRowId: string | null,
  pinnedIndex: number | null
): readonly ResourceTableRow[] {
  if (!pinnedRowId || pinnedIndex === null || pinnedIndex < 0) {
    return rows;
  }

  const currentIndex = rows.findIndex((row) => row.id === pinnedRowId);
  if (currentIndex < 0) {
    return rows;
  }

  const target = Math.min(pinnedIndex, rows.length - 1);
  if (currentIndex === target) {
    return rows;
  }

  const reordered = [...rows];
  const [pinned] = reordered.splice(currentIndex, 1);
  reordered.splice(target, 0, pinned);
  return reordered;
}
