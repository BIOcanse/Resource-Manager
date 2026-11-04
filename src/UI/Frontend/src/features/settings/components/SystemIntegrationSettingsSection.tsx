import { Show } from "solid-js";
import { ServiceRecoverySetting } from "./ServiceRecoverySetting";
import { FrontendStartupSetting } from "./FrontendStartupSetting";
import { useFrontendVisibilityDemand } from "../../../frontendWork/useFrontendVisibilityDemand";
import { frontendVisibilitySurface } from "../../../frontendWork/frontendVisibilitySurface";
import { frontendWorkIds } from "../../../frontendWork/frontendWorkIds";
import {
  forceTerminateHotkeyActionId,
  performanceOverlayHotkeyActionId,
  type EditableHotkeyActionId
} from "../editableHotkeys";
import type { SettingsTextBundle } from "../../../text.ts";
import type {
  AppEditableHotkeySettings,
  AppSystemIntegrationSettings
} from "../../../types";
import { EditableHotkeyEditor } from "./EditableHotkeyEditor";
import { SwitchControl } from "../../../ui/primitives/Switch.tsx";
import { HelpHint } from "../../../ui/primitives/HelpHint.tsx";

interface SystemIntegrationSettingsSectionProps {
  settings: AppSystemIntegrationSettings | undefined;
  text: SettingsTextBundle;
  onTaskManagerShortcutReplacementChange: (enabled: boolean) => void;
  onAutoStartChange: (enabled: boolean) => void;
  onServiceRecoveryChange: (enabled: boolean, delay: number, count: number) => void;
  serviceRecovery?: import("../../../types").ServiceRecoveryDiagnostics;
  onEditableHotkeyChange: (hotkey: AppEditableHotkeySettings) => void;
}

/** Edit startup, recovery, task-manager replacement, and hotkey integration. */
export function SystemIntegrationSettingsSection(props: SystemIntegrationSettingsSectionProps) {
  const systemIntegration = (): AppSystemIntegrationSettings => props.settings!;
  const hotkey = (actionId: EditableHotkeyActionId) => systemIntegration().hotkeys?.find(
    (item) => item.actionId === actionId
  );
  // Visibility owns login-startup reads; release while hidden and reread on return.
  const workId = frontendWorkIds.settingsSystemIntegration;
  useFrontendVisibilityDemand(workId, [workId]);

  return (
    <div class="settings-section-panel" {...frontendVisibilitySurface(workId, [workId])}>
      <Show when={props.settings}>
      <ServiceRecoverySetting settings={systemIntegration()} diagnostics={props.serviceRecovery}
        text={props.text.systemIntegration} onChange={props.onServiceRecoveryChange} />
      <FrontendStartupSetting text={props.text.systemIntegration} />
      <div class="settings-row">
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong>{props.text.systemIntegration.autoStartTitle}</strong>
            <HelpHint label={props.text.systemIntegration.autoStartTitle}>{props.text.systemIntegration.autoStartDescription}</HelpHint>
          </div>
        </div>
        <SwitchControl ariaLabel={props.text.systemIntegration.autoStartTitle} checked={systemIntegration().autoStartEnabled} onChange={(checked) => props.onAutoStartChange(checked)} />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong>{props.text.systemIntegration.taskManagerTitle}</strong>
            <HelpHint label={props.text.systemIntegration.taskManagerTitle}>{props.text.systemIntegration.taskManagerDescription}</HelpHint>
          </div>
        </div>
        <SwitchControl ariaLabel={props.text.systemIntegration.taskManagerTitle} checked={systemIntegration().taskManagerShortcutReplacementEnabled} onChange={(checked) => props.onTaskManagerShortcutReplacementChange(checked)} />
      </div>
      <Show when={hotkey(forceTerminateHotkeyActionId)}>{(binding) => <EditableHotkeyEditor
        hotkey={binding()}
        title={props.text.systemIntegration.forceTerminateTitle}
        description={props.text.systemIntegration.forceTerminateDescription}
        warning={props.text.systemIntegration.forceTerminateWarning}
        labels={props.text.systemIntegration}
        onChange={props.onEditableHotkeyChange}
      />}</Show>
      <Show when={hotkey(performanceOverlayHotkeyActionId)}>{(binding) => <EditableHotkeyEditor
        hotkey={binding()}
        title={props.text.systemIntegration.performanceOverlayHotkeyTitle}
        description={props.text.systemIntegration.performanceOverlayHotkeyDescription}
        labels={props.text.systemIntegration}
        onChange={props.onEditableHotkeyChange}
      />}</Show>
      </Show>
    </div>
  );
}
