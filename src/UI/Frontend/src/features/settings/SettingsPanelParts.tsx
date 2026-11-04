import { Show } from "solid-js";
import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { HelpHint } from "../../ui/primitives/HelpHint.tsx";
import { ReportRuleSettings } from "./components/ReportRuleSettings";
import { MemoryProtectionSettingsSection } from "./components/MemoryProtectionSettingsSection";
import { CpuMonopolySettingsSection } from "./components/CpuMonopolySettingsSection";
import { HardwareControlSettings } from "./components/HardwareControlSettings";
import { AppearanceSettingsSection } from "./components/AppearanceSettingsSection";
import { CreditsSettingsSection } from "./components/CreditsSettingsSection";
import { DebugSettingsSection } from "./components/DebugSettingsSection";
import { PerformanceOverlaySettingsSection } from "./components/PerformanceOverlaySettingsSection";
import { MemorySettingsSection } from "./components/MemorySettingsSection";
import { MemoryCpuTierSettingsSection } from "./components/MemoryCpuTierSettingsSection";
import { PublicServicesSettingsSection } from "./components/PublicServicesSettingsSection";
import { RefreshCadenceSetting } from "./components/RefreshCadenceSetting";
import { RuntimePolicySettingsSection } from "./components/RuntimePolicySettingsSection";
import { SystemIntegrationSettingsSection } from "./components/SystemIntegrationSettingsSection";
import { CrashDumpSettings } from "./components/CrashDumpSettings";
import { CrashDumpKeysSetting } from "./components/CrashDumpKeysSetting";
import { TrustedAccessManager } from "./components/TrustedAccessManager";
import { UpdatesSettingsSection } from "./components/UpdatesSettingsSection";
import { NotificationsSettingsSection } from "./components/NotificationsSettingsSection";
import { uiText, currentSettingsText } from "../../text.ts";
import type { SettingsPanel } from "../../types";
import type { SettingsPanelBindings } from "./settingsPanelBindings.ts";

/* Share section content, save status, and actions between the settings page and embedded panels. */

// Read-only panels do not edit the settings draft or offer save actions.
// Trusted-access controls apply their changes immediately.
export const readOnlySettingsPanels: ReadonlySet<SettingsPanel> = new Set(["performanceOverlay", "trustedAccess", "credits"]);
// Omit reset when defaults are owned exclusively by the backend.
const panelsWithoutLocalDefaults: ReadonlySet<SettingsPanel> = new Set(["cpuMonopolyProtection", "crashDumps", "crashDumpKeys"]);

/** Reuse the settings section label. */
export function settingsPanelLabel(panel: SettingsPanel): string {
  const text = currentSettingsText();
  if (panel === "crashDumps") return text.crashDumps.title;
  if (panel === "crashDumpKeys") return uiText.crashDumpKeys.title;
  return text.sections[panel] ?? panel;
}

/** Reset only this section; save the complete shared draft. */
export function SettingsPanelActions(props: { readonly panel: SettingsPanel; readonly bindings: SettingsPanelBindings }) {
  const text = () => currentSettingsText();
  const b = () => props.bindings;
  const statusText = () => {
    const state = b().saveState;
    if (state === "saving") return text().saveState.saving;
    if (state === "saved") return text().saveState.saved;
    if (state === "partial") return text().saveState.partial;
    if (state === "constrained") return text().saveState.constrained;
    if (state === "error") {
      // Show type rejections with their path even when they concern another section.
      if (b().runtimeFailureCode === "invalid-settings-value-type") {
        return b().runtimeFailurePath === null
          ? text().saveState.invalidValueType
          : `${text().saveState.invalidValueType} · ${b().runtimeFailurePath}`;
      }
      return text().saveState.error;
    }
    if (state === "conflict") return text().saveState.conflict;
    if (b().isDirty) return text().saveState.dirty;
    return "";
  };
  return (
    <Show when={b().loadState === "ready" && !readOnlySettingsPanels.has(props.panel)}>
      <span
        class="settings-save-state"
        classList={{
          error: b().saveState === "error" || b().saveState === "conflict",
          warning: b().saveState === "partial" || b().saveState === "constrained"
        }}
        role={b().saveState === "error" || b().saveState === "conflict" ? "alert" : "status"}
      >
        {statusText()}
      </span>
      {/* List each restricted setting in the status explanation. */}
      <Show when={b().saveState === "constrained" && b().runtimeCapabilityConstrainedPaths.length > 0}>
        <HelpHint>{b().runtimeCapabilityConstrainedPaths.join("\n")}</HelpHint>
      </Show>
      <Show when={b().saveState === "conflict"}>
        <button class="secondary" type="button" onClick={() => b().onReload()}>
          {text().actions.reload}
        </button>
      </Show>
      <Show when={b().saveState === "partial"}>
        <button class="secondary" type="button" onClick={() => b().onReapply()}>
          {text().actions.reapply}
        </button>
      </Show>
      <Show when={b().saveState !== "conflict"}>
        <button
          class="secondary"
          type="button"
          disabled={b().saveState === "saving" || b().isDirty}
          onClick={() => b().onReload()}
        >
          {text().actions.reload}
        </button>
        {/* Explain why reload is blocked by unsaved changes beside the disabled control. */}
        <Show when={b().isDirty}>
          <HelpHint>{uiText.misc.saveOrRevertFirst}</HelpHint>
        </Show>
      </Show>
      {/* Omit reset for sections with backend-only defaults. */}
      <Show when={!panelsWithoutLocalDefaults.has(props.panel)}>
        <button
          class="secondary"
          type="button"
          disabled={b().saveState === "saving" || b().saveState === "conflict"}
          onClick={() => b().onRestoreDefaults(props.panel)}
        >
          {text().actions.restoreDefaults}
        </button>
      </Show>
      <button
        type="button"
        disabled={b().saveState === "saving" || b().saveState === "conflict" || !b().isDirty}
        onClick={() => b().onSave()}
      >
        <StableLabel labels={{ idle: text().actions.save, busy: text().saveState.saving }} current={b().saveState === "saving" ? "busy" : "idle"} />
      </button>
    </Show>
  );
}

