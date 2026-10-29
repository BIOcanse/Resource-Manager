import { onPageLeave } from "../../app/navigation/pageHost.tsx";
import { SettingsPage } from "./SettingsPage";
import type { SettingsStore } from "../../stores/settingsStore";
import type { RuntimeCapabilitiesStore } from "../../stores/runtimeCapabilitiesStore";
import type { ProductUpdateStore } from "../../stores/productUpdateStore";
import type { SoftwareRecord } from "../../types";
import type { ConfirmDialogRequest } from "../../components/AppFeedback";

interface SettingsWorkspaceProps {
  settings: SettingsStore;
  onTrustedAccessFocusTargetChange?: (target: HTMLElement | undefined) => void;
  productUpdates: ProductUpdateStore;
  runtimeCapabilities: RuntimeCapabilitiesStore;
  softwareDetailOpen: boolean;
  onOpenSoftwareOverlay: (software: SoftwareRecord) => void;
  confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>;
}

export function SettingsWorkspace(props: SettingsWorkspaceProps) {
  const settings = props.settings;
  // 未保存的设置草稿只属于这一次访问：离开设置页时丢弃（常驻时隐藏那一刻，否则卸下时）。
  onPageLeave(settings.discardDraftChanges);
  return (
    <SettingsPage
        onTrustedAccessFocusTargetChange={props.onTrustedAccessFocusTargetChange}
        settings={settings.draftSettings()}
        loadState={settings.loadState()}
        activeSection={settings.activeSection()}
        saveState={settings.saveState()}
        runtimeCapabilityConstrainedPaths={settings.runtimeCapabilityConstrainedPaths()}
        preciseGpuPlacementAvailable={props.runtimeCapabilities.gpuPlacementEnabled()}
        publicServicesAvailable={props.runtimeCapabilities.publicServicesEnabled()}
        optimizationRuntimeAvailable={props.runtimeCapabilities.optimizationEnabled()}
        isDirty={settings.isDirty()}
        onSectionChange={settings.setActiveSection}
        onReload={() => void settings.refresh()}
        onReapply={() => void settings.reapply()}
        onSave={() => void settings.saveDraft()}
        onRestoreDefaults={settings.resetDraftToDefaults}
        onResetFrontendPreferences={() => void settings.resetFrontendPreferences()}
        runtimeFailureCode={settings.runtimeFailureCode()}
        runtimeFailurePath={settings.runtimeFailurePath()}
        onReportsChange={settings.updateReports}
        onMemoryProtectionChange={settings.updateMemoryProtection}
        committedMemoryProtection={settings.settings().memoryProtection}
        confirmDialog={props.confirmDialog}
        onControlChange={settings.updateControl}
        onPreciseGpuPlacementChange={settings.updatePreciseGpuPlacement}
        onKeepSmartRecordHistoryChange={settings.updateKeepSmartRecordHistory}
        onAutomaticMemoryCleanupLinesChange={settings.updateAutomaticMemoryCleanupLines}
        onMemoryOptimizationTargetLinesChange={settings.updateMemoryOptimizationTargetLines}
        onGpuOverflowThresholdsChange={settings.updateGpuOverflowThresholds}
        onRealtimeActionBudgetChange={settings.updateRealtimeActionBudget}
        onLogicRefreshIntervalChange={settings.updateLogicRefreshInterval}
        onUiStyleChange={settings.updateUiStyle}
        onThemeChange={settings.updateTheme}
        onAnimationStyleChange={settings.updateAnimationStyle}
        onAutoDisableAnimationsChange={settings.updateAutoDisableAnimations}
        onResourceBarFrameRateModeChange={settings.updateResourceBarFrameRateMode}
        onResourceBarFrameRateChange={settings.updateResourceBarFrameRate}
        onBarColorModeChange={settings.updateBarColorMode}
        onByteUnitModeChange={settings.updateByteUnitMode}
        onFontSmoothingChange={settings.updateFontSmoothing}
        onLanguageChange={settings.updateLanguage}
        onTaskManagerShortcutReplacementChange={settings.updateTaskManagerShortcutReplacement}
        onAutoStartChange={settings.updateAutoStart}
        onServiceRecoveryChange={settings.updateServiceRecovery}
        serviceRecovery={settings.serviceRecovery()}
        productUpdates={props.productUpdates}
        committedUpdateSettings={settings.settings().update}
        onUpdateModeChange={settings.updateUpdateMode}
        onPeriodicUpdateCheckChange={settings.updatePeriodicUpdateCheck}
        onCheckIntervalChange={settings.updateCheckInterval}
        onNotificationSwitchChange={settings.updateNotificationSwitch}
        onEditableHotkeyChange={settings.updateEditableHotkey}
        onLocalPublicServiceChange={settings.updateLocalPublicService}
        onPublicFileIndexChange={settings.updatePublicFileIndex}
        onPublicDatabaseServiceChange={settings.updatePublicDatabaseService}
        onPublicAiModelServiceChange={settings.updatePublicAiModelService}
        onLmStudioEndpointChange={settings.updateLmStudioEndpoint}
        onLmStudioAutoStartChange={settings.updateLmStudioAutoStart}
        onDebugModeChange={settings.updateDebugMode}
        onDebugLogChange={settings.updateDebugLog}
        onHostManagerSmartCoordinatorScoreOnlyChange={settings.updateHostManagerSmartCoordinatorScoreOnly}
        onHostManagerSmartCoordinatorPerformanceLogChange={settings.updateHostManagerSmartCoordinatorPerformanceLog}
        overlayEditingAvailable={props.runtimeCapabilities.mutablePersistenceEnabled()}
        softwareDetailOpen={props.softwareDetailOpen}
        onOpenSoftwareOverlay={props.onOpenSoftwareOverlay}
    />
  );
}
