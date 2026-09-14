import { createSignal, onCleanup, onMount, type Accessor } from "solid-js";
import { subscribeShellHostMessages } from "../utils";
import { FrontendVisibilityDemandController } from "./FrontendVisibilityDemandController";
import type { FrontendWorkId } from "./frontendWorkIds";
import {
  frontendVisibilityDemandsAttribute,
  frontendVisibilitySurfaceAttribute,
  frontendVisibilitySurfaceSelector,
  readFrontendVisibilitySurface
} from "./frontendVisibilitySurface";
import {
  isFrontendSurfaceVisible,
  resolveFrontendVisibilityBounds,
  sameOrderedValues,
  visibleFrontendDemandIds,
  type FrontendVisibilityBounds
} from "./frontendVisibility";

const repairIntervalMilliseconds = 5_000;

interface FrontendVisibilitySurfaceRuntime {
  element: HTMLElement;
  demandIds: readonly string[];
  visible: boolean;
}

export interface FrontendWorkController {
  frontendVisible: Accessor<boolean>;
  activeVisibilityDemandIds: Accessor<readonly string[]>;
  activeWorkIds: Accessor<readonly FrontendWorkId[]>;
  isNeeded: (workId: FrontendWorkId) => boolean;
  registerVisibilityDemand: (
    demandId: string,
    workIds: readonly FrontendWorkId[]
  ) => () => void;
  refreshVisibilitySurfaces: () => void;
}

