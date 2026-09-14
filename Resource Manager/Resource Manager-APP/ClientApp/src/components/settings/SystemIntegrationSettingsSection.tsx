import { Show } from "solid-js";
import { AiGatewayKeyManager } from "../AiGatewayKeyManager";
import {
  defaultForceTerminateHotkey,
  forceTerminateHotkeyActionId
} from "../../settings/editableHotkeys";
import type { SettingsTextBundle } from "../../text";
import type {
  AppAiModelServiceSettings,
  AppEditableHotkeySettings,
  AppLocalPublicServiceSettings,
  AppSystemIntegrationSettings
} from "../../types";
import { EditableHotkeyEditor } from "./EditableHotkeyEditor";

interface SystemIntegrationSettingsSectionProps {
  settings: AppSystemIntegrationSettings | undefined;
  publicServiceSettings: AppLocalPublicServiceSettings | undefined;
  aiModelServiceSettings: AppAiModelServiceSettings | undefined;
  text: SettingsTextBundle;
  publicServicesAvailable: boolean;
  onTaskManagerShortcutReplacementChange: (enabled: boolean) => void;
  onAutoStartChange: (enabled: boolean) => void;
  onEditableHotkeyChange: (hotkey: AppEditableHotkeySettings) => void;
  onLocalPublicServiceChange: (enabled: boolean) => void;
  onPublicFileIndexChange: (enabled: boolean) => void;
  onPublicDatabaseServiceChange: (enabled: boolean) => void;
  onPublicAiModelServiceChange: (enabled: boolean) => void;
  onLmStudioEndpointChange: (endpoint: string) => void;
  onLmStudioAutoStartChange: (enabled: boolean) => void;
}

