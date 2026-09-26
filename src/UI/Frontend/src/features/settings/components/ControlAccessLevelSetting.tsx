import { createSignal, For, onMount, Show } from "solid-js";
import {
  getControlAccessLevel,
  setControlAccessLevel
} from "../../../control/controlApi.ts";
import { controlAccessLevels } from "../../../control/controlTypes.ts";
import type { ControlAccessLevel } from "../../../control/controlTypes.ts";
import { useFrontendRuntime } from "../../../frontendRuntime/FrontendRuntimeContext";
import { DialogRoot } from "../../../ui/primitives/Dialog";
import { RadioGroupItem, RadioGroupRoot } from "../../../ui/primitives/RadioGroup.tsx";
import { uiText } from "../../../text.ts";

/**
 * 调节权限档位：普通 / root。
 *
 * **这一项不走设置页的草稿-保存那一套，选完就生效。** 它存在后端控制面那边
 * （控制目录每次列可控对象都要按它判每一项），设置页只是它的一个入口。
 * 抄一份进应用设置就变成两个属主，迟早对不上。
 *
 * 往高处切要确认一次。往低处切不用问 —— 收回权限本来就是随时该能做的事，
 * 而且不会改动任何已有设定：超出新档位的项只是变成不可调，用户设过什么还留着。
 */
export function ControlAccessLevelSetting() {
  const runtime = useFrontendRuntime();
  const text = uiText.control.accessLevel;
  const [level, setLevel] = createSignal<ControlAccessLevel>("normal");
  const [pending, setPending] = createSignal<ControlAccessLevel | null>(null);
  const [failed, setFailed] = createSignal(false);

  onMount(() => {
    void getControlAccessLevel(runtime.requestClient)
      .then(setLevel)
      .catch(() => setFailed(true));
  });

  const commit = (next: ControlAccessLevel) => {
    setPending(null);
    void setControlAccessLevel(runtime.requestClient, next)
      .then((applied) => { setLevel(applied); setFailed(false); })
      .catch(() => setFailed(true));
  };

  const choose = (next: ControlAccessLevel) => {
    if (next === level()) {
      return;
    }
    // 往低处收不用问：不改动任何设定，只是把更危险的项重新锁上。
    if (controlAccessLevels.indexOf(next) < controlAccessLevels.indexOf(level())) {
      commit(next);
      return;
    }
    setPending(next);
  };

  return (
    <div class="settings-row settings-row-block control-access-level">
      <div class="settings-row-copy">
        <strong>{text.title}</strong>
        <span>{text.intro}</span>
      </div>
      <RadioGroupRoot
        value={level()}
        ariaLabel={text.title}
        orientation="vertical"
        class="control-access-level-choices"
        onChange={(next) => choose(next as ControlAccessLevel)}
      >
        <For each={controlAccessLevels}>
          {(entry) => (
            <RadioGroupItem value={entry} class="control-access-level-choice">
              <span class="control-access-level-choice-name">{text.name[entry]}</span>
              <span class="control-access-level-choice-summary">{text.summary[entry]}</span>
            </RadioGroupItem>
          )}
        </For>
      </RadioGroupRoot>
      <Show when={failed()}>
        <p class="control-access-level-failed">{text.saveFailed}</p>
      </Show>

      {/*
        往高处切之前把这一档意味着什么说清楚。
        说的是**这一档放开了什么**，不是吓唬人 —— 用户是特意来切的，
        他要的是知道自己接下来会看到哪些项。
      */}
      <DialogRoot
        open={pending() !== null}
        labelledBy="control-access-level-confirm-title"
        class="control-access-level-confirm"
        onDismiss={() => setPending(null)}
      >
        <Show when={pending()}>
          {(next) => (
            <>
              <h2 id="control-access-level-confirm-title">
                {text.confirmTitle(text.name[next()])}
              </h2>
              <p>{text.summary[next()]}</p>
              <For each={text.consequences[next()]}>
                {(line) => <p class="settings-hint">{line}</p>}
              </For>
              {/* 这一档的免责声明。原话，不改写。 */}
              <p class="control-access-level-disclaimer">{text.disclaimer[next()]}</p>
              <div class="modal-actions">
                <button class="secondary" type="button" onClick={() => setPending(null)}>
                  {text.cancel}
                </button>
                <button type="button" onClick={() => commit(next())}>
                  {text.confirmAction}
                </button>
              </div>
            </>
          )}
        </Show>
      </DialogRoot>
    </div>
  );
}
