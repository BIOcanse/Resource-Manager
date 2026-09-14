import type {
  TaskCenterItem,
  TaskCenterItemSource,
  TaskCenterSnapshot
} from "./TaskCenterProjection.ts";

export type TaskCenterView = "active" | "history" | "all";
export type TaskCenterVisibility = "user" | "diagnostics";

export interface TaskCenterSelector {
  readonly view?: TaskCenterView;
  readonly visibility?: TaskCenterVisibility;
  readonly source?: TaskCenterItemSource;
}

export function selectTaskCenterItems(
  snapshot: TaskCenterSnapshot,
  selector: TaskCenterSelector = {}
): readonly TaskCenterItem[] {
  const view = selector.view ?? "all";
  const visibility = selector.visibility ?? "user";
  return snapshot.items.filter((item) => {
    if (visibility === "user" && item.visibility !== "user") {
      return false;
    }
    if (selector.source && item.source !== selector.source) {
      return false;
    }
    if (view === "active" && !item.active) {
      return false;
    }
    if (view === "history" && item.active) {
      return false;
    }
    return true;
  });
}
