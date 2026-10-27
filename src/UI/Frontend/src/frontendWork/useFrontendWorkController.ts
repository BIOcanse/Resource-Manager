import { createSelector, createSignal, onCleanup, onMount, type Accessor } from "solid-js";
import { subscribeShellHostMessages } from "../utils.ts";
import { FrontendVisibilityDemandController } from "./FrontendVisibilityDemandController.ts";
import { FrontendVisibilityCooldown } from "./FrontendVisibilityCooldown.ts";
import { ResourceManager } from "../preferences/generated/frontendPoints.values.ts";
import type { FrontendVisibilitySourceKind, FrontendWorkId } from "./frontendWorkIds";
import {
  type FrontendVisibilitySourceDemand,
  frontendVisibilityDemandsAttribute,
  frontendVisibilitySurfaceAttribute,
  frontendVisibilitySurfaceSelector,
  readFrontendVisibilitySurface
} from "./frontendVisibilitySurface.ts";
import {
  sameOrderedValues,
  visibleFrontendDemandIds
} from "./frontendVisibility.ts";

interface FrontendVisibilitySurfaceRuntime {
  element: HTMLElement;
  demandIds: readonly string[];
  visible: boolean;
}

export interface FrontendWorkController {
  frontendVisible: Accessor<boolean>;
  /** 某类带来源需求当前可见的来源 ID，按需求 ID 排序。 */
  activeVisibilitySourceIds: (kind: FrontendVisibilitySourceKind) => readonly string[];
  activeWorkIds: Accessor<readonly FrontendWorkId[]>;
  isNeeded: (workId: FrontendWorkId) => boolean;
  registerVisibilityDemand: (
    demand: string | FrontendVisibilitySourceDemand,
    workIds: readonly FrontendWorkId[]
  ) => () => void;
  refreshVisibilitySurfaces: () => void;
}

export function useFrontendWorkController(): FrontendWorkController {
  const [frontendVisible, setFrontendVisible] = createSignal(true);
  const visibilityCooldown = new FrontendVisibilityCooldown(
    ResourceManager.FrontendPoints.VisibilityCooldownMs,
    visible => {
      if (visible === frontendVisible()) return;
      setFrontendVisible(visible);
      publishSurfaceVisibility();
    });
  const [activeSourceDemands, setActiveSourceDemands] =
    createSignal<readonly FrontendVisibilitySourceDemand[]>([]);
  const [activeWorkIds, setActiveWorkIds] = createSignal<readonly FrontendWorkId[]>([]);
  const visibilityDemandControllers = new Map<string, FrontendVisibilityDemandController>();
  const visibilitySurfaces = new Map<HTMLElement, FrontendVisibilitySurfaceRuntime>();
  const workReferenceCounts = new Map<FrontendWorkId, number>();
  let visibleDemandIds = new Set<string>();
  let root: HTMLElement | null = null;
  let intersectionObserver: IntersectionObserver | null = null;
  let mutationObserver: MutationObserver | null = null;

  let hostVisible = true;
  // 每项工作的“是否需要”只在这一项变化时通知：消费端在 effect 里读它决定是否持有订阅，
  // 跟踪整份列表会让无关工作变化或同一需求的注销再注册（卡片重建）把订阅退掉重订，合并通道随之整条重建。
  const isNeeded = createSelector<readonly FrontendWorkId[], FrontendWorkId>(
    activeWorkIds, (workId, ids) => ids.includes(workId));

  const controller: FrontendWorkController = {
    frontendVisible,
    activeVisibilitySourceIds: (kind) => activeSourceDemands()
      .filter((demand) => demand.kind === kind)
      .map((demand) => demand.sourceId),
    activeWorkIds,
    isNeeded,
    registerVisibilityDemand(demand, workIds) {
      const demandController = new FrontendVisibilityDemandController(
        demand,
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
    refreshVisibilitySurfaces
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
        refreshVisibilitySurfaces();
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
          refreshVisibilitySurfaces();
        }
      }
    });

    document.addEventListener("visibilitychange", handleDocumentVisibility);
    syncFrontendVisibility();
    refreshVisibilitySurfaces();

    onCleanup(() => {
      document.removeEventListener("visibilitychange", handleDocumentVisibility);
      unsubscribeHostMessages();
      visibilityCooldown.dispose();
      intersectionObserver?.disconnect();
      mutationObserver?.disconnect();
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
    visibilityCooldown.setVisible(nextVisible);
  }
  function syncVisibilitySurfaces() {
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
          visible: false
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
    // A minimized/occluded document may deliver empty geometry. Preserve the
    // last visible observation; only the cooldown may withdraw window demand.
    if (!hostVisible || document.visibilityState !== "visible") return;
    let changed = false;
    for (const entry of entries) {
      const runtime = visibilitySurfaces.get(entry.target as HTMLElement);
      if (!runtime) {
        continue;
      }

      const visible = entry.isIntersecting;
      if (runtime.visible !== visible) {
        runtime.visible = visible;
        changed = true;
      }
    }
    if (changed) {
      publishSurfaceVisibility();
    }
  }

  function refreshVisibilitySurfaces() {
    // 只同步标记和消费已交付的观察；新元素等待浏览器布局后的首个相交结果。
    const changed = syncVisibilitySurfaces();
    if (changed) publishSurfaceVisibility();
    const entries = intersectionObserver?.takeRecords() ?? [];
    if (entries.length) handleSurfaceIntersections(entries);
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
    const nextActiveSourceDemands = Array.from(visibilityDemandControllers.values())
      .filter((demandController) => demandController.active && demandController.source)
      .map((demandController) => demandController.source!)
      .sort((left, right) => left.demandId < right.demandId ? -1 : left.demandId > right.demandId ? 1 : 0);
    if (!sameOrderedValues(
      activeSourceDemands().map((demand) => demand.demandId),
      nextActiveSourceDemands.map((demand) => demand.demandId))) {
      setActiveSourceDemands(nextActiveSourceDemands);
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
