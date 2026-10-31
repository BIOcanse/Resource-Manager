import { Show } from "solid-js";
import { ServiceRecoverySetting } from "./ServiceRecoverySetting";
import { CrashDumpSettings } from "./CrashDumpSettings";
import { CrashDumpRequestCard } from "../../crashReports/CrashDumpRequestCard.tsx";
import { FrontendStartupSetting } from "./FrontendStartupSetting";
import { useFrontendVisibilityDemand } from "../../../frontendWork/useFrontendVisibilityDemand";
import { frontendVisibilitySurface } from "../../../frontendWork/frontendVisibilitySurface";
import { frontendWorkIds } from "../../../frontendWork/frontendWorkIds";
import {
  defaultForceTerminateHotkey,
  forceTerminateHotkeyActionId
} from "../editableHotkeys";
import type { SettingsTextBundle } from "../../../text.ts";
import type {
  AppCrashDumpMode,
  AppCrashDumpSettings,
  AppEditableHotkeySettings,
  AppSystemIntegrationSettings,
  CrashDumpDiagnostics
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
  crashDumps?: CrashDumpDiagnostics;
  onCrashDumpsChange: (crashDumps: AppCrashDumpSettings) => void;
  onChooseCrashDumpMode: (mode: AppCrashDumpMode) => Promise<void>;
  onEditableHotkeyChange: (hotkey: AppEditableHotkeySettings) => void;
}

/**
 * 设置 → 系统集成：崩溃转储的请求卡片（尚未决定时）、崩溃后重启、崩溃转储和崩溃记录、登录后启动到托盘、开机自启、
 * 替换任务管理器和强制结束快捷键。
 */
export function SystemIntegrationSettingsSection(props: SystemIntegrationSettingsSectionProps) {
  const systemIntegration = (): AppSystemIntegrationSettings => props.settings ?? {
    autoStartEnabled: false,
    taskManagerShortcutReplacementEnabled: false,
    hotkeys: [defaultForceTerminateHotkey()]
  };
  const forceTerminateHotkey = () => systemIntegration().hotkeys?.find(
    (hotkey) => hotkey.actionId === forceTerminateHotkeyActionId
  ) ?? defaultForceTerminateHotkey();
  // 这个分区的可见区域：登录启动项按它读取（常驻页隐藏时退掉，回来时重读）。
  const workId = frontendWorkIds.settingsSystemIntegration;
  useFrontendVisibilityDemand(workId, [workId]);

  return (
    <div class="settings-section-panel" {...frontendVisibilitySurface(workId, [workId])}>
      <CrashDumpRequestCard diagnostics={props.crashDumps} onChoose={props.onChooseCrashDumpMode} />
      <ServiceRecoverySetting settings={systemIntegration()} diagnostics={props.serviceRecovery}
        text={props.text.systemIntegration} onChange={props.onServiceRecoveryChange} />
      <Show when={systemIntegration().crashDumps}>{(settings) =>
        <CrashDumpSettings settings={settings()} diagnostics={props.crashDumps} text={props.text}
          onChange={props.onCrashDumpsChange} onChooseMode={props.onChooseCrashDumpMode} />}</Show>
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
