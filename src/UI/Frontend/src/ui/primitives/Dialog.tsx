import { onMount, Show, type JSX } from "solid-js";
import { X } from "lucide-solid";
import { useModalFocus } from "../../interactions/modalFocus.ts";
import { enterNode, exitOnUnmount } from "../../presentation/nodeAnimation.ts";
import { uiText } from "../../text.ts";

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
      <DialogFrame {...props} backdropRef={(element) => { backdropElement = element; }} dialogRef={(element) => { dialogElement = element; }} />
    </Show>
  );
}

/**
 * 打开着的对话框：每次打开挂一次。交互上挂上即可用、卸下即消失（焦点、inert 由 useModalFocus 当场改）；
 * 画面由呈现层（presentation/nodeAnimation.ts）放：挂上时对话框和遮罩的不透明度由磁石带进来，卸下时遮罩连同对话框留一份幽灵播放退出槽位。
 */
function DialogFrame(props: DialogRootProps & {
  readonly backdropRef: (element: HTMLDivElement) => void;
  readonly dialogRef: (element: HTMLElement) => void;
}) {
  let backdropElement: HTMLDivElement | undefined;
  let dialogElement: HTMLElement | undefined;
  onMount(() => enterNode(dialogElement!, { part: "dialog", name: "弹窗进场", alsoFade: [backdropElement!] }));
  return (
    <div
      ref={(element) => { backdropElement = element; props.backdropRef(element); exitOnUnmount(element); }}
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
        ref={(element) => { dialogElement = element; props.dialogRef(element); }}
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
        aria-label={props.closeLabel ?? uiText.misc.close}
        title={props.closeLabel ?? uiText.misc.close}
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
