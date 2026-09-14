import type { JSX } from "solid-js";

const dragThresholdPixels = 5;
const interactiveSelector = "button,input,select,textarea,a,[role='button'],[role='separator'],[contenteditable='true']";

export interface PointerReorderOptions {
  enabled: boolean;
  group: string;
  sourceId: string;
  onStart: (sourceId: string) => void;
  onOver: (targetId: string | null) => void;
  onCommit: (sourceId: string, targetId: string) => void;
  onEnd: () => void;
}

export interface PointerReorderAttributes {
  "data-pointer-reorder-group": string;
  "data-pointer-reorder-id": string;
  onPointerDown: JSX.EventHandlerUnion<HTMLElement, PointerEvent>;
}

export function pointerReorderProps(
  options: () => PointerReorderOptions
): PointerReorderAttributes {
  const current = options();
  return {
    "data-pointer-reorder-group": current.group,
    "data-pointer-reorder-id": current.sourceId,
    onPointerDown: (event) => startPointerReorder(event, options())
  };
}

function startPointerReorder(
  event: PointerEvent & { currentTarget: HTMLElement },
  options: PointerReorderOptions
) {
  if (!options.enabled || event.button !== 0 || event.isPrimary === false) {
    return;
  }

  const sourceElement = event.currentTarget;
  const interactive = (event.target as Element | null)?.closest(interactiveSelector);
  if (interactive && interactive !== sourceElement) {
    return;
  }
  event.stopPropagation();

  const pointerId = event.pointerId;
  const startX = event.clientX;
  const startY = event.clientY;
  let active = false;
  let targetId: string | null = null;

  const finish = (commit: boolean) => {
    document.removeEventListener("pointermove", move, true);
    document.removeEventListener("pointerup", up, true);
    document.removeEventListener("pointercancel", cancel, true);
    window.removeEventListener("blur", cancel);
    if (!active) {
      return;
    }

    document.body.classList.remove("pointer-reordering");
    if (commit && targetId && targetId !== options.sourceId) {
      options.onCommit(options.sourceId, targetId);
    }
    options.onEnd();
    suppressClickOnce(sourceElement);
  };

  const move = (moveEvent: PointerEvent) => {
    if (moveEvent.pointerId !== pointerId) {
      return;
    }

    if (!active) {
      const distance = Math.hypot(moveEvent.clientX - startX, moveEvent.clientY - startY);
      if (distance < dragThresholdPixels) {
        return;
      }

      active = true;
      document.body.classList.add("pointer-reordering");
      options.onStart(options.sourceId);
    }

    const candidate = document
      .elementFromPoint(moveEvent.clientX, moveEvent.clientY)
      ?.closest<HTMLElement>(`[data-pointer-reorder-group="${options.group}"]`);
    const nextTarget = candidate?.dataset.pointerReorderId ?? null;
    targetId = nextTarget && nextTarget !== options.sourceId ? nextTarget : null;
    options.onOver(targetId);
    moveEvent.preventDefault();
  };

  const up = (upEvent: PointerEvent) => {
    if (upEvent.pointerId !== pointerId) {
      return;
    }
    if (active) {
      upEvent.preventDefault();
    }
    finish(true);
  };

  const cancel = () => finish(false);
  document.addEventListener("pointermove", move, true);
  document.addEventListener("pointerup", up, true);
  document.addEventListener("pointercancel", cancel, true);
  window.addEventListener("blur", cancel, { once: true });
}

function suppressClickOnce(element: HTMLElement) {
  const suppress = (event: MouseEvent) => {
    event.preventDefault();
    event.stopImmediatePropagation();
  };
  element.addEventListener("click", suppress, { capture: true, once: true });
  window.setTimeout(() => element.removeEventListener("click", suppress, true), 0);
}
