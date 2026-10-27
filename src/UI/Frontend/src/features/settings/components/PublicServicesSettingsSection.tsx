import { Show } from "solid-js";
import { AiGatewayKeyManager } from "./AiGatewayKeyManager";
import { useFrontendVisibilityDemand } from "../../../frontendWork/useFrontendVisibilityDemand";
import { frontendVisibilitySurface } from "../../../frontendWork/frontendVisibilitySurface";
import { frontendWorkIds } from "../../../frontendWork/frontendWorkIds";
import type { SettingsTextBundle } from "../../../text.ts";
import type {
  AppAiModelServiceSettings,
  AppLocalPublicServiceSettings
} from "../../../types";
import { uiText } from "../../../text.ts";
import { SwitchControl } from "../../../ui/primitives/Switch.tsx";

interface PublicServicesSettingsSectionProps {
  publicServiceSettings: AppLocalPublicServiceSettings | undefined;
  aiModelServiceSettings: AppAiModelServiceSettings | undefined;
  text: SettingsTextBundle;
  publicServicesAvailable: boolean;
  onLocalPublicServiceChange: (enabled: boolean) => void;
  onPublicFileIndexChange: (enabled: boolean) => void;
  onPublicDatabaseServiceChange: (enabled: boolean) => void;
  onPublicAiModelServiceChange: (enabled: boolean) => void;
  onLmStudioEndpointChange: (endpoint: string) => void;
  onLmStudioAutoStartChange: (enabled: boolean) => void;
}

/** 设置 → 公共服务：本机公共接口、文件索引、SQLite、AI 模型目录、LM Studio 和 AI 网关密钥。值存在设置的 publicService 和 aiModelService 里。 */
export function PublicServicesSettingsSection(props: PublicServicesSettingsSectionProps) {
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
  // 这个分区的可见区域：AI 网关密钥列表按它读取（常驻页隐藏时退掉，回来时重读）。
  const workId = frontendWorkIds.settingsPublicServices;
  useFrontendVisibilityDemand(workId, [workId]);

  return (
    <div class="settings-section-panel" {...frontendVisibilitySurface(workId, [workId])}>
      <Show when={!props.publicServicesAvailable}>
        <div class="settings-capability-notice" role="status">
          {uiText.misc.publicServicesNotRunning}
        </div>
      </Show>
      <div class="settings-row">
        <div class="settings-row-copy">
          <strong>{props.text.systemIntegration.publicServiceTitle}</strong>
          <span>{props.text.systemIntegration.publicServiceDescription}</span>
          <code>http://127.0.0.1:9321/api/public/v1</code>
          <Show when={!props.publicServicesAvailable}><span role="note">{uiText.misc.publicServicesDisabled}</span></Show>
        </div>
        <SwitchControl ariaLabel={props.text.systemIntegration.publicServiceTitle} checked={publicService().enabled} disabled={!props.publicServicesAvailable} onChange={(checked) => props.onLocalPublicServiceChange(checked)} />
      </div>
      <div class="settings-row settings-debug-option">
        <div class="settings-row-copy">
          <strong>{props.text.systemIntegration.publicFileIndexTitle}</strong>
          <span>{props.text.systemIntegration.publicFileIndexDescription}</span>
        </div>
        <SwitchControl ariaLabel={props.text.systemIntegration.publicFileIndexTitle} checked={publicService().fileIndexEnabled} disabled={!props.publicServicesAvailable || !publicService().enabled} onChange={(checked) => props.onPublicFileIndexChange(checked)} />
      </div>
      <div class="settings-row settings-debug-option">
        <div class="settings-row-copy">
          <strong>{props.text.systemIntegration.publicDatabaseServiceTitle}</strong>
          <span>{props.text.systemIntegration.publicDatabaseServiceDescription}</span>
        </div>
        <SwitchControl ariaLabel={props.text.systemIntegration.publicDatabaseServiceTitle} checked={publicService().databaseServiceEnabled} disabled={!props.publicServicesAvailable || !publicService().enabled} onChange={(checked) => props.onPublicDatabaseServiceChange(checked)} />
      </div>
      <div class="settings-row settings-debug-option">
        <div class="settings-row-copy">
          <strong>{props.text.systemIntegration.publicAiModelCatalogTitle}</strong>
          <span>{props.text.systemIntegration.publicAiModelCatalogDescription}</span>
        </div>
        <SwitchControl ariaLabel={props.text.systemIntegration.publicAiModelCatalogTitle} checked={publicService().aiModelCatalogEnabled} disabled={!props.publicServicesAvailable || !publicService().enabled} onChange={(checked) => props.onPublicAiModelServiceChange(checked)} />
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
        <SwitchControl ariaLabel={props.text.systemIntegration.lmStudioAutoStartTitle} checked={aiModelService().autoStartEnabled} disabled={!props.publicServicesAvailable || !publicService().enabled || !publicService().aiModelCatalogEnabled} onChange={(checked) => props.onLmStudioAutoStartChange(checked)} />
      </div>
      <AiGatewayKeyManager
        enabled={props.publicServicesAvailable && publicService().enabled && publicService().aiModelCatalogEnabled}
        labels={props.text.systemIntegration}
      />
    </div>
  );
}
