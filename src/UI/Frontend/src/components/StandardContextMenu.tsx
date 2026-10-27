import { createEffect, createSignal, For, on, onCleanup, Show } from "solid-js";
import { restoreFocusTarget, scheduleFocusRestore } from "../interactions/focusReturn.ts";
import { exitOnUnmount } from "../presentation/nodeAnimation.ts";

/**
 * 这一项的原因写在哪一项下面：不能用的项挨着、原因又相同时（例如只提供查看的启动配置下的几项系统操作），
 * 只在第一项下写一次，后面几项把它当作自己的描述。不能用或没写原因时是 null。
 */
export function disabledReasonOwner(items: readonly StandardContextMenuItem[], index: number): number | null {
  const reason = items[index]?.disabled ? items[index].disabledReason : undefined;
  if (!reason) return null;
  let owner = index;
  while (owner > 0 && items[owner - 1].disabled && items[owner - 1].disabledReason === reason) owner -= 1;
  return owner;
}

export interface StandardContextMenuItem {
  id: string;
  label: string;
  disabled?: boolean;
  danger?: boolean;
  /** 不能用的原因：在这一项里写在名称下面一行（禁用的按钮收不到指针，原因要看得见）。 */
  disabledReason?: string;
  separatorBefore?: boolean;
  onSelect?: () => void;
}

export interface StandardContextMenuModel {
  id: string;
  x: number;
  y: number;
  // 至少一项可执行：唯一的生产者 createSoftwareContextMenuModel 的“转到详细信息”恒可用。
  items: StandardContextMenuItem[];
  returnFocusTarget?: HTMLElement | null;
}

type ContextMenuCloseReason = "return-focus" | "pointer" | "command";

interface ContextMenuFocusSession {
  modelId: string;
  target: HTMLElement | null;
  focusKey: string | undefined;
}

