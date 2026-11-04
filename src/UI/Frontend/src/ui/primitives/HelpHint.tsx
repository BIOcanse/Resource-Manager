import { createEffect, createSignal, createUniqueId, For, onCleanup, Show, type JSX } from "solid-js";
import { CircleHelp } from "lucide-solid";
import { uiText } from "../../text.ts";
import { AnchoredLayer } from "./AnchoredLayer.tsx";
import { HELP_HINT_SPACING } from "./anchoredPlacement.ts";
import { corridor, graceArea, insideArea, type Point } from "./pointerGrace.ts";

/** Help stays open while pointer or focus is inside the button, layer, or geometric pointer corridor. Esc clears both sources and restores button focus only when focus was inside. Drag selection stays within the layer; losing the anchor closes it. */
export interface HelpHintProps {
  /** Help content. */
  readonly children: JSX.Element;
  /** Accessible button name, defaulting to the localized help label. */
  readonly label?: string;
  /** Optional button icon for a marker that directly opens help; label supplies its accessible name. */
  readonly icon?: JSX.Element;
  readonly class?: string;
}

export interface HelpHintEntry {
  /** Option name shown in bold. */
  readonly label: string;
  /** Option explanation or unavailability reason. */
  readonly text: string;
}

/** Show option explanations in one adjacent help button; render nothing when no explanations exist. */
export function OptionsHelpHint(props: { readonly entries: readonly HelpHintEntry[]; readonly label?: string; readonly class?: string }) {
  return (
    <Show when={props.entries.length > 0}>
      <HelpHint label={props.label} class={props.class}>
        <For each={props.entries}>
          {(entry) => <p class="help-hint-entry"><strong>{entry.label}</strong>{entry.text}</p>}
        </For>
      </HelpHint>
    </Show>
  );
}

