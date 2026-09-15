import { Show } from "solid-js";
import type { SettingsTextBundle } from "../../text.ts";
import type { AppSettings } from "../../types";
import { uiText } from "../../text.ts";

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
        <label class="settings-switch">
          <input
            type="checkbox"
            aria-label={props.text.debug.debugModeTitle}
            checked={debug().debugModeEnabled}
            onChange={(event) => props.onDebugModeChange(event.currentTarget.checked)}
          />
          <span />
        </label>
      </div>
      <Show when={debug().debugModeEnabled}>
        <div class="settings-row settings-debug-option">
          <div class="settings-row-copy">
            <strong>{props.text.debug.debugLogTitle}</strong>
            <span>{props.text.debug.debugLogDescription}</span>
          </div>
          <label class="settings-switch">
            <input
              type="checkbox"
              aria-label={props.text.debug.debugLogTitle}
              checked={debug().debugLogEnabled}
              onChange={(event) => props.onDebugLogChange(event.currentTarget.checked)}
            />
            <span />
          </label>
        </div>
        <Show when={debug().debugLogEnabled}>
          <div class="settings-row settings-debug-option">
            <div class="settings-row-copy">
              <strong>{props.text.debug.hostManagerSmartCoordinatorPerformanceLogTitle}</strong>
              <span>{props.text.debug.hostManagerSmartCoordinatorPerformanceLogDescription}</span>
            </div>
            <label class="settings-switch">
              <input
                type="checkbox"
                aria-label={props.text.debug.hostManagerSmartCoordinatorPerformanceLogTitle}
                checked={debug().hostManagerSmartCoordinatorPerformanceLogEnabled}
                disabled={!props.optimizationRuntimeAvailable}
                title={!props.optimizationRuntimeAvailable ? uiText.misc.optimizationRuntimeDisabled : undefined}
                onChange={(event) => props.onHostManagerSmartCoordinatorPerformanceLogChange(event.currentTarget.checked)}
              />
              <span />
            </label>
          </div>
        </Show>
        <div class="settings-row settings-debug-option">
          <div class="settings-row-copy">
            <strong>{props.text.debug.hostManagerSmartCoordinatorScoreOnlyTitle}</strong>
            <span>{props.text.debug.hostManagerSmartCoordinatorScoreOnlyDescription}</span>
          </div>
          <label class="settings-switch">
            <input
              type="checkbox"
              aria-label={props.text.debug.hostManagerSmartCoordinatorScoreOnlyTitle}
              checked={debug().hostManagerSmartCoordinatorScoreOnlyEnabled}
              disabled={!props.optimizationRuntimeAvailable}
              title={!props.optimizationRuntimeAvailable ? uiText.misc.optimizationRuntimeDisabled : undefined}
              onChange={(event) => props.onHostManagerSmartCoordinatorScoreOnlyChange(event.currentTarget.checked)}
            />
            <span />
          </label>
        </div>
      </Show>
    </div>
  );
}
