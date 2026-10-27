import { ServiceRecoverySetting } from "./ServiceRecoverySetting";
import { FrontendStartupSetting } from "./FrontendStartupSetting";
import {
  defaultForceTerminateHotkey,
  forceTerminateHotkeyActionId
} from "../editableHotkeys";
import type { SettingsTextBundle } from "../../../text.ts";
import type {
  AppEditableHotkeySettings,
  AppSystemIntegrationSettings
} from "../../../types";
import { EditableHotkeyEditor } from "./EditableHotkeyEditor";
import { SwitchControl } from "../../../ui/primitives/Switch.tsx";

interface SystemIntegrationSettingsSectionProps {
  settings: AppSystemIntegrationSettings | undefined;
  text: SettingsTextBundle;
  onTaskManagerShortcutReplacementChange: (enabled: boolean) => void;
  onAutoStartChange: (enabled: boolean) => void;
  onServiceRecoveryChange: (enabled: boolean, delay: number, count: number) => void;
  serviceRecovery?: import("../../../types").ServiceRecoveryDiagnostics;
  onEditableHotkeyChange: (hotkey: AppEditableHotkeySettings) => void;
}

/** 设置 → 系统集成：崩溃后重启、登录后启动到托盘、开机自启、替换任务管理器和强制结束快捷键。 */
export function SystemIntegrationSettingsSection(props: SystemIntegrationSettingsSectionProps) {
  const systemIntegration = (): AppSystemIntegrationSettings => props.settings ?? {
    autoStartEnabled: false,
    taskManagerShortcutReplacementEnabled: false,
    hotkeys: [defaultForceTerminateHotkey()]
  };
  const forceTerminateHotkey = () => systemIntegration().hotkeys?.find(
    (hotkey) => hotkey.actionId === forceTerminateHotkeyActionId
  ) ?? defaultForceTerminateHotkey();

  return (
    <div class="settings-section-panel">
      <ServiceRecoverySetting settings={systemIntegration()} diagnostics={props.serviceRecovery}
        text={props.text.systemIntegration} onChange={props.onServiceRecoveryChange} />
      <FrontendStartupSetting text={props.text.systemIntegration} />
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.systemIntegration.autoStartTitle}</strong>
          <span>{props.text.systemIntegration.autoStartDescription}</span>
        </div>
        <SwitchControl ariaLabel={props.text.systemIntegration.autoStartTitle} checked={systemIntegration().autoStartEnabled} onChange={(checked) => props.onAutoStartChange(checked)} />
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.systemIntegration.taskManagerTitle}</strong>
          <span>{props.text.systemIntegration.taskManagerDescription}</span>
        </div>
        <SwitchControl ariaLabel={props.text.systemIntegration.taskManagerTitle} checked={systemIntegration().taskManagerShortcutReplacementEnabled} onChange={(checked) => props.onTaskManagerShortcutReplacementChange(checked)} />
      </div>
      <EditableHotkeyEditor
        hotkey={forceTerminateHotkey()}
        labels={props.text.systemIntegration}
        onChange={props.onEditableHotkeyChange}
      />
    </div>
  );
}
