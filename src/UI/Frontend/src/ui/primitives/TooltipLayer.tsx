import { createEffect, createMemo, createSignal, onCleanup, Show } from "solid-js";
import { AnchoredLayer } from "./AnchoredLayer.tsx";
import { HOVER_TOOLTIP_SPACING } from "./anchoredPlacement.ts";

/** Empty attribute enabling a tooltip from the element aria-label. */
export const NAME_TOOLTIP_ATTRIBUTE = "data-name-tooltip";
/** Attribute whose value supplies the full-content tooltip. */
export const FULL_CONTENT_ATTRIBUTE = "data-full-content";

const ANCHOR_SELECTOR = `[${NAME_TOOLTIP_ATTRIBUTE}], [${FULL_CONTENT_ATTRIBUTE}]`;
const SOURCE_ATTRIBUTES = [NAME_TOOLTIP_ATTRIBUTE, FULL_CONTENT_ATTRIBUTE, "aria-label"];

function anchorOf(target: EventTarget | null): HTMLElement | null {
  return target instanceof Element ? target.closest<HTMLElement>(ANCHOR_SELECTOR) : null;
}

function tooltipText(anchor: HTMLElement): string {
  const text = anchor.hasAttribute(FULL_CONTENT_ATTRIBUTE)
    ? anchor.getAttribute(FULL_CONTENT_ATTRIBUTE)
    : anchor.getAttribute("aria-label");
  return text?.trim() ?? "";
}

/** One application tooltip follows hover or keyboard-visible focus, with pointer priority. Esc or press suppresses the current source until it changes. Read current attributes, close on removal, and keep the layer noninteractive and hidden from assistive technology. */
export function TooltipLayer() {
  const [hovered, setHovered] = createSignal<HTMLElement | null>(null);
  const [focused, setFocused] = createSignal<HTMLElement | null>(null);
  const [dismissed, setDismissed] = createSignal<HTMLElement | null>(null);
  const [revision, setRevision] = createSignal(0);
  const anchor = createMemo(() => {
    const current = hovered() ?? focused();
    return current && current !== dismissed() ? current : null;
  });
  const text = createMemo(() => {
    revision();
    const current = anchor();
    return current ? tooltipText(current) : "";
  });

  // Changing or clearing the source releases suppression from Esc or pointer press.
  const hover = (next: HTMLElement | null) => {
    if (next === hovered()) return;
    if (next !== dismissed()) setDismissed(null);
    setHovered(next);
  };
  const focus = (next: HTMLElement | null) => {
    if (next === focused()) return;
    if (next !== dismissed()) setDismissed(null);
    setFocused(next);
  };
  const drop = (element: HTMLElement | null) => {
    if (!element) return;
    if (hovered() === element) hover(null);
    if (focused() === element) focus(null);
  };
  const dismiss = () => {
    const current = anchor();
    if (current) setDismissed(current);
  };

  const onPointerOver = (event: PointerEvent) => {
    if (event.pointerType !== "touch") hover(anchorOf(event.target));
  };
  const onPointerOut = (event: PointerEvent) => {
    if (event.pointerType !== "touch" && event.relatedTarget === null) hover(null);
  };
  const onFocusIn = (event: FocusEvent) => {
    const target = event.target;
    focus(target instanceof Element && target.matches(":focus-visible") ? anchorOf(target) : null);
  };
  const onFocusOut = () => focus(null);
  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === "Escape") dismiss();
  };
  document.addEventListener("pointerover", onPointerOver, true);
  document.addEventListener("pointerout", onPointerOut, true);
  document.addEventListener("pointerdown", dismiss, true);
  document.addEventListener("focusin", onFocusIn, true);
  document.addEventListener("focusout", onFocusOut, true);
  document.addEventListener("keydown", onKeyDown, true);
  onCleanup(() => {
    document.removeEventListener("pointerover", onPointerOver, true);
    document.removeEventListener("pointerout", onPointerOut, true);
    document.removeEventListener("pointerdown", dismiss, true);
    document.removeEventListener("focusin", onFocusIn, true);
    document.removeEventListener("focusout", onFocusOut, true);
    document.removeEventListener("keydown", onKeyDown, true);
  });

  // Observe only the active source; refresh changed text or close when its declaration disappears.
  createEffect(() => {
    const current = anchor();
    if (!current) return;
    const observer = new MutationObserver(() => {
      if (current.matches(ANCHOR_SELECTOR)) setRevision((value) => value + 1);
      else drop(current);
    });
    observer.observe(current, { attributes: true, attributeFilter: SOURCE_ATTRIBUTES });
    onCleanup(() => observer.disconnect());
  });

  return (
    <Show when={text() !== ""}>
      <AnchoredLayer
        anchor={anchor}
        spacing={HOVER_TOOLTIP_SPACING}
        class="hover-tooltip"
        aria-hidden="true"
        onAnchorLost={() => drop(anchor())}
      >
        {text()}
      </AnchoredLayer>
    </Show>
  );
}
