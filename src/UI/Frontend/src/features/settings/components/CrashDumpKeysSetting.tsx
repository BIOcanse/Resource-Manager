import { For, Show } from "solid-js";
import type { AppCrashDumpKeySettings, CrashDumpKeyDiagnostics } from "../../../types";
import { SwitchControl } from "../../../ui/primitives/Switch.tsx";
import { uiText } from "../../../text.ts";

/* Edit only the enabled draft field. The backend owns defaults, key combinations, snapshots, and effective system diagnostics. */
export function CrashDumpKeysSetting(props: {
  settings: AppCrashDumpKeySettings | undefined;
  diagnostics: CrashDumpKeyDiagnostics | null | undefined;
  onChange: (enabled: boolean) => void;
}) {
  const copy = () => uiText.crashDumpKeys;
  const reasonText = (code: string) => Object.hasOwn(copy().reasons, code)
    ? copy().reasons[code as keyof ReturnType<typeof copy>["reasons"]] : code;
  return (
    <div class="settings-row settings-row-block crash-dump-keys-setting" data-crash-dump-keys>
      <div class="settings-row-copy">
        <strong>{copy().title}</strong>
        <span>{copy().description}</span>
        <ul class="crash-dump-keys-details">
          <For each={copy().details}>{(line) => <li>{line}</li>}</For>
        </ul>
        <Show when={props.settings} fallback={<span role="status">{copy().notProvided}</span>}>
          <Show when={props.diagnostics}>
            {(status) => (
              <div class="crash-dump-keys-status" role="status" data-state={status().state}>
                <strong>{copy().statusTitle}</strong>
                <span class="crash-dump-keys-state">{copy().states[status().state]}</span>
                <span>{status().keyCombination === "custom" ? copy().combinations.custom : copy().combinations.rightCtrlScrollLockTwice}</span>
                <Show when={status().kernelDumpConfigured === false}>
                  <span class="crash-dump-keys-warning">{copy().kernelDumpMissing}</span>
                </Show>
                <Show when={status().reason}>
                  {(reason) => <span class="crash-dump-keys-warning" data-full-content={reason()}>{copy().reason(reasonText(reason()))}</span>}
                </Show>
                <Show when={status().detail !== 0}>
                  <span>{copy().win32Detail(`0x${status().detail.toString(16).toUpperCase().padStart(8, "0")}`)}</span>
                </Show>
              </div>
            )}
          </Show>
        </Show>
      </div>
      <Show when={props.settings}>
        {(settings) => <SwitchControl ariaLabel={copy().title} checked={settings().enabled} onChange={props.onChange} />}
      </Show>
    </div>
  );
}
