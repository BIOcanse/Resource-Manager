export type ResourceRowFocusMove = "previous" | "next" | "first" | "last";

export interface ResourceRowFocusCandidate {
  readonly id: string;
  readonly kind: string;
}

export function resourceTableFocusableRows<T extends ResourceRowFocusCandidate>(
  rows: readonly T[]
): T[] {
  return rows.filter((row) => row.kind === "software" || row.kind === "process");
}

export function reconcileResourceTableFocus(
  rows: readonly ResourceRowFocusCandidate[],
  currentId: string | null,
  preferredIndex = 0
): string | null {
  const candidates = resourceTableFocusableRows(rows);
  if (currentId && candidates.some((row) => row.id === currentId)) {
    return currentId;
  }
  if (candidates.length === 0) {
    return null;
  }
  return candidates[Math.min(candidates.length - 1, Math.max(0, preferredIndex))].id;
}

export function moveResourceTableFocus(
  rows: readonly ResourceRowFocusCandidate[],
  currentId: string | null,
  move: ResourceRowFocusMove
): string | null {
  const candidates = resourceTableFocusableRows(rows);
  if (candidates.length === 0) {
    return null;
  }
  if (move === "first") {
    return candidates[0].id;
  }
  if (move === "last") {
    return candidates[candidates.length - 1].id;
  }
  const currentIndex = Math.max(0, candidates.findIndex((row) => row.id === currentId));
  const delta = move === "previous" ? -1 : 1;
  return candidates[Math.min(
    candidates.length - 1,
    Math.max(0, currentIndex + delta))].id;
}
