import { For, Show, createUniqueId, type JSX } from "solid-js";
import { uiText } from "../text.ts";
import type { UserDetailSection } from "../presentation/userDetails";
import {
  DialogActions,
  DialogBody,
  DialogHeader,
  DialogRoot
} from "../ui/primitives/Dialog.tsx";

export function UserDetailsDialog(props: {
  open: boolean;
  title: string;
  summary?: string;
  className?: string;
  sections?: UserDetailSection[];
  children?: JSX.Element;
  actions?: JSX.Element;
  onClose: () => void;
}) {
  const titleId = `user-details-title-${createUniqueId()}`;
  let closeButton: HTMLButtonElement | undefined;

  return (
    <DialogRoot
      open={props.open}
      labelledBy={titleId}
      backdropClass="user-details-backdrop"
      class={`user-details-modal ${props.className ?? ""}`.trim()}
      initialFocus={() => closeButton}
      onDismiss={props.onClose}
    >
      <DialogHeader
        title={props.title}
        titleId={titleId}
        closeButtonRef={(element) => { closeButton = element; }}
        onDismiss={props.onClose}
      />
      <DialogBody class="user-details-body">
        <Show when={props.summary}>
          {(summary) => <p class="user-details-summary">{summary()}</p>}
        </Show>
        <For each={props.sections ?? []}>
          {(section) => (
            <section class="user-details-section">
              <Show when={section.title}>{(title) => <h3>{title()}</h3>}</Show>
              <dl>
                <For each={section.items}>
                  {(item) => (
                    <div class="user-details-row">
                      <dt>{item.label}</dt>
                      <dd>{item.value}</dd>
                    </div>
                  )}
                </For>
              </dl>
            </section>
          )}
        </For>
        {props.children}
      </DialogBody>
      <DialogActions>
        {props.actions}
        <button class="secondary" type="button" onClick={props.onClose}>{uiText.misc.close}</button>
      </DialogActions>
    </DialogRoot>
  );
}
