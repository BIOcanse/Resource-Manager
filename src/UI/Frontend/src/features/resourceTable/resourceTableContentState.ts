import type { ObservationStatus } from "../../observation/observationState.ts";

export type ResourceTableContentState =
  | "pending"
  | "unavailable"
  | "ready-empty"
  | "filtered-empty";

export function classifyResourceTableContentState(input: {
  readonly hasSnapshot: boolean;
  readonly status: ObservationStatus;
  readonly businessRowCount: number;
  readonly visibleBusinessRowCount: number;
  readonly searchActive: boolean;
}): ResourceTableContentState | null {
  if (input.status === "error" || input.status === "profile-disabled") {
    return "unavailable";
  }
  if (!input.hasSnapshot) {
    // A received null is not a zero-row table and cannot justify a zero count.
    return input.status === "loading" ? "pending" : "unavailable";
  }
  if (input.businessRowCount > 0) {
    return input.searchActive && input.visibleBusinessRowCount === 0
      ? "filtered-empty"
      : null;
  }
  return "ready-empty";
}

export function resourceTableCountText(input: {
  readonly state: ResourceTableContentState | null;
  readonly businessRowCount: number;
  readonly visibleBusinessRowCount: number;
  readonly searchActive: boolean;
}): string {
  if (input.state === "pending" || input.state === "unavailable") return "--";
  return input.searchActive
    ? `${input.visibleBusinessRowCount} / ${input.businessRowCount}`
    : String(input.businessRowCount);
}
