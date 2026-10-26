export interface FrontendVisibilitySurfaceObservation {
  demandIds: readonly string[];
  visible: boolean;
}

export function visibleFrontendDemandIds(
  surfaces: Iterable<FrontendVisibilitySurfaceObservation>
) {
  const visibleDemandIds = new Set<string>();
  for (const surface of surfaces) {
    if (!surface.visible) {
      continue;
    }
    for (const demandId of surface.demandIds) {
      visibleDemandIds.add(demandId);
    }
  }
  return Array.from(visibleDemandIds).sort();
}

export function sameOrderedValues<T>(left: readonly T[], right: readonly T[]) {
  return left.length === right.length
    && left.every((value, index) => value === right[index]);
}
