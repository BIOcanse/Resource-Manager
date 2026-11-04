import { Show } from "solid-js";
import type { SettingsTextBundle } from "../../../text.ts";
import type { AppSettings } from "../../../types";
import { uiText } from "../../../text.ts";
import { SwitchControl } from "../../../ui/primitives/Switch.tsx";
import { HelpHint } from "../../../ui/primitives/HelpHint.tsx";

interface DebugSettingsSectionProps {
  settings: AppSettings["debug"] | undefined;
  text: SettingsTextBundle;
  optimizationRuntimeAvailable: boolean;
  onDebugModeChange: (enabled: boolean) => void;
  onDebugLogChange: (enabled: boolean) => void;
  onHostManagerSmartCoordinatorScoreOnlyChange: (enabled: boolean) => void;
  onHostManagerSmartCoordinatorPerformanceLogChange: (enabled: boolean) => void;
}

export function DebugSettingsSection(props: DebugSettingsSectionProps) {
  const debug = () => props.settings ?? {
    debugModeEnabled: false,
    debugLogEnabled: false,
    hostManagerSmartCoordinatorScoreOnlyEnabled: false,
    hostManagerSmartCoordinatorPerformanceLogEnabled: false
  };

  return (
    <div class="settings-section-panel">
      <div class="settings-row">
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong>{props.text.debug.debugModeTitle}</strong>
            <HelpHint label={props.text.debug.debugModeTitle}>{props.text.debug.debugModeDescription}</HelpHint>
          </div>
        </div>
        <SwitchControl ariaLabel={props.text.debug.debugModeTitle} checked={debug().debugModeEnabled} onChange={(checked) => props.onDebugModeChange(checked)} />
      </div>
      <Show when={debug().debugModeEnabled}>
        <div class="settings-row settings-debug-option">
          <div class="settings-row-copy">
            <div class="settings-row-title">
              <strong>{props.text.debug.debugLogTitle}</strong>
              <HelpHint label={props.text.debug.debugLogTitle}>{props.text.debug.debugLogDescription}</HelpHint>
            </div>
          </div>
          <SwitchControl ariaLabel={props.text.debug.debugLogTitle} checked={debug().debugLogEnabled} onChange={(checked) => props.onDebugLogChange(checked)} />
        </div>
        <Show when={debug().debugLogEnabled}>
          <div class="settings-row settings-debug-option">
            <div class="settings-row-copy">
              <div class="settings-row-title">
                <strong>{props.text.debug.hostManagerSmartCoordinatorPerformanceLogTitle}</strong>
                <HelpHint label={props.text.debug.hostManagerSmartCoordinatorPerformanceLogTitle}>{props.text.debug.hostManagerSmartCoordinatorPerformanceLogDescription}</HelpHint>
              </div>
              <Show when={!props.optimizationRuntimeAvailable}><span role="note">{uiText.misc.optimizationRuntimeDisabled}</span></Show>
            </div>
            <SwitchControl ariaLabel={props.text.debug.hostManagerSmartCoordinatorPerformanceLogTitle} checked={debug().hostManagerSmartCoordinatorPerformanceLogEnabled} disabled={!props.optimizationRuntimeAvailable} onChange={(checked) => props.onHostManagerSmartCoordinatorPerformanceLogChange(checked)} />
          </div>
        </Show>
      </Show>
      {/* Keep analysis-only visible while enabled so it can be disabled independently of debug mode. */}
      <Show when={debug().debugModeEnabled || debug().hostManagerSmartCoordinatorScoreOnlyEnabled}>
        <div class="settings-row settings-debug-option">
          <div class="settings-row-copy">
            <div class="settings-row-title">
              <strong>{props.text.debug.hostManagerSmartCoordinatorScoreOnlyTitle}</strong>
              <HelpHint label={props.text.debug.hostManagerSmartCoordinatorScoreOnlyTitle}>{props.text.debug.hostManagerSmartCoordinatorScoreOnlyDescription}</HelpHint>
            </div>
            <Show when={!props.optimizationRuntimeAvailable}><span role="note">{uiText.misc.optimizationRuntimeDisabled}</span></Show>
          </div>
          <SwitchControl ariaLabel={props.text.debug.hostManagerSmartCoordinatorScoreOnlyTitle} checked={debug().hostManagerSmartCoordinatorScoreOnlyEnabled} disabled={!props.optimizationRuntimeAvailable} onChange={(checked) => props.onHostManagerSmartCoordinatorScoreOnlyChange(checked)} />
        </div>
      </Show>
    </div>
  );
}
