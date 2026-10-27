import { Show } from "solid-js";
import type { SettingsTextBundle } from "../../../text.ts";
import type { AppSettings } from "../../../types";
import { uiText } from "../../../text.ts";
import { SwitchControl } from "../../../ui/primitives/Switch.tsx";

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
          <strong>{props.text.debug.debugModeTitle}</strong>
          <span>{props.text.debug.debugModeDescription}</span>
        </div>
        <SwitchControl ariaLabel={props.text.debug.debugModeTitle} checked={debug().debugModeEnabled} onChange={(checked) => props.onDebugModeChange(checked)} />
      </div>
      <Show when={debug().debugModeEnabled}>
        <div class="settings-row settings-debug-option">
          <div class="settings-row-copy">
            <strong>{props.text.debug.debugLogTitle}</strong>
            <span>{props.text.debug.debugLogDescription}</span>
          </div>
          <SwitchControl ariaLabel={props.text.debug.debugLogTitle} checked={debug().debugLogEnabled} onChange={(checked) => props.onDebugLogChange(checked)} />
        </div>
        <Show when={debug().debugLogEnabled}>
          <div class="settings-row settings-debug-option">
            <div class="settings-row-copy">
              <strong>{props.text.debug.hostManagerSmartCoordinatorPerformanceLogTitle}</strong>
              <span>{props.text.debug.hostManagerSmartCoordinatorPerformanceLogDescription}</span>
              <Show when={!props.optimizationRuntimeAvailable}><span role="note">{uiText.misc.optimizationRuntimeDisabled}</span></Show>
            </div>
            <SwitchControl ariaLabel={props.text.debug.hostManagerSmartCoordinatorPerformanceLogTitle} checked={debug().hostManagerSmartCoordinatorPerformanceLogEnabled} disabled={!props.optimizationRuntimeAvailable} onChange={(checked) => props.onHostManagerSmartCoordinatorPerformanceLogChange(checked)} />
          </div>
        </Show>
      </Show>
      {/* 仅分析不跟随调试模式关闭（见 optimization_runtime_corrections_2026-10-22.md），开着时这一行始终可见，才能看到并关掉它。 */}
      <Show when={debug().debugModeEnabled || debug().hostManagerSmartCoordinatorScoreOnlyEnabled}>
        <div class="settings-row settings-debug-option">
          <div class="settings-row-copy">
            <strong>{props.text.debug.hostManagerSmartCoordinatorScoreOnlyTitle}</strong>
            <span>{props.text.debug.hostManagerSmartCoordinatorScoreOnlyDescription}</span>
            <Show when={!props.optimizationRuntimeAvailable}><span role="note">{uiText.misc.optimizationRuntimeDisabled}</span></Show>
          </div>
          <SwitchControl ariaLabel={props.text.debug.hostManagerSmartCoordinatorScoreOnlyTitle} checked={debug().hostManagerSmartCoordinatorScoreOnlyEnabled} disabled={!props.optimizationRuntimeAvailable} onChange={(checked) => props.onHostManagerSmartCoordinatorScoreOnlyChange(checked)} />
        </div>
      </Show>
    </div>
  );
}
