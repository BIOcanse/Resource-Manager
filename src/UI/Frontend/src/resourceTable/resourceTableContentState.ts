export type ResourceTableContentState =
  | "ready-empty"
  | "filtered-empty";

export function classifyResourceTableContentState(input: {
  readonly businessRowCount: number;
  readonly visibleBusinessRowCount: number;
  readonly searchActive: boolean;
}): ResourceTableContentState | null {
  if (input.businessRowCount > 0) {
    return input.searchActive && input.visibleBusinessRowCount === 0
      ? "filtered-empty"
      : null;
  }
  return "ready-empty";
}