/** Render settings section content. */
export function SettingsPanelBody(props: { readonly panel: SettingsPanel; readonly bindings: SettingsPanelBindings }) {
  const text = () => currentSettingsText();
  const b = () => props.bindings;
  return (
    <>
      <Show when={props.panel === "runtimePolicy"}>
        <RuntimePolicySettingsSection
          settings={b().settings.performance}
          text={text()}
          preciseGpuPlacementAvailable={b().preciseGpuPlacementAvailable}
          onPreciseGpuPlacementChange={b().onPreciseGpuPlacementChange}
          onKeepSmartRecordHistoryChange={b().onKeepSmartRecordHistoryChange}
          onGpuOverflowThresholdsChange={b().onGpuOverflowThresholdsChange}
          onRealtimeActionBudgetChange={b().onRealtimeActionBudgetChange}
        />
      </Show>
      <Show when={props.panel === "memory"}>
        <MemorySettingsSection
          settings={b().settings.performance}
          text={text()}
          onAutomaticMemoryCleanupLinesChange={b().onAutomaticMemoryCleanupLinesChange}
          onMemoryOptimizationTargetLinesChange={b().onMemoryOptimizationTargetLinesChange}
        />
      </Show>
      <Show when={props.panel === "memoryCpuTier"}>
        <MemoryCpuTierSettingsSection
          settings={b().settings.memoryCpuTier}
          committed={b().committedMemoryCpuTier}
          onChange={b().onMemoryCpuTierChange}
        />
      </Show>
      <Show when={props.panel === "memoryProtection"}>
        <MemoryProtectionSettingsSection
          settings={b().settings.memoryProtection}
          committed={b().committedMemoryProtection}
          runtimeFailureCode={b().runtimeFailureCode}
          onChange={b().onMemoryProtectionChange}
          confirmDialog={b().confirmDialog}
        />
      </Show>
      <Show when={props.panel === "cpuMonopolyProtection"}>
        <CpuMonopolySettingsSection
          settings={b().settings.cpuMonopolyProtection}
          committed={b().committedCpuMonopolyProtection}
          runtimeFailureCode={b().runtimeFailureCode}
          onChange={b().onCpuMonopolyProtectionChange}
        />
      </Show>
      <Show when={props.panel === "hardwareControl"}>
        <HardwareControlSettings control={b().settings.control!} text={text()} onChange={b().onControlChange} />
      </Show>
      <Show when={props.panel === "reportRules"}>
        <ReportRuleSettings reports={b().settings.reports!} onChange={b().onReportsChange}
          runtimeFailureCode={b().runtimeFailureCode} />
      </Show>
      <Show when={props.panel === "appearance"}>
        <AppearanceSettingsSection
          onResetPreferences={b().onResetFrontendPreferences}
          settings={b().settings.appearance}
          text={text()}
          onUiStyleChange={b().onUiStyleChange}
          onThemeChange={b().onThemeChange}
          onAnimationStyleChange={b().onAnimationStyleChange}
          onResourceBarFrameRateModeChange={b().onResourceBarFrameRateModeChange}
          onResourceBarFrameRateChange={b().onResourceBarFrameRateChange}
          onBarColorModeChange={b().onBarColorModeChange}
          onByteUnitModeChange={b().onByteUnitModeChange}
          onFontSmoothingChange={b().onFontSmoothingChange}
          onLanguageChange={b().onLanguageChange}
        >
          <RefreshCadenceSetting
            settings={b().settings.performance}
            text={text()}
            onLogicRefreshIntervalChange={b().onLogicRefreshIntervalChange}
          />
        </AppearanceSettingsSection>
      </Show>
      <Show when={props.panel === "performanceOverlay"}>
        <PerformanceOverlaySettingsSection
          text={text()}
          overlayEditingAvailable={b().overlayEditingAvailable}
          softwareDetailOpen={b().softwareDetailOpen}
          onOpenOverlay={b().onOpenSoftwareOverlay}
        />
      </Show>
      <Show when={props.panel === "systemIntegration"}>
        <SystemIntegrationSettingsSection
          settings={b().settings.systemIntegration}
          text={text()}
          onTaskManagerShortcutReplacementChange={b().onTaskManagerShortcutReplacementChange}
          onAutoStartChange={b().onAutoStartChange}
          onServiceRecoveryChange={b().onServiceRecoveryChange}
          serviceRecovery={b().serviceRecovery}
          onEditableHotkeyChange={b().onEditableHotkeyChange}
        />
      </Show>
      <Show when={props.panel === "crashDumps"}>
        <div class="settings-section-panel">
          <Show when={b().settings.systemIntegration?.crashDumps}>{(settings) =>
            <CrashDumpSettings settings={settings()} diagnostics={b().crashDumps} text={text()}
              onChange={b().onCrashDumpsChange} onChooseMode={b().onChooseCrashDumpMode} />}</Show>
        </div>
      </Show>
      <Show when={props.panel === "crashDumpKeys"}>
        <div class="settings-section-panel">
          <Show when={b().settings.systemIntegration}>
            <CrashDumpKeysSetting settings={b().settings.systemIntegration?.crashDumpKeys} diagnostics={b().crashDumpKeys}
              onChange={b().onCrashDumpKeysEnabledChange} />
          </Show>
        </div>
      </Show>
      <Show when={props.panel === "publicServices"}>
        <PublicServicesSettingsSection
          publicServiceSettings={b().settings.publicService}
          aiModelServiceSettings={b().settings.aiModelService}
          text={text()}
          publicServicesAvailable={b().publicServicesAvailable}
          onLocalPublicServiceChange={b().onLocalPublicServiceChange}
          onPublicFileIndexChange={b().onPublicFileIndexChange}
          onPublicDatabaseServiceChange={b().onPublicDatabaseServiceChange}
          onPublicAiModelServiceChange={b().onPublicAiModelServiceChange}
          onLmStudioEndpointChange={b().onLmStudioEndpointChange}
          onLmStudioAutoStartChange={b().onLmStudioAutoStartChange}
        />
      </Show>
      <Show when={props.panel === "trustedAccess"}>
        <div class="settings-section-panel">
          <TrustedAccessManager copy={text().systemIntegration.trustedAccess}
            onFocusTargetChange={b().onTrustedAccessFocusTargetChange} />
        </div>
      </Show>
      <Show when={props.panel === "debug"}>
        <DebugSettingsSection
          settings={b().settings.debug}
          text={text()}
          optimizationRuntimeAvailable={b().optimizationRuntimeAvailable}
          onDebugModeChange={b().onDebugModeChange}
          onDebugLogChange={b().onDebugLogChange}
          onHostManagerSmartCoordinatorScoreOnlyChange={b().onHostManagerSmartCoordinatorScoreOnlyChange}
          onHostManagerSmartCoordinatorPerformanceLogChange={b().onHostManagerSmartCoordinatorPerformanceLogChange}
        />
      </Show>
      <Show when={props.panel === "notifications"}>
        <NotificationsSettingsSection notifications={b().settings.notifications} text={text()}
          onSwitchChange={b().onNotificationSwitchChange} />
      </Show>
      <Show when={props.panel === "updates"}>
        <UpdatesSettingsSection
          settings={b().settings.update}
          committedSettings={b().committedUpdateSettings}
          productUpdates={b().productUpdates}
          text={text()}
          onModeChange={b().onUpdateModeChange}
          onPeriodicCheckChange={b().onPeriodicUpdateCheckChange}
          onCheckIntervalChange={b().onCheckIntervalChange}
        />
      </Show>
      <Show when={props.panel === "credits"}>
        <CreditsSettingsSection text={text()} />
      </Show>
    </>
  );
}