export function SystemIntegrationSettingsSection(props: SystemIntegrationSettingsSectionProps) {
  const systemIntegration = (): AppSystemIntegrationSettings => props.settings ?? {
    autoStartEnabled: false,
    taskManagerShortcutReplacementEnabled: false,
    hotkeys: [defaultForceTerminateHotkey()]
  };
  const forceTerminateHotkey = () => systemIntegration().hotkeys?.find(
    (hotkey) => hotkey.actionId === forceTerminateHotkeyActionId
  ) ?? defaultForceTerminateHotkey();
  const publicService = (): AppLocalPublicServiceSettings => props.publicServiceSettings ?? {
    enabled: false,
    fileIndexEnabled: false,
    databaseServiceEnabled: false,
    aiModelCatalogEnabled: false
  };
  const aiModelService = (): AppAiModelServiceSettings => props.aiModelServiceSettings ?? {
    provider: "lm-studio",
    endpoint: "http://127.0.0.1:1234",
    autoStartEnabled: false
  };

  return (
    <div class="settings-section-panel">
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.systemIntegration.autoStartTitle}</strong>
          <span>{props.text.systemIntegration.autoStartDescription}</span>
        </div>
        <label class="settings-switch">
          <input type="checkbox" aria-label={props.text.systemIntegration.autoStartTitle}
            checked={systemIntegration().autoStartEnabled}
            onChange={(event) => props.onAutoStartChange(event.currentTarget.checked)} />
          <span />
        </label>
      </div>
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.systemIntegration.taskManagerTitle}</strong>
          <span>{props.text.systemIntegration.taskManagerDescription}</span>
        </div>
        <label class="settings-switch">
          <input
            type="checkbox"
            aria-label={props.text.systemIntegration.taskManagerTitle}
            checked={systemIntegration().taskManagerShortcutReplacementEnabled}
            onChange={(event) => props.onTaskManagerShortcutReplacementChange(event.currentTarget.checked)}
          />
          <span />
        </label>
      </div>
      <EditableHotkeyEditor
        hotkey={forceTerminateHotkey()}
        labels={props.text.systemIntegration}
        onChange={props.onEditableHotkeyChange}
      />
      <Show when={!props.publicServicesAvailable}>
        <div class="settings-capability-notice" role="status">
          当前启动配置未运行公共服务。以下项目显示已保存配置，不表示对应服务正在运行。
        </div>
      </Show>
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.systemIntegration.publicServiceTitle}</strong>
          <span>{props.text.systemIntegration.publicServiceDescription}</span>
          <code>http://127.0.0.1:9321/api/public/v1</code>
        </div>
        <label class="settings-switch">
          <input
            type="checkbox"
            aria-label={props.text.systemIntegration.publicServiceTitle}
            checked={publicService().enabled}
            disabled={!props.publicServicesAvailable}
            title={!props.publicServicesAvailable ? "当前启动模式未启用公共服务" : undefined}
            onChange={(event) => props.onLocalPublicServiceChange(event.currentTarget.checked)}
          />
          <span />
        </label>
      </div>
      <div class="settings-row settings-debug-option">
        <div class="settings-row-copy">
          <strong>{props.text.systemIntegration.publicFileIndexTitle}</strong>
          <span>{props.text.systemIntegration.publicFileIndexDescription}</span>
        </div>
        <label class="settings-switch">
          <input
            type="checkbox"
            aria-label={props.text.systemIntegration.publicFileIndexTitle}
            checked={publicService().fileIndexEnabled}
            disabled={!props.publicServicesAvailable || !publicService().enabled}
            onChange={(event) => props.onPublicFileIndexChange(event.currentTarget.checked)}
          />
          <span />
        </label>
      </div>
      <div class="settings-row settings-debug-option">
        <div class="settings-row-copy">
          <strong>{props.text.systemIntegration.publicDatabaseServiceTitle}</strong>
          <span>{props.text.systemIntegration.publicDatabaseServiceDescription}</span>
        </div>
        <label class="settings-switch">
          <input
            type="checkbox"
            aria-label={props.text.systemIntegration.publicDatabaseServiceTitle}
            checked={publicService().databaseServiceEnabled}
            disabled={!props.publicServicesAvailable || !publicService().enabled}
            onChange={(event) => props.onPublicDatabaseServiceChange(event.currentTarget.checked)}
          />
          <span />
        </label>
      </div>
      <div class="settings-row settings-debug-option">
        <div class="settings-row-copy">
          <strong>{props.text.systemIntegration.publicAiModelCatalogTitle}</strong>
          <span>{props.text.systemIntegration.publicAiModelCatalogDescription}</span>
        </div>
        <label class="settings-switch">
          <input
            type="checkbox"
            aria-label={props.text.systemIntegration.publicAiModelCatalogTitle}
            checked={publicService().aiModelCatalogEnabled}
            disabled={!props.publicServicesAvailable || !publicService().enabled}
            onChange={(event) => props.onPublicAiModelServiceChange(event.currentTarget.checked)}
          />
          <span />
        </label>
      </div>
      <div class="settings-row settings-debug-option">
        <div class="settings-row-copy">
          <strong>{props.text.systemIntegration.lmStudioEndpointTitle}</strong>
          <span>{props.text.systemIntegration.lmStudioEndpointDescription}</span>
        </div>
        <input
          class="settings-text-field"
          type="url"
          aria-label={props.text.systemIntegration.lmStudioEndpointTitle}
          value={aiModelService().endpoint}
          disabled={!props.publicServicesAvailable || !publicService().enabled || !publicService().aiModelCatalogEnabled}
          onChange={(event) => props.onLmStudioEndpointChange(event.currentTarget.value)}
        />
      </div>
      <div class="settings-row settings-debug-option">
        <div class="settings-row-copy">
          <strong>{props.text.systemIntegration.lmStudioAutoStartTitle}</strong>
          <span>{props.text.systemIntegration.lmStudioAutoStartDescription}</span>
        </div>
        <label class="settings-switch">
          <input
            type="checkbox"
            aria-label={props.text.systemIntegration.lmStudioAutoStartTitle}
            checked={aiModelService().autoStartEnabled}
            disabled={!props.publicServicesAvailable || !publicService().enabled || !publicService().aiModelCatalogEnabled}
            onChange={(event) => props.onLmStudioAutoStartChange(event.currentTarget.checked)}
          />
          <span />
        </label>
      </div>
      <AiGatewayKeyManager
        enabled={props.publicServicesAvailable && publicService().enabled && publicService().aiModelCatalogEnabled}
        labels={props.text.systemIntegration}
      />
    </div>
  );
}
