import { createSignal, For, Show } from "solid-js";
import type { AppCrashDumpMode, AppCrashDumpSettings, CrashDumpDiagnostics } from "../../../types";
import type { SettingsTextBundle } from "../../../i18n/settingsTypes.ts";
import { NumberField, SegmentedControl } from "./SettingsControls";
import { crashDumpModeView, crashDumpStatusView } from "../../crashReports/crashReportPresentation.ts";
import { HelpHint } from "../../../ui/primitives/HelpHint.tsx";

/**
 * Crash dump settings: mode, dump type and retention edit the settings draft and save with it.
 * Effective mode, its source and any mismatch come from backend diagnostics. When dumps are on by default,
 * an immediate "Turn off crash dumps" bypasses the draft. Records are listed below by CrashReportRecords.
 */
export function CrashDumpSettings(props: {
  settings: AppCrashDumpSettings;
  diagnostics: CrashDumpDiagnostics | undefined;
  text: SettingsTextBundle;
  onChange: (crashDumps: AppCrashDumpSettings) => void;
  onChooseMode: (mode: AppCrashDumpMode) => Promise<void>;
}) {
  const copy = () => props.text.crashDumps;
  const current = () => props.settings;
  const modeView = () => crashDumpModeView(props.diagnostics, copy());
  const statusView = () => crashDumpStatusView(props.diagnostics, copy());
  const modeOptions = () => (["default", "ask", "on", "off"] as const).map(id => ({ id, ...copy().modeOptions[id] }));
  const typeOptions = () => (["mini", "full"] as const).map(id => ({ id, ...copy().dumpTypeOptions[id] }));
  const [turningOff, setTurningOff] = createSignal(false);
  const [turnOffFailed, setTurnOffFailed] = createSignal(false);
  async function turnOff() {
    setTurningOff(true);
    setTurnOffFailed(false);
    try { await props.onChooseMode("off"); }
    catch { setTurnOffFailed(true); }
    finally { setTurningOff(false); }
  }

  return (
    <section class="crash-dump-settings" aria-labelledby="crashDumpSettingsTitle">
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong id="crashDumpSettingsTitle">{copy().title}</strong>
          <span>{copy().description}</span>
        </div>
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong>{copy().modeTitle}</strong>
            <HelpHint label={copy().modeTitle}>{copy().modeDescription}</HelpHint>
          </div>
          <Show when={modeView()}>{(view) => <span class="crash-dump-effective" role="status">{view().effective}</span>}</Show>
        </div>
        <SegmentedControl value={current().mode} options={modeOptions()} ariaLabel={copy().modeTitle}
          onChange={(mode) => props.onChange({ ...current(), mode })} />
      </div>
      <Show when={modeView()?.offerTurnOff}>
        <div class="settings-row">
          <div class="settings-row-copy">
            <span>{copy().turnOffDescription}</span>
            <Show when={turnOffFailed()}><span role="alert">{copy().requestFailed}</span></Show>
          </div>
          <button type="button" class="secondary" disabled={turningOff()} onClick={() => void turnOff()}>{copy().turnOff}</button>
        </div>
      </Show>
      <div class="settings-row">
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong>{copy().dumpTypeTitle}</strong>
            <HelpHint label={copy().dumpTypeTitle}>{copy().dumpTypeDescription}</HelpHint>
          </div>
        </div>
        <SegmentedControl value={current().dumpType} options={typeOptions()} ariaLabel={copy().dumpTypeTitle}
          onChange={(dumpType) => props.onChange({ ...current(), dumpType })} />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <span>{copy().keepCountDescription}</span>
        </div>
        <NumberField label={copy().keepCountTitle} value={current().keepCount} min={1}
          max={props.diagnostics?.keepCountMaximum ?? current().keepCount} step={1}
          disabled={props.diagnostics === undefined}
          onChange={(keepCount) => props.onChange({ ...current(), keepCount })} />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy" role="status">
          <strong>{copy().statusTitle}</strong>
          <span>{statusView().status}</span>
          <For each={statusView().reasons}>{(reason) => <span>{reason}</span>}</For>
          <Show when={props.diagnostics}>{(diagnostics) =>
            <span><span class="crash-report-file-label">{copy().dumpFolderLabel}</span> <code class="crash-report-code">{diagnostics().dumpFolder}</code></span>}</Show>
          <For each={statusView().images}>{(image) =>
            <span><code class="crash-report-code">{image.image}</code> {image.state}{image.detail ? ` · ${image.detail}` : ""}</span>}</For>
        </div>
      </div>
    </section>
  );
}
