import { onCleanup } from "solid-js";
import { SettingsPage } from "./SettingsPage";
import type { SettingsStore } from "../../stores/settingsStore";
import type { RuntimeCapabilitiesStore } from "../../stores/runtimeCapabilitiesStore";

interface SettingsWorkspaceProps {
  settings: SettingsStore;
  runtimeCapabilities: RuntimeCapabilitiesStore;
}

export function SettingsWorkspace(props: SettingsWorkspaceProps) {
  const settings = props.settings;
  onCleanup(settings.discardDraftChanges);
  return (
    <SettingsPage
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
        onControlChange={settings.updateControl}
        onSmartMonitoringModeChange={settings.updateSmartMonitoringMode}
        onPreciseGpuPlacementChange={settings.updatePreciseGpuPlacement}
        onGpuPerformanceUseCasesChange={settings.updateGpuPerformanceUseCases}
        onAutomaticMemoryCleanupLinesChange={settings.updateAutomaticMemoryCleanupLines}
        onMemoryOptimizationTargetLinesChange={settings.updateMemoryOptimizationTargetLines}
        onGpuOverflowThresholdsChange={settings.updateGpuOverflowThresholds}
        onLogicRefreshIntervalChange={settings.updateLogicRefreshInterval}
        onUiStyleChange={settings.updateUiStyle}
        onThemeChange={settings.updateTheme}
        onAnimationsChange={settings.updateAnimations}
        onResourceBarFrameRateModeChange={settings.updateResourceBarFrameRateMode}
        onResourceBarFrameRateChange={settings.updateResourceBarFrameRate}
        onBarColorModeChange={settings.updateBarColorMode}
        onByteUnitModeChange={settings.updateByteUnitMode}
        onReadingSourceModeChange={settings.updateReadingSourceMode}
        onFontSmoothingChange={settings.updateFontSmoothing}
        onLanguageChange={settings.updateLanguage}
        onTaskManagerShortcutReplacementChange={settings.updateTaskManagerShortcutReplacement}
        onAutoStartChange={settings.updateAutoStart}
        onAutoUpdateChange={settings.updateAutoUpdate}
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
    />
  );
}
