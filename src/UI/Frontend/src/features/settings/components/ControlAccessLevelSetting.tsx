import { createSignal, For, Show } from "solid-js";
import {
  setControlAccessLevel
} from "../../control/controlApi.ts";
import { controlAccessLevels } from "../../control/controlTypes.ts";
import type { ControlAccessLevel } from "../../control/controlTypes.ts";
import { useFrontendRuntime } from "../../../frontendRuntime/FrontendRuntimeContext";
import { useFrontendWork } from "../../../frontendWork/FrontendWorkContext.tsx";
import { useSource } from "../../../frontendRuntime/source/useSource.ts";
import { frontendWorkIds } from "../../../frontendWork/frontendWorkIds";
import { sourceCanRender } from "../../../frontendRuntime/source/SourceSnapshot.ts";
import { acceptSourceCommand } from "../../../frontendRuntime/source/acceptSourceCommand.ts";
import { DialogRoot } from "../../../ui/primitives/Dialog";
import { RadioGroupItem, RadioGroupRoot } from "../../../ui/primitives/RadioGroup.tsx";
import { uiText } from "../../../text.ts";

/** The backend control plane owns access level and applies changes immediately. Confirm elevation; lowering only locks controls and preserves saved values. */
export function ControlAccessLevelSetting() {
  const runtime = useFrontendRuntime();
  const work = useFrontendWork();
  // Hardware-control visibility owns this read.
  const source = useSource(runtime.sources.control.accessLevel,
    () => ({ active: work.isNeeded(frontendWorkIds.settingsHardwareControl), refreshIntervalMs: null }));
  const text = uiText.control.accessLevel;
  const level = () => sourceCanRender(source.snapshot()) ? source.snapshot().data : null;
  const [pending, setPending] = createSignal<ControlAccessLevel | null>(null);
  const [failed, setFailed] = createSignal(false);

  const readState = () => sourceCanRender(source.snapshot()) ? "ready"
    : ["idle", "loading"].includes(source.snapshot().status) ? "loading" : "error";
  const [saving, setSaving] = createSignal(false);
  const read = () => { void source.refresh(); };

  const commit = (next: ControlAccessLevel) => {
    setPending(null);
    setSaving(true);
    void acceptSourceCommand(runtime.sources.control.accessLevel,
      () => setControlAccessLevel(runtime.requestClient, next), value => value)
      .then(() => setFailed(false))
      .catch(() => setFailed(true))
      .finally(() => setSaving(false));
  };

  const choose = (next: ControlAccessLevel) => {
    const current = level();
    if (readState() !== "ready" || current === null || saving()) return;
    if (next === current) {
      return;
    }
    // Lowering access needs no confirmation because it preserves settings.
    if (controlAccessLevels.indexOf(next) < controlAccessLevels.indexOf(current)) {
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
        value={level() ?? ""}
        disabled={readState() !== "ready" || saving()}
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
      <Show when={readState() !== "ready"}>
        <p role={readState() === "error" ? "alert" : "status"}>{readState() === "error"
          ? uiText.control.recovery.accessReadFailed : uiText.control.recovery.accessLoading}</p>
        <Show when={readState() === "error"}>
          <button type="button" class="secondary" onClick={read}>{uiText.control.retryRead}</button>
        </Show>
      </Show>
      <Show when={failed()}>
        <p class="control-access-level-failed">{text.saveFailed}</p>
      </Show>

      {/* Explain which controls the higher level unlocks before confirmation. */}
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
              {/* Display the access-level disclaimer unchanged. */}
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
