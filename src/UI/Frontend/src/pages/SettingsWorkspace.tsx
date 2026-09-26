import { onCleanup } from "solid-js";
import { SettingsPage } from "../features/settings/SettingsPage";
import type { SettingsStore } from "../stores/settingsStore";
import type { RuntimeCapabilitiesStore } from "../stores/runtimeCapabilitiesStore";

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
        onSmartMonitoringModeChange={settings.updateSmartMonitoringMode}
        onPreciseGpuPlacementChange={settings.updatePreciseGpuPlacement}
        onGpuPerformanceUseCasesChange={settings.updateGpuPerformanceUseCases}
        onAutomaticMemoryCleanupLinesChange={settings.updateAutomaticMemoryCleanupLines}
        onMemoryOptimizationTargetLinesChange={settings.updateMemoryOptimizationTargetLines}
        onFrontendHiddenRefreshModeChange={settings.updateFrontendHiddenRefreshMode}
        onLogicRefreshIntervalChange={settings.updateLogicRefreshInterval}
        onThemeChange={settings.updateTheme}
        onAnimationsChange={settings.updateAnimations}
        onResourceBarHardwareAccelerationModeChange={settings.updateResourceBarHardwareAccelerationMode}
        onBarColorModeChange={settings.updateBarColorMode}
        onByteUnitModeChange={settings.updateByteUnitMode}
        onFontSmoothingChange={settings.updateFontSmoothing}
        onLanguageChange={settings.updateLanguage}
        onTaskManagerShortcutReplacementChange={settings.updateTaskManagerShortcutReplacement}
        onAutoStartChange={settings.updateAutoStart}
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
