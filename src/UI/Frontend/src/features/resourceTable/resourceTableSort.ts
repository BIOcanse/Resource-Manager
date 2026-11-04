/** impact is the default weighted composite order rather than a column. */
export interface ResourceTableSort {
  readonly columnId: string;
  readonly direction: "asc" | "desc";
}

/** Composite order has no column sort arrow. */
export const defaultResourceTableSort: ResourceTableSort = Object.freeze({ columnId: "impact", direction: "desc" });

/** Cycle each header through descending, ascending, and composite order; a different column starts descending. */
export function nextResourceTableSort(current: ResourceTableSort, columnId: string): ResourceTableSort {
  if (current.columnId !== columnId) return { columnId, direction: "desc" };
  return current.direction === "desc" ? { columnId, direction: "asc" } : defaultResourceTableSort;
}