export function HelpHint(props: HelpHintProps) {
  const popoverId = `help-hint-${createUniqueId()}`;
  let button: HTMLButtonElement | undefined;
  let popover: HTMLDivElement | undefined;
  const [pointerInside, setPointerInside] = createSignal(false);
  const [focusInside, setFocusInside] = createSignal(false);
  const open = () => pointerInside() || focusInside();

  const withinHint = (target: EventTarget | null) =>
    target instanceof Node && Boolean(button?.contains(target) || popover?.contains(target));

  // Pointer transit area, or null when not traveling between elements.
  let grace: Point[] | null = null;
  const onGraceMove = (event: PointerEvent) => {
    if (grace && !insideArea(grace, { x: event.clientX, y: event.clientY })) leave();
  };
  const endGrace = () => {
    if (!grace) return;
    grace = null;
    document.removeEventListener("pointermove", onGraceMove, true);
  };
  const leave = () => {
    endGrace();
    setPointerInside(false);
  };
  const onPointerEnter = () => {
    endGrace();
    setPointerInside(true);
  };
  const pointerLeft = (self: () => HTMLElement | undefined, other: () => HTMLElement | undefined) => (event: PointerEvent) => {
    if (withinHint(event.relatedTarget)) return;
    const left = self();
    const target = other();
    // Window exit, touch release, or a missing counterpart ends hover immediately.
    if (event.pointerType === "touch" || event.relatedTarget === null || !left || !target?.isConnected) {
      leave();
      return;
    }
    const exit = { x: event.clientX, y: event.clientY };
    const targetBox = target.getBoundingClientRect();
    // A departure outside the corridor is already a leave, even without another pointer move.
    if (!insideArea(corridor(left.getBoundingClientRect(), targetBox), exit)) {
      leave();
      return;
    }
    endGrace();
    grace = graceArea(exit, targetBox);
    document.addEventListener("pointermove", onGraceMove, true);
  };
  const anchorLost = () => {
    leave();
    setFocusInside(false);
  };
  const onFocusOut = (event: FocusEvent) => {
    if (!withinHint(event.relatedTarget)) setFocusInside(false);
  };
  const dismiss = (event: KeyboardEvent) => {
    if (withinHint(document.activeElement)) {
      event.preventDefault();
      event.stopPropagation();
      // Restore focus before clearing sources so focusin cannot reopen the hint.
      button?.focus({ preventScroll: true });
    }
    leave();
    setFocusInside(false);
  };
  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === "Escape" && open()) dismiss(event);
  };

  // Constrain selection because a body portal could otherwise select intervening page content.
  // Before each frame, clamp an escaped selection endpoint to text under the pointer inside the content box.
  let dragFrame = 0;
  let dragPoint: Point | null = null;
  const confineSelection = () => {
    dragFrame = 0;
    const selection = document.getSelection();
    if (!dragPoint || !popover || !selection?.anchorNode || !selection.focusNode) return;
    if (!popover.contains(selection.anchorNode) || popover.contains(selection.focusNode)) return;
    const box = popover.getBoundingClientRect();
    const style = getComputedStyle(popover);
    const left = box.left + popover.clientLeft + parseFloat(style.paddingLeft);
    const top = box.top + popover.clientTop + parseFloat(style.paddingTop);
    const right = box.right - popover.clientLeft - parseFloat(style.paddingRight) - 1;
    const bottom = box.bottom - popover.clientTop - parseFloat(style.paddingBottom) - 1;
    const caret = document.caretPositionFromPoint(Math.min(right, Math.max(left, dragPoint.x)), Math.min(bottom, Math.max(top, dragPoint.y)));
    if (caret && popover.contains(caret.offsetNode)) {
      selection.setBaseAndExtent(selection.anchorNode, selection.anchorOffset, caret.offsetNode, caret.offset);
    }
  };
  const onDragMove = (event: PointerEvent) => {
    dragPoint = { x: event.clientX, y: event.clientY };
    if (dragFrame === 0) dragFrame = requestAnimationFrame(confineSelection);
  };
  const endDrag = () => {
    document.removeEventListener("pointermove", onDragMove, true);
    document.removeEventListener("pointerup", onDragEnd, true);
    document.removeEventListener("pointercancel", onDragEnd, true);
    if (dragFrame !== 0) cancelAnimationFrame(dragFrame);
    dragFrame = 0;
    dragPoint = null;
  };
  const onDragEnd = (event: PointerEvent) => {
    dragPoint = { x: event.clientX, y: event.clientY };
    confineSelection();
    endDrag();
  };
  const onPopoverPointerDown = (event: PointerEvent) => {
    if (event.button !== 0 || event.pointerType === "touch") return;
    endDrag();
    document.addEventListener("pointermove", onDragMove, true);
    document.addEventListener("pointerup", onDragEnd, true);
    document.addEventListener("pointercancel", onDragEnd, true);
  };

  // Pointer-only Esc dismissal preserves outside focus and lets the key propagate.
  // Attach the global listener only while the pointer source is active.
  const onDocumentKeyDown = (event: KeyboardEvent) => {
    if (event.key === "Escape" && pointerInside() && !focusInside()) dismiss(event);
  };
  createEffect(() => {
    if (!pointerInside()) return;
    document.addEventListener("keydown", onDocumentKeyDown);
    onCleanup(() => document.removeEventListener("keydown", onDocumentKeyDown));
  });
  onCleanup(() => {
    endGrace();
    endDrag();
  });

  return (
    <>
      <button
        ref={button}
        type="button"
        class={["help-hint", open() ? "open" : "", props.class ?? ""].filter(Boolean).join(" ")}
        aria-label={props.label ?? uiText.common.helpHint}
        aria-describedby={popoverId}
        onPointerEnter={onPointerEnter}
        onPointerLeave={pointerLeft(() => button, () => popover)}
        onFocusIn={() => setFocusInside(true)}
        onFocusOut={onFocusOut}
        onClick={() => setFocusInside(true)}
        onKeyDown={onKeyDown}
      >
        {props.icon ?? <CircleHelp size={14} aria-hidden="true" />}
      </button>
      <Show when={open()}>
        <HelpHintPopover
          id={popoverId}
          anchor={() => button}
          ref={(element) => { popover = element; }}
          onAnchorLost={anchorLost}
          onPointerEnter={onPointerEnter}
          onPointerLeave={pointerLeft(() => popover, () => button)}
          onPointerDown={onPopoverPointerDown}
          onFocusIn={() => setFocusInside(true)}
          onFocusOut={onFocusOut}
          onKeyDown={(event) => {
            if (event.key === "Tab") {
              // Return Tab to the button so focus resumes in page order despite the body portal.
              event.preventDefault();
              button?.focus({ preventScroll: true });
              return;
            }
            onKeyDown(event);
          }}
        >
          {props.children}
        </HelpHintPopover>
      </Show>
    </>
  );
}

function HelpHintPopover(props: {
  readonly id: string;
  readonly anchor: () => HTMLElement | undefined;
  readonly ref: (element: HTMLDivElement) => void;
  readonly onAnchorLost: () => void;
  readonly onPointerEnter: () => void;
  readonly onPointerLeave: (event: PointerEvent) => void;
  readonly onPointerDown: (event: PointerEvent) => void;
  readonly onFocusIn: () => void;
  readonly onFocusOut: (event: FocusEvent) => void;
  readonly onKeyDown: (event: KeyboardEvent) => void;
  readonly children: JSX.Element;
}) {
  // The anchored layer follows the button each frame, including adjacent layout changes.
  return (
    <AnchoredLayer
      anchor={props.anchor}
      spacing={HELP_HINT_SPACING}
      ref={props.ref}
      onAnchorLost={props.onAnchorLost}
      id={props.id}
      class="help-hint-popover"
      role="tooltip"
      tabIndex={-1}
      data-modal-focus-portal="true"
      onPointerEnter={props.onPointerEnter}
      onPointerLeave={props.onPointerLeave}
      onPointerDown={props.onPointerDown}
      onFocusIn={props.onFocusIn}
      onFocusOut={props.onFocusOut}
      onKeyDown={props.onKeyDown}
    >
      {props.children}
    </AnchoredLayer>
  );
}
