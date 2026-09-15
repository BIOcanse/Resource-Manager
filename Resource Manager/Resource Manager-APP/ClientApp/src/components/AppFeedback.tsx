import { createSignal, For, Show } from "solid-js";
import { X } from "lucide-solid";
import { uiText } from "../text.ts";
import type { UserDetailSection } from "../presentation/userDetails";
import {
  ConfirmDialog,
  type ConfirmDialogRecord,
  type FeedbackTone
} from "../ui/patterns/ConfirmDialog.tsx";
import { UserDetailsDialog } from "./UserDetailsDialog";

export type {
  ConfirmDialogRecord,
  ConfirmDialogRequest
} from "../ui/patterns/ConfirmDialog.tsx";

export type ToastTone = FeedbackTone;

export interface ToastItem {
  id: string;
  tone: ToastTone;
  title: string;
  message?: string;
  details?: string[];
}

export type ToastInput = string | {
  tone?: ToastTone;
  title?: string;
  message?: string;
  details?: string[];
  durationMs?: number;
};

export function ConfirmDialogHost(props: {
  request: ConfirmDialogRecord | null;
  onResolve: (confirmed: boolean) => void;
}) {
  return (
    <ConfirmDialog
      request={props.request}
      confirmLabel={uiText.feedback.confirm}
      cancelLabel={uiText.feedback.cancel}
      closeLabel={uiText.feedback.close}
      onResolve={props.onResolve}
    />
  );
}

export function ToastHost(props: {
  items: ToastItem[];
  onDismiss: (id: string) => void;
}) {
  const [selectedDetails, setSelectedDetails] = createSignal<ToastItem | null>(null);

  return (
    <>
      <div class="toast-host" aria-live="polite" aria-atomic="false">
        <For each={props.items}>
          {(item) => (
            <article class={`toast ${item.tone}`}>
              <div class="toast-content">
                <strong>{item.title}</strong>
                <Show when={item.message}>
                  {(message) => <p>{message()}</p>}
                </Show>
                <Show when={(item.details?.length ?? 0) > 0}>
                  <div class="toast-actions">
                    <button
                      class="secondary details-button"
                      type="button"
                      onClick={() => {
                        setSelectedDetails(item);
                        props.onDismiss(item.id);
                      }}
                    >
                      详细信息
                    </button>
                  </div>
                </Show>
              </div>
              <button class="icon-button secondary" type="button" aria-label={uiText.feedback.close} onClick={() => props.onDismiss(item.id)}>
                <X aria-hidden="true" size={18} strokeWidth={2} />
              </button>
            </article>
          )}
        </For>
      </div>
      <UserDetailsDialog
        open={selectedDetails() !== null}
        title={uiText.misc.operationDetailTitle(selectedDetails()?.title ?? uiText.misc.fallbackOperation)}
        summary={selectedDetails()?.message}
        sections={toastDetailSections(selectedDetails())}
        onClose={() => setSelectedDetails(null)}
      />
    </>
  );
}

function toastDetailSections(item: ToastItem | null): UserDetailSection[] {
  const values = item?.details
    ?.flatMap((detail) => detail.split(/\r?\n/))
    .map((detail) => detail.trim())
    .filter(Boolean) ?? [];
  if (values.length === 0) {
    return [];
  }

  return [{
    title: uiText.misc.relatedInfo,
    items: values.map((value, index) => ({
      label: values.length === 1 ? uiText.misc.content : uiText.misc.infoIndex(index + 1),
      value
    }))
  }];
}
