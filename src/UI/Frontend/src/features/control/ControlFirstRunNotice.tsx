import { createSignal, For, Show } from "solid-js";
import { DialogRoot } from "../../ui/primitives/Dialog";
import { uiText } from "../../text.ts";

/**
 * 第一次进控制页说的两屏：先说保修，再说风险。
 *
 * **两屏而不是一大段。** 它们回答的是两个不同的问题 ——「我会不会失去保修」
 * 和「我会不会把机器弄坏」。挤在一起，用户两个都记不住。
 *
 * 第二屏说明普通档风险：**默认这一档也是在动硬件配置**，
 * 所以话要在第一次就说完，而不是等用户去解锁更高一档才说。
 * Root 的风险在设置里切档位时单独说明。
 */
export function ControlFirstRunNotice(props: { onAcknowledge: () => void }) {
  const text = uiText.control.notice;
  const [step, setStep] = createSignal<"warranty" | "risk">("warranty");

  return (
    <DialogRoot
      open
      labelledBy="control-first-run-notice-title"
      class="control-first-run-notice"
      // 看完为止：这两屏是进这一页的前提，点旁边划走就等于没说过。
      dismissOnBackdrop={false}
      onDismiss={() => undefined}
    >
      <Show
        when={step() === "warranty"}
        fallback={
          <>
            <h2 id="control-first-run-notice-title">{text.riskTitle}</h2>
            <p class="control-first-run-notice-disclaimer">
              {uiText.control.accessLevel.disclaimer.normal}
            </p>
            <div class="modal-actions">
              <button type="button" onClick={props.onAcknowledge}>{text.accept}</button>
            </div>
          </>
        }
      >
        <h2 id="control-first-run-notice-title">{text.warrantyTitle}</h2>
        <For each={text.warrantyBody}>
          {(line) => <p>{line}</p>}
        </For>
        <div class="modal-actions">
          <button type="button" onClick={() => setStep("risk")}>{text.next}</button>
        </div>
      </Show>
    </DialogRoot>
  );
}