export function useFrontendWorkController(): FrontendWorkController {
  const [frontendVisible, setFrontendVisible] = createSignal(
    document.visibilityState === "visible");
  const [activeVisibilityDemandIds, setActiveVisibilityDemandIds] =
    createSignal<readonly string[]>([]);
  const [activeWorkIds, setActiveWorkIds] = createSignal<readonly FrontendWorkId[]>([]);
  const visibilityDemandControllers = new Map<string, FrontendVisibilityDemandController>();
  const visibilitySurfaces = new Map<HTMLElement, FrontendVisibilitySurfaceRuntime>();
  const workReferenceCounts = new Map<FrontendWorkId, number>();
  let visibleDemandIds = new Set<string>();
  let root: HTMLElement | null = null;
  let intersectionObserver: IntersectionObserver | null = null;
  let mutationObserver: MutationObserver | null = null;
  let repairTimer = 0;
  let hostVisible = true;

  const controller: FrontendWorkController = {
    frontendVisible,
    activeVisibilityDemandIds,
    activeWorkIds,
    isNeeded: (workId) => activeWorkIds().includes(workId),
    registerVisibilityDemand(demandId, workIds) {
      const demandController = new FrontendVisibilityDemandController(
        demandId,
        workIds,
        changeWorkReference);
      if (visibilityDemandControllers.has(demandController.demandId)) {
        throw new Error(
          `Frontend visibility demand '${demandController.demandId}' is already registered.`);
      }

      visibilityDemandControllers.set(demandController.demandId, demandController);
      demandController.setActive(
        frontendVisible() && visibleDemandIds.has(demandController.demandId));
      publishControllerState();
      let registered = true;
      return () => {
        if (!registered) {
          return;
        }
        registered = false;
        if (visibilityDemandControllers.get(demandController.demandId) !== demandController) {
          return;
        }
        demandController.dispose();
        visibilityDemandControllers.delete(demandController.demandId);
        publishControllerState();
      };
    },
    refreshVisibilitySurfaces: repairVisibilitySurfaces
  };

  onMount(() => {
    root = document.querySelector<HTMLElement>(".app-shell");
    if (!root) {
      return;
    }

    intersectionObserver = new IntersectionObserver(handleSurfaceIntersections, {
      root,
      threshold: 0
    });
    mutationObserver = new MutationObserver(() => {
      if (syncVisibilitySurfaces()) {
        publishSurfaceVisibility();
      }
    });
    mutationObserver.observe(root, {
      subtree: true,
      childList: true,
      attributes: true,
      attributeFilter: [
        frontendVisibilitySurfaceAttribute,
        frontendVisibilityDemandsAttribute
      ]
    });

    const handleDocumentVisibility = () => {
      syncFrontendVisibility();
      if (frontendVisible()) {
        repairVisibilitySurfaces();
      }
    };
    const unsubscribeHostMessages = subscribeShellHostMessages((message) => {
      if (message === "host.visibility:hidden") {
        hostVisible = false;
        syncFrontendVisibility();
      } else if (message === "host.visibility:visible") {
        hostVisible = true;
        syncFrontendVisibility();
        if (frontendVisible()) {
          repairVisibilitySurfaces();
        }
      }
    });

    document.addEventListener("visibilitychange", handleDocumentVisibility);
    repairTimer = window.setInterval(
      repairVisibilitySurfaces,
      repairIntervalMilliseconds);
    repairVisibilitySurfaces();

    onCleanup(() => {
      document.removeEventListener("visibilitychange", handleDocumentVisibility);
      unsubscribeHostMessages();
      intersectionObserver?.disconnect();
      mutationObserver?.disconnect();
      window.clearInterval(repairTimer);
      for (const demandController of visibilityDemandControllers.values()) {
        demandController.dispose();
      }
      visibilityDemandControllers.clear();
      visibilitySurfaces.clear();
      workReferenceCounts.clear();
      visibleDemandIds.clear();
      root = null;
      intersectionObserver = null;
      mutationObserver = null;
    });
  });

  return controller;

  function syncFrontendVisibility() {
    const nextVisible = hostVisible && document.visibilityState === "visible";
    if (nextVisible === frontendVisible()) {
      return;
    }

    setFrontendVisible(nextVisible);
    if (!nextVisible) {
      let changed = false;
      for (const surface of visibilitySurfaces.values()) {
        if (surface.visible) {
          surface.visible = false;
          changed = true;
        }
      }
      if (changed || visibleDemandIds.size > 0) {
        publishSurfaceVisibility();
      }
    }
  }

  function syncVisibilitySurfaces(
    visibilityBounds: FrontendVisibilityBounds = resolveFrontendVisibilityBounds(root)
  ) {
    if (!root) {
      return false;
    }

    const currentElements = new Set(
      root.querySelectorAll<HTMLElement>(frontendVisibilitySurfaceSelector));
    let changed = false;
    for (const [element] of visibilitySurfaces) {
      if (currentElements.has(element) && element.isConnected) {
        continue;
      }
      intersectionObserver?.unobserve(element);
      visibilitySurfaces.delete(element);
      changed = true;
    }

    for (const element of currentElements) {
      const parsed = readFrontendVisibilitySurface(element);
      if (!parsed) {
        if (visibilitySurfaces.delete(element)) {
          intersectionObserver?.unobserve(element);
          changed = true;
        }
        continue;
      }

      const current = visibilitySurfaces.get(element);
      if (!current) {
        visibilitySurfaces.set(element, {
          element,
          demandIds: parsed.demandIds,
          visible: frontendVisible() && isFrontendSurfaceVisible(element, visibilityBounds)
        });
        intersectionObserver?.observe(element);
        changed = true;
      } else if (!sameOrderedValues(current.demandIds, parsed.demandIds)) {
        current.demandIds = parsed.demandIds;
        changed = true;
      }
    }
    return changed;
  }

  function handleSurfaceIntersections(entries: IntersectionObserverEntry[]) {
    let changed = false;
    for (const entry of entries) {
      const runtime = visibilitySurfaces.get(entry.target as HTMLElement);
      if (!runtime) {
        continue;
      }

      const visible = frontendVisible() && entry.isIntersecting;
      if (runtime.visible !== visible) {
        runtime.visible = visible;
        changed = true;
      }
    }
    if (changed) {
      publishSurfaceVisibility();
    }
  }

  function repairVisibilitySurfaces() {
    const visibilityBounds = resolveFrontendVisibilityBounds(root);
    let changed = syncVisibilitySurfaces(visibilityBounds);
    for (const runtime of visibilitySurfaces.values()) {
      const visible = frontendVisible()
        && isFrontendSurfaceVisible(runtime.element, visibilityBounds);
      if (runtime.visible !== visible) {
        runtime.visible = visible;
        changed = true;
      }
    }
    if (changed) {
      publishSurfaceVisibility();
    }
  }

  function publishSurfaceVisibility() {
    visibleDemandIds = new Set(visibleFrontendDemandIds(visibilitySurfaces.values()));
    for (const demandController of visibilityDemandControllers.values()) {
      demandController.setActive(
        frontendVisible() && visibleDemandIds.has(demandController.demandId));
    }
    publishControllerState();
  }

  function publishControllerState() {
    const nextActiveDemandIds = Array.from(visibilityDemandControllers.values())
      .filter((demandController) => demandController.active)
      .map((demandController) => demandController.demandId)
      .sort();
    if (!sameOrderedValues(activeVisibilityDemandIds(), nextActiveDemandIds)) {
      setActiveVisibilityDemandIds(nextActiveDemandIds);
    }

    const previousWorkIds = activeWorkIds();
    const nextWorkIds = Array.from(workReferenceCounts.keys()).sort() as FrontendWorkId[];
    if (!sameOrderedValues(previousWorkIds, nextWorkIds)) {
      setActiveWorkIds(nextWorkIds);
    }
  }

  function changeWorkReference(workId: FrontendWorkId, delta: 1 | -1) {
    const nextCount = (workReferenceCounts.get(workId) ?? 0) + delta;
    if (nextCount <= 0) {
      workReferenceCounts.delete(workId);
    } else {
      workReferenceCounts.set(workId, nextCount);
    }
  }

}
