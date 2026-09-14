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

export interface FrontendVisibilityBounds {
  top: number;
  right: number;
  bottom: number;
  left: number;
}

export function resolveFrontendVisibilityBounds(
  root: HTMLElement | null
): FrontendVisibilityBounds {
  const rootRect = root?.getBoundingClientRect();
  return {
    top: Math.max(0, rootRect?.top ?? 0),
    right: Math.min(window.innerWidth, rootRect?.right ?? window.innerWidth),
    bottom: Math.min(window.innerHeight, rootRect?.bottom ?? window.innerHeight),
    left: Math.max(0, rootRect?.left ?? 0)
  };
}

export function isFrontendSurfaceVisible(
  element: HTMLElement,
  bounds: FrontendVisibilityBounds
) {
  if (!element.isConnected || element.getClientRects().length === 0) {
    return false;
  }

  const surfaceRect = element.getBoundingClientRect();
  return surfaceRect.bottom > bounds.top
    && surfaceRect.top < bounds.bottom
    && surfaceRect.right > bounds.left
    && surfaceRect.left < bounds.right;
}

export function sameOrderedValues<T>(left: readonly T[], right: readonly T[]) {
  return left.length === right.length
    && left.every((value, index) => value === right[index]);
}
