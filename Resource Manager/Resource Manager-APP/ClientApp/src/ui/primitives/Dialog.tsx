import { Show, type JSX } from "solid-js";
import { X } from "lucide-solid";
import { useModalFocus } from "../../interactions/modalFocus.ts";

export interface DialogRootProps {
  readonly open: boolean;
  readonly id?: string;
  readonly labelledBy: string;
  readonly describedBy?: string;
  readonly backdropClass?: string;
  readonly class?: string;
  readonly dismissOnBackdrop?: boolean;
  readonly initialFocus?: () => HTMLElement | undefined;
  readonly onDismiss: () => void;
  readonly children: JSX.Element;
}

export function DialogRoot(props: DialogRootProps) {
  let backdropElement: HTMLDivElement | undefined;
  let dialogElement: HTMLElement | undefined;
  useModalFocus({
    open: () => props.open,
    backdrop: () => backdropElement,
    dialog: () => dialogElement,
    initialFocus: props.initialFocus,
    onDismiss: props.onDismiss
  });

  return (
    <Show when={props.open}>
      <div
        ref={(element) => { backdropElement = element; }}
        class={`modal-backdrop ${props.backdropClass ?? ""}`.trim()}
        role="presentation"
        onClick={(event) => {
          if (props.dismissOnBackdrop !== false && event.target === event.currentTarget) {
            props.onDismiss();
          }
        }}
      >
        <section
          id={props.id}
          ref={(element) => { dialogElement = element; }}
          class={`modal ${props.class ?? ""}`.trim()}
          role="dialog"
          tabIndex={-1}
          aria-modal="true"
          aria-labelledby={props.labelledBy}
          aria-describedby={props.describedBy}
        >
          {props.children}
        </section>
      </div>
    </Show>
  );
}

export function DialogHeader(props: {
  readonly title: string;
  readonly titleId: string;
  readonly class?: string;
  readonly headingClass?: string;
  readonly closeLabel?: string;
  readonly onDismiss: () => void;
  readonly closeButtonRef?: (element: HTMLButtonElement) => void;
  readonly children?: JSX.Element;
}) {
  return (
    <header class={`modal-header ${props.class ?? ""}`.trim()}>
      <div class={`dialog-heading ${props.headingClass ?? ""}`.trim()}>
        <h2 id={props.titleId}>{props.title}</h2>
        {props.children}
      </div>
      <button
        ref={props.closeButtonRef}
        class="icon-button secondary"
        type="button"
        aria-label={props.closeLabel ?? "关闭"}
        title={props.closeLabel ?? "关闭"}
        onClick={props.onDismiss}
      >
        <X aria-hidden="true" size={18} strokeWidth={2} />
      </button>
    </header>
  );
}

export function DialogBody(props: {
  readonly class?: string;
  readonly children: JSX.Element;
}) {
  return (
    <div class={`dialog-body ${props.class ?? ""}`.trim()}>
      {props.children}
    </div>
  );
}

export function DialogActions(props: {
  readonly class?: string;
  readonly children: JSX.Element;
}) {
  return (
    <footer class={`modal-actions ${props.class ?? ""}`.trim()}>
      {props.children}
    </footer>
  );
}
