import { createEffect, type Accessor } from "solid-js";

interface InlineEditorFocusOptions {
  readonly active: Accessor<boolean>;
  readonly opener: () => HTMLElement | undefined;
  readonly editor: () => HTMLElement | undefined;
  readonly initialFocus?: () => HTMLElement | undefined;
}

export function useInlineEditorFocus(options: InlineEditorFocusOptions) {
  let wasActive = false;
  let opener: HTMLElement | null = null;
  let openerFocusKey: string | undefined;
  let transition = 0;

  createEffect(() => {
    const active = options.active();
    if (active === wasActive) {
      return;
    }

    wasActive = active;
    const currentTransition = ++transition;
    if (active) {
      opener = options.opener() ?? null;
      openerFocusKey = opener?.dataset.focusKey;
      queueMicrotask(() => {
        if (transition !== currentTransition || !options.active()) {
          return;
        }

        const editor = options.editor();
        (options.initialFocus?.() ?? firstInlineEditorControl(editor) ?? editor)
          ?.focus({ preventScroll: true });
      });
      return;
    }

    const focusTarget = resolveInlineEditorReturnTarget(opener, openerFocusKey);
    queueMicrotask(() => {
      if (transition !== currentTransition || options.active()) {
        return;
      }

      focusTarget?.focus({ preventScroll: true });
    });
  });
}

export function firstInlineEditorControl(editor: HTMLElement | undefined) {
  if (!editor) {
    return undefined;
  }

  const candidates = editor.querySelectorAll<HTMLElement>(
    "button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), "
      + "textarea:not([disabled]), [tabindex]:not([tabindex='-1'])");
  return Array.from(candidates).find((element) =>
    !element.inert
    && !element.closest("[inert], [hidden], [aria-hidden='true']"));
}

function resolveInlineEditorReturnTarget(
  opener: HTMLElement | null,
  focusKey: string | undefined
) {
  if (opener?.isConnected && (!focusKey || opener.dataset.focusKey === focusKey)) {
    return opener;
  }

  if (!focusKey) {
    return null;
  }

  return document.querySelector<HTMLElement>(`[data-focus-key="${CSS.escape(focusKey)}"]`);
}
