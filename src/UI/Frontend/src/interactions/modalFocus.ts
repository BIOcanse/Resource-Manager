import { createEffect, onCleanup, type Accessor } from "solid-js";

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
    const inertedSiblings = inertBackdropSiblings(options.backdrop());

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
      for (const { element, wasInert } of inertedSiblings) {
        element.inert = wasInert;
      }

      const focusTarget = resolveFocusReturnTarget(opener, openerFocusKey);
      if (focusTarget && !focusTarget.inert && !focusTarget.closest("[inert]")) {
        focusTarget.focus({ preventScroll: true });
      }
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

function inertBackdropSiblings(backdrop: HTMLElement | undefined) {
  const records: InertRecord[] = [];
  const parent = backdrop?.parentElement;
  if (!parent || !backdrop) {
    return records;
  }

  for (const child of parent.children) {
    if (child === backdrop || !(child instanceof HTMLElement)) {
      continue;
    }

    records.push({ element: child, wasInert: child.inert });
    child.inert = true;
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

function resolveFocusReturnTarget(opener: HTMLElement | null, focusKey: string | undefined) {
  if (opener?.isConnected && (!focusKey || opener.dataset.focusKey === focusKey)) {
    return opener;
  }

  if (!focusKey) {
    return null;
  }

  return document.querySelector<HTMLElement>(`[data-focus-key="${CSS.escape(focusKey)}"]`);
}
