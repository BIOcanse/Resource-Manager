import { Show } from "solid-js";
import type { AppSystemIntegrationSettings, ServiceRecoveryDiagnostics } from "../../../types";
import type { SettingsTextBundle } from "../../../text.ts";
import { NumberField } from "./SettingsControls";
import { recoveryPresentation } from "../serviceRecoveryPresentation.ts";
import { SwitchControl } from "../../../ui/primitives/Switch.tsx";

export function ServiceRecoverySetting(props: {
  settings: AppSystemIntegrationSettings;
  diagnostics: ServiceRecoveryDiagnostics | undefined;
  text: SettingsTextBundle["systemIntegration"];
  onChange: (enabled: boolean, delay: number, count: number) => void;
}) {
  const enabled = () => props.settings.restartAfterCrashEnabled !== false;
  const delay = () => props.settings.crashRestartDelaySeconds ?? 10;
  const count = () => props.settings.crashRestartCount ?? 3;
  const view = () => recoveryPresentation(props.diagnostics, props.text);
  return (
    <>
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.crashRecoveryTitle}</strong>
          <span>{props.text.crashRecoveryDescription}</span>
        </div>
        <SwitchControl ariaLabel={props.text.crashRecoveryTitle} checked={enabled()} onChange={(checked) => props.onChange(checked, delay(), count())} />
      </div>
      <div class="settings-row">
        <NumberField label={props.text.crashRestartDelayTitle} value={delay()} min={1} max={3600} step={1}
          disabled={!enabled()} onChange={(value) => props.onChange(enabled(), value, count())} />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <span>{view().resetDescription}</span>
        </div>
        <NumberField label={props.text.crashRestartCountTitle} value={count()} min={1} max={10} step={1}
          disabled={!enabled()} onChange={(value) => props.onChange(enabled(), delay(), value)} />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy" role="status">
          <strong>{props.text.crashRecoveryActualTitle}</strong>
          <span>{view().status}</span>
          <Show when={props.diagnostics?.writeError}><span>{props.text.crashRecoveryWriteFailed}</span></Show>
          <Show when={props.diagnostics?.readError}><span>{props.text.crashRecoveryReadFailed}</span></Show>
          <Show when={props.diagnostics?.actual}>
            <span>{props.text.crashRestartCountTitle}: {view().actualCount}</span>
            <span>{props.text.crashRestartDelayTitle}: {view().actualDelays}</span>
            <span>{props.text.crashCounterResetTitle}: {view().actualResetDays}</span>
            <Show when={props.diagnostics?.actual?.failureActionsOnNonCrashFailures}>
              <span>{props.text.crashRecoveryNonCrashWarning}</span>
            </Show>
          </Show>
        </div>
      </div>
    </>
  );
}
