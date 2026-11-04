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
import { HelpHint } from "../../../ui/primitives/HelpHint.tsx";

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

/** Edit publicService and aiModelService settings and manage gateway keys. */
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
  // Visibility owns gateway key-list reads; release while hidden and reread on return.
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
          <div class="settings-row-title">
            <strong>{props.text.systemIntegration.publicServiceTitle}</strong>
            <HelpHint label={props.text.systemIntegration.publicServiceTitle}>{props.text.systemIntegration.publicServiceDescription}</HelpHint>
          </div>
          <code>http://127.0.0.1:9321/api/public/v1</code>
          <Show when={!props.publicServicesAvailable}><span role="note">{uiText.misc.publicServicesDisabled}</span></Show>
        </div>
        <SwitchControl ariaLabel={props.text.systemIntegration.publicServiceTitle} checked={publicService().enabled} disabled={!props.publicServicesAvailable} onChange={(checked) => props.onLocalPublicServiceChange(checked)} />
      </div>
      <div class="settings-row settings-debug-option">
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong>{props.text.systemIntegration.publicFileIndexTitle}</strong>
            <HelpHint label={props.text.systemIntegration.publicFileIndexTitle}>{props.text.systemIntegration.publicFileIndexDescription}</HelpHint>
          </div>
        </div>
        <SwitchControl ariaLabel={props.text.systemIntegration.publicFileIndexTitle} checked={publicService().fileIndexEnabled} disabled={!props.publicServicesAvailable || !publicService().enabled} onChange={(checked) => props.onPublicFileIndexChange(checked)} />
      </div>
      <div class="settings-row settings-debug-option">
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong>{props.text.systemIntegration.publicDatabaseServiceTitle}</strong>
            <HelpHint label={props.text.systemIntegration.publicDatabaseServiceTitle}>{props.text.systemIntegration.publicDatabaseServiceDescription}</HelpHint>
          </div>
        </div>
        <SwitchControl ariaLabel={props.text.systemIntegration.publicDatabaseServiceTitle} checked={publicService().databaseServiceEnabled} disabled={!props.publicServicesAvailable || !publicService().enabled} onChange={(checked) => props.onPublicDatabaseServiceChange(checked)} />
      </div>
      <div class="settings-row settings-debug-option">
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong>{props.text.systemIntegration.publicAiModelCatalogTitle}</strong>
            <HelpHint label={props.text.systemIntegration.publicAiModelCatalogTitle}>{props.text.systemIntegration.publicAiModelCatalogDescription}</HelpHint>
          </div>
        </div>
        <SwitchControl ariaLabel={props.text.systemIntegration.publicAiModelCatalogTitle} checked={publicService().aiModelCatalogEnabled} disabled={!props.publicServicesAvailable || !publicService().enabled} onChange={(checked) => props.onPublicAiModelServiceChange(checked)} />
      </div>
      <div class="settings-row settings-debug-option">
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong>{props.text.systemIntegration.lmStudioEndpointTitle}</strong>
            <HelpHint label={props.text.systemIntegration.lmStudioEndpointTitle}>{props.text.systemIntegration.lmStudioEndpointDescription}</HelpHint>
          </div>
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
          <div class="settings-row-title">
            <strong>{props.text.systemIntegration.lmStudioAutoStartTitle}</strong>
            <HelpHint label={props.text.systemIntegration.lmStudioAutoStartTitle}>{props.text.systemIntegration.lmStudioAutoStartDescription}</HelpHint>
          </div>
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
