import { For, Show } from "solid-js";
import {
  DialogActions,
  DialogBody,
  DialogHeader,
  DialogRoot
} from "../primitives/Dialog.tsx";

export type FeedbackTone = "success" | "error" | "warning" | "info";

export interface ConfirmDialogRequest {
  title: string;
  message?: string;
  details?: string[];
  tone?: FeedbackTone;
  confirmLabel?: string;
  cancelLabel?: string;
  dedupeKey?: string;
}

export interface ConfirmDialogRecord extends ConfirmDialogRequest {
  id: string;
  dedupeKey: string;
}

export function ConfirmDialog(props: {
  request: ConfirmDialogRecord | null;
  confirmLabel: string;
  cancelLabel: string;
  closeLabel: string;
  onResolve: (confirmed: boolean) => void;
}) {
  let cancelButton: HTMLButtonElement | undefined;

  return (
    <Show keyed when={props.request}>
      {(request) => (
        <DialogRoot
          open
          labelledBy="feedbackConfirmTitle"
          describedBy="feedbackConfirmDescription"
          backdropClass="feedback-backdrop"
          class={`feedback-modal ${request.tone ?? "info"}`}
          dismissOnBackdrop={false}
          initialFocus={() => cancelButton}
          onDismiss={() => props.onResolve(false)}
        >
          <DialogHeader
            title={request.title}
            titleId="feedbackConfirmTitle"
            closeLabel={props.closeLabel}
            onDismiss={() => props.onResolve(false)}
          />
          <DialogBody class="feedback-body">
            <div id="feedbackConfirmDescription" class="feedback-description">
              <Show when={request.message}>
                {(message) => <p>{message()}</p>}
              </Show>
              <Show when={(request.details?.length ?? 0) > 0}>
                <ul class="feedback-details">
                  <For each={request.details ?? []}>
                    {(detail) => <li>{detail}</li>}
                  </For>
                </ul>
              </Show>
            </div>
          </DialogBody>
          <DialogActions>
            <button
              ref={(element) => { cancelButton = element; }}
              class="secondary"
              type="button"
              onClick={() => props.onResolve(false)}
            >
              {request.cancelLabel ?? props.cancelLabel}
            </button>
            <button type="button" onClick={() => props.onResolve(true)}>
              {request.confirmLabel ?? props.confirmLabel}
            </button>
          </DialogActions>
        </DialogRoot>
      )}
    </Show>
  );
}