export function StandardContextMenu(props: {
  model: StandardContextMenuModel | null;
  onClose: () => void;
}) {
  let menu: HTMLDivElement | undefined;
  const [position, setPosition] = createSignal({ x: 0, y: 0 });
  const [activeIndex, setActiveIndex] = createSignal(-1);
  let focusSession: ContextMenuFocusSession | null = null;

  createEffect(() => {
    const model = props.model;
    if (!model) {
      return;
    }

    setPosition({ x: model.x, y: model.y });
    requestAnimationFrame(() => {
      if (!menu) {
        return;
      }

      const rect = menu.getBoundingClientRect();
      setPosition({
        x: Math.max(4, Math.min(model.x, window.innerWidth - rect.width - 4)),
        y: Math.max(4, Math.min(model.y, window.innerHeight - rect.height - 4))
      });
    });
  });

  createEffect(on(
    () => props.model?.id ?? null,
    (modelId, previousModelId) => {
      if (!modelId) {
        if (previousModelId && focusSession?.modelId === previousModelId) {
          const abandonedSession = focusSession;
          focusSession = null;
          scheduleFocusRestore(abandonedSession.target, abandonedSession.focusKey);
        }
        return;
      }

      const model = props.model;
      if (!model || model.id !== modelId) {
        return;
      }

      const activeElement = document.activeElement instanceof HTMLElement
        ? document.activeElement
        : null;
      const returnTarget = model.returnFocusTarget ?? activeElement;
      focusSession = {
        modelId,
        target: returnTarget,
        focusKey: returnTarget?.dataset.focusKey
      };
      const closeOnPointer = () => closeMenu("pointer");
      const closeOnExternalScrollIntent = (event: Event) => {
        if (!(event.target instanceof Node) || !menu?.contains(event.target)) {
          closeMenu("return-focus");
        }
      };
      const closeOnResize = () => closeMenu("return-focus");

      window.addEventListener("pointerdown", closeOnPointer);
      window.addEventListener("wheel", closeOnExternalScrollIntent, { passive: true });
      window.addEventListener("touchmove", closeOnExternalScrollIntent, { passive: true });
      window.addEventListener("resize", closeOnResize);
      onCleanup(() => {
        window.removeEventListener("pointerdown", closeOnPointer);
        window.removeEventListener("wheel", closeOnExternalScrollIntent);
        window.removeEventListener("touchmove", closeOnExternalScrollIntent);
        window.removeEventListener("resize", closeOnResize);
      });
    }
  ));

  createEffect(() => {
    const model = props.model;
    if (!model) {
      return;
    }

    setActiveIndex(firstEnabledMenuItemIndex(model.items));
    requestAnimationFrame(() => focusActiveMenuItem());
  });

  const closeMenu = (reason: ContextMenuCloseReason) => {
    const closingModel = props.model;
    const modelTarget = closingModel?.returnFocusTarget ?? null;
    const closingSession = focusSession?.target || focusSession?.focusKey
      ? focusSession
      : closingModel
        ? {
            modelId: closingModel.id,
            target: modelTarget,
            focusKey: modelTarget?.dataset.focusKey
          }
        : null;
    focusSession = null;
    if (reason !== "pointer" && closingSession) {
      restoreFocusTarget(closingSession.target, closingSession.focusKey);
    }
    props.onClose();
    if (reason !== "pointer" && closingSession) {
      scheduleFocusRestore(closingSession.target, closingSession.focusKey);
    }
  };

  const moveActive = (direction: 1 | -1) => {
    const items = props.model?.items ?? [];
    if (items.length === 0) {
      return;
    }

    let index = activeIndex();
    if (index < 0) {
      index = direction === 1 ? -1 : 0;
    }
    for (let attempt = 0; attempt < items.length; attempt += 1) {
      index = (index + direction + items.length) % items.length;
      if (!items[index].disabled) {
        setActiveIndex(index);
        focusActiveMenuItem();
        return;
      }
    }
  };

  const moveToBoundary = (boundary: "first" | "last") => {
    const items = props.model?.items ?? [];
    const index = boundary === "first"
      ? firstEnabledMenuItemIndex(items)
      : lastEnabledMenuItemIndex(items);
    if (index >= 0) {
      setActiveIndex(index);
      focusActiveMenuItem();
    }
  };

  const handleMenuKeyDown = (event: KeyboardEvent) => {
    if (event.key === "Escape" || event.key === "Tab") {
      event.preventDefault();
      event.stopPropagation();
      closeMenu("return-focus");
      return;
    }
    if (event.key === "PageUp" || event.key === "PageDown") {
      event.preventDefault();
      closeMenu("return-focus");
      return;
    }
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      moveActive(event.key === "ArrowDown" ? 1 : -1);
      return;
    }
    if (event.key === "Home" || event.key === "End") {
      event.preventDefault();
      moveToBoundary(event.key === "Home" ? "first" : "last");
    }
  };

  const focusActiveMenuItem = () => {
    requestAnimationFrame(() => {
      menu?.querySelector<HTMLButtonElement>(`[data-context-menu-index="${activeIndex()}"]`)
        ?.focus({ preventScroll: true });
    });
  };

  return (
    <Show when={props.model}>
      {(model) => (
        <div
          ref={(element) => { menu = element; exitOnUnmount(element); }}
          class="standard-context-menu"
          role="menu"
          tabIndex={-1}
          style={{ left: `${position().x}px`, top: `${position().y}px` }}
          onPointerDown={(event) => event.stopPropagation()}
          onContextMenu={(event) => event.preventDefault()}
          onKeyDown={handleMenuKeyDown}
        >
          <For each={model().items}>
            {(item, index) => (
              <>
                <Show when={item.separatorBefore}>
                  <div class="standard-context-menu-separator" role="separator" />
                </Show>
                <button
                  type="button"
                  role="menuitem"
                  tabIndex={index() === activeIndex() ? 0 : -1}
                  data-context-menu-index={index()}
                  class="standard-context-menu-item"
                  classList={{ danger: item.danger }}
                  disabled={item.disabled}
                  // 原因写在项里，但项的名称只是它的名字，原因作为描述读出（原因相同的几项共用第一项下的那一行）。
                  aria-label={item.label}
                  aria-describedby={(() => {
                    const owner = disabledReasonOwner(model().items, index());
                    return owner === null ? undefined : `${model().id}-reason-${owner}`;
                  })()}
                  onFocus={() => setActiveIndex(index())}
                  onPointerMove={() => {
                    if (!item.disabled) {
                      setActiveIndex(index());
                    }
                  }}
                  onClick={() => {
                    if (item.disabled) {
                      return;
                    }

                    closeMenu("command");
                    item.onSelect?.();
                  }}
                >
                  {item.label}
                  <Show when={disabledReasonOwner(model().items, index()) === index() ? item.disabledReason : undefined}>
                    {(reason) => <small id={`${model().id}-reason-${index()}`} class="standard-context-menu-reason">{reason()}</small>}
                  </Show>
                </button>
              </>
            )}
          </For>
        </div>
      )}
    </Show>
  );
}

function firstEnabledMenuItemIndex(items: readonly StandardContextMenuItem[]) {
  return items.findIndex((item) => !item.disabled);
}

function lastEnabledMenuItemIndex(items: readonly StandardContextMenuItem[]) {
  for (let index = items.length - 1; index >= 0; index -= 1) {
    if (!items[index].disabled) {
      return index;
    }
  }
  return -1;
}
