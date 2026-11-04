import { createSignal, For, Show } from "solid-js";
import { DialogBody, DialogRoot } from "../../ui/primitives/Dialog";
import { uiText } from "../../text.ts";

/** Separate warranty and hardware-risk notices must be acknowledged before first use; elevated-mode risks are explained when selected. */
export function ControlFirstRunNotice(props: { onAcknowledge: () => void; busy?: boolean; failed?: boolean }) {
  const text = uiText.control.notice;
  const [step, setStep] = createSignal<"warranty" | "risk">("warranty");

  return (
    <DialogRoot
      open
      labelledBy="control-first-run-notice-title"
      class="control-first-run-notice"
      // Require both notices to be completed before entering the page.
      dismissOnBackdrop={false}
      onDismiss={() => undefined}
    >
      <Show
        when={step() === "warranty"}
        fallback={
          <>
            <DialogBody class="control-first-run-notice-body">
              <h2 id="control-first-run-notice-title">{text.riskTitle}</h2>
              <p class="control-first-run-notice-disclaimer">
                {uiText.control.accessLevel.disclaimer.normal}
              </p>
            </DialogBody>
            <div class="modal-actions">
              <Show when={props.failed}><p role="alert">{uiText.control.recovery.noticeAckFailed}</p></Show>
              {/* Move focus to the second screen's button when the first screen unmounts. */}
              <button type="button" disabled={props.busy} onClick={props.onAcknowledge}
                ref={(element) => queueMicrotask(() => element.focus({ preventScroll: true }))}>{text.accept}</button>
            </div>
          </>
        }
      >
        <DialogBody class="control-first-run-notice-body">
          <h2 id="control-first-run-notice-title">{text.warrantyTitle}</h2>
          <For each={text.warrantyBody}>
            {(line) => <p>{line}</p>}
          </For>
        </DialogBody>
        <div class="modal-actions">
          <button type="button" onClick={() => setStep("risk")}>{text.next}</button>
        </div>
      </Show>
    </DialogRoot>
  );
}
