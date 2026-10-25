import { createEffect, onCleanup, type Accessor } from "solid-js";
import { restoreFocusTarget } from "./focusReturn.ts";

interface ModalFocusOptions {
  open: Accessor<boolean>;
  backdrop: () => HTMLElement | undefined;
  dialog: () => HTMLElement | undefined;
  initialFocus?: () => HTMLElement | undefined;
  onDismiss: () => void;
}

interface InertRecord {
  element: HTMLElement;
  wasInert: boolean;
}

const modalStack: symbol[] = [];

export function useModalFocus(options: ModalFocusOptions) {
  createEffect(() => {
    if (!options.open()) {
      return;
    }

    const token = Symbol("modal-focus");
    modalStack.push(token);
    const opener = document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null;
    const openerFocusKey = opener?.dataset.focusKey;
    const inertedOutside = inertOutsideBackdrop(options.backdrop());

    const handleKeyDown = (event: KeyboardEvent) => {
      if (!isTopModal(token)) {
        return;
      }

      const eventTarget = event.target instanceof HTMLElement ? event.target : null;
      if (eventTarget?.closest("[data-modal-focus-portal='true']")) {
        return;
      }

      if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        options.onDismiss();
        return;
      }

      if (event.key !== "Tab") {
        return;
      }

      const dialog = options.dialog();
      if (!dialog) {
        return;
      }

      const active = document.activeElement;
      const focusable = getFocusableDialogElements(dialog);
      if (focusable.length === 0) {
        event.preventDefault();
        dialog.focus({ preventScroll: true });
        return;
      }

      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (event.shiftKey && (active === first || !dialog.contains(active))) {
        event.preventDefault();
        last.focus({ preventScroll: true });
      } else if (!event.shiftKey && (active === last || !dialog.contains(active))) {
        event.preventDefault();
        first.focus({ preventScroll: true });
      }
    };

    document.addEventListener("keydown", handleKeyDown, true);
    queueMicrotask(() => {
      if (!isTopModal(token) || !options.open()) {
        return;
      }

      const dialog = options.dialog();
      (options.initialFocus?.() ?? (dialog ? getFocusableDialogElements(dialog)[0] : undefined) ?? dialog)
        ?.focus({ preventScroll: true });
    });

    onCleanup(() => {
      document.removeEventListener("keydown", handleKeyDown, true);
      removeModalToken(token);
      for (const { element, wasInert } of inertedOutside) {
        element.inert = wasInert;
      }

      restoreFocusTarget(opener, openerFocusKey);
    });
  });
}

export function getFocusableDialogElements(dialog: HTMLElement) {
  const candidates = dialog.querySelectorAll<HTMLElement>(
    "button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), "
      + "textarea:not([disabled]), [tabindex]:not([tabindex='-1'])");
  return Array.from(candidates).filter((element) =>
    !element.inert
    && !element.closest("[inert]")
    && element.getAttribute("aria-hidden") !== "true"
    && element.getClientRects().length > 0);
}

/**
 * 把对话框之外的整个页面设为 inert：从遮罩往上走到 body，每一层都把祖先链以外的兄弟元素设为 inert。
 * 对话框挂在应用根里时，这就是它旁边的应用内容；经 Portal 挂到 body 时，它旁边的应用根（#root）也在其中。
 * 记下每个元素原来的 inert，关闭时原样恢复（叠开的对话框各自恢复自己改过的那部分）。
 */
function inertOutsideBackdrop(backdrop: HTMLElement | undefined) {
  const records: InertRecord[] = [];
  let kept: HTMLElement | null = backdrop ?? null;
  // DOM 深度上限：正常页面不到 30 层；防的是异常结构下无止境地往上走。
  for (let depth = 0; kept && kept !== document.body && depth < 256; depth += 1) {
    const parent: HTMLElement | null = kept.parentElement;
    if (!parent) {
      break;
    }

    for (const child of parent.children) {
      if (child === kept || !(child instanceof HTMLElement)) {
        continue;
      }

      records.push({ element: child, wasInert: child.inert });
      child.inert = true;
    }
    kept = parent;
  }
  return records;
}

function isTopModal(token: symbol) {
  return modalStack[modalStack.length - 1] === token;
}

function removeModalToken(token: symbol) {
  const index = modalStack.lastIndexOf(token);
  if (index >= 0) {
    modalStack.splice(index, 1);
  }
}
