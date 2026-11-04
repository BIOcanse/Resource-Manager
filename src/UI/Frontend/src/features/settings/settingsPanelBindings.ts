import type { ConfirmDialogRequest } from "../../components/AppFeedback";
import type { SettingsStore } from "../../stores/settingsStore";
import type { RuntimeCapabilitiesStore } from "../../stores/runtimeCapabilitiesStore";
import type { ProductUpdateStore } from "../../stores/productUpdateStore";
import type { AnimationStyleId } from "../../presentation/animationStyle.ts";
import type { CpuMonopolyProtectionSettings } from "../../data/cpuMonopolyProtection/cpuMonopolyProtection.ts";
import type {
  AppBarColorMode,
  AppCrashDumpMode,
  AppCrashDumpSettings,
  AppEditableHotkeySettings,
  AppFontSmoothing,
  AppLanguageMode,
  AppLogicRefreshIntervalKey,
  AppNotificationSettings,
  AppPerformanceSettings,
  AppPresetNumericSetting,
  AppResourceBarFrameRateMode,
  AppSettings,
  AppPalette,
  AppUiStyle,
  AppUpdateMode,
  AppUpdateSettings,
  ByteUnitMode,
  CrashDumpDiagnostics,
  CrashDumpKeyDiagnostics,
  ServiceRecoveryDiagnostics,
  SettingsPanel,
  SoftwareRecord
} from "../../types";

/** Share one settingsStore draft, loading state, save state, and actions across all settings panels. */
export interface SettingsPanelBindings {
  settings: AppSettings;
  onTrustedAccessFocusTargetChange?: (target: HTMLElement | undefined) => void;
  loadState: "loading" | "refreshing" | "ready" | "stale" | "error";
  saveState: "idle" | "saving" | "saved" | "constrained" | "partial" | "error" | "conflict";
  runtimeCapabilityConstrainedPaths: readonly string[];
  runtimeFailureCode: string | null;
  /** JSON path supplied with invalid-settings-value-type. */
  runtimeFailurePath: string | null;
  onReportsChange: (reports: NonNullable<AppSettings["reports"]>) => void;
  onMemoryProtectionChange: (memoryProtection: NonNullable<AppSettings["memoryProtection"]>) => void;
  onMemoryCpuTierChange: (memoryCpuTier: NonNullable<AppSettings["memoryCpuTier"]>) => void;
  /** Compare saved tier settings to distinguish previews; reread backend values after save. */
  committedMemoryCpuTier: AppSettings["memoryCpuTier"];
  /** Confirm irreversible deletion of a saved operating point. */
  confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>;
  /** Use saved protection settings to detect changes and refresh backend state after save. */
  committedMemoryProtection: AppSettings["memoryProtection"];
  preciseGpuPlacementAvailable: boolean;
  publicServicesAvailable: boolean;
  optimizationRuntimeAvailable: boolean;
  isDirty: boolean;
  onReload: () => void;
  onReapply: () => void;
  onSave: () => void;
  /** Reset only fields owned by this section. */
  onRestoreDefaults: (panel: SettingsPanel) => void;
  onResetFrontendPreferences: () => void;
  onControlChange: (control: NonNullable<AppSettings["control"]>) => void;
  onPreciseGpuPlacementChange: (enabled: boolean) => void;
  onKeepSmartRecordHistoryChange: (enabled: boolean) => void;
  onAutomaticMemoryCleanupLinesChange: (physicalMemoryPercent: number, virtualMemoryPercent: number) => void;
  onMemoryOptimizationTargetLinesChange: (physicalMemoryPercent: number, virtualMemoryPercent: number) => void;
  onGpuOverflowThresholdsChange: (gpuUsagePercent: number, dedicatedMemoryPercent: number) => void;
  onRealtimeActionBudgetChange: (mode: AppPerformanceSettings["realtimeActionBudgetMode"], count: number, percent: number) => void;
  onLogicRefreshIntervalChange: (key: AppLogicRefreshIntervalKey, setting: AppPresetNumericSetting) => void;
  onUiStyleChange: (uiStyle: AppUiStyle) => void;
  onThemeChange: (theme: AppPalette) => void;
  onAnimationStyleChange: (style: AnimationStyleId) => void;
  onResourceBarFrameRateModeChange: (mode: AppResourceBarFrameRateMode) => void;
  onResourceBarFrameRateChange: (framesPerSecond: number) => void;
  onBarColorModeChange: (barColorMode: AppBarColorMode) => void;
  onByteUnitModeChange: (byteUnitMode: ByteUnitMode) => void;
  onFontSmoothingChange: (fontSmoothing: AppFontSmoothing) => void;
  onLanguageChange: (language: AppLanguageMode) => void;
  onTaskManagerShortcutReplacementChange: (enabled: boolean) => void;
  onAutoStartChange: (enabled: boolean) => void;
  onServiceRecoveryChange: (enabled: boolean, delay: number, count: number) => void;
  serviceRecovery?: ServiceRecoveryDiagnostics;
  crashDumps?: CrashDumpDiagnostics;
  onCrashDumpsChange: (crashDumps: AppCrashDumpSettings) => void;
  onChooseCrashDumpMode: (mode: AppCrashDumpMode) => Promise<void>;
  crashDumpKeys?: CrashDumpKeyDiagnostics | null;
  onCrashDumpKeysEnabledChange: (enabled: boolean) => void;
  onCpuMonopolyProtectionChange: (settings: CpuMonopolyProtectionSettings) => void;
  /** Use saved CPU protection settings to detect changes. */
  committedCpuMonopolyProtection: AppSettings["cpuMonopolyProtection"];
  productUpdates: ProductUpdateStore;
  /** Use saved interval settings for next-check and connected-time rows; drafts do not change runtime state. */
  committedUpdateSettings: AppUpdateSettings | undefined;
  onUpdateModeChange: (mode: AppUpdateMode) => void;
  onPeriodicUpdateCheckChange: (enabled: boolean) => void;
  onCheckIntervalChange: (hours: number) => void;
  onNotificationSwitchChange: (path: keyof AppNotificationSettings, enabled: boolean) => void;
  onEditableHotkeyChange: (hotkey: AppEditableHotkeySettings) => void;
  onLocalPublicServiceChange: (enabled: boolean) => void;
  onPublicFileIndexChange: (enabled: boolean) => void;
  onPublicDatabaseServiceChange: (enabled: boolean) => void;
  onPublicAiModelServiceChange: (enabled: boolean) => void;
  onLmStudioEndpointChange: (endpoint: string) => void;
  onLmStudioAutoStartChange: (enabled: boolean) => void;
  onDebugModeChange: (enabled: boolean) => void;
  onDebugLogChange: (enabled: boolean) => void;
  onHostManagerSmartCoordinatorScoreOnlyChange: (enabled: boolean) => void;
  onHostManagerSmartCoordinatorPerformanceLogChange: (enabled: boolean) => void;
  /** Whether software details may edit overlays. */
  overlayEditingAvailable: boolean;
  /** Whether the software-details dialog is open. */
  softwareDetailOpen: boolean;
  /** Open software details at its overlay tab. */
  onOpenSoftwareOverlay: (software: SoftwareRecord) => void;
}

/** Combine the settings store with page-level capabilities. */
export interface SettingsPanelSources {
  readonly settings: SettingsStore;
  readonly productUpdates: ProductUpdateStore;
  readonly runtimeCapabilities: RuntimeCapabilitiesStore;
  readonly softwareDetailOpen: () => boolean;
  readonly onOpenSoftwareOverlay: (software: SoftwareRecord) => void;
  readonly confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>;
  readonly onTrustedAccessFocusTargetChange?: (target: HTMLElement | undefined) => void;
}

/** Expose reactive getters so consumers track store changes. */
export function createSettingsPanelBindings(sources: SettingsPanelSources): SettingsPanelBindings {
  const store = sources.settings;
  const capabilities = sources.runtimeCapabilities;
  return {
    get settings() { return store.draftSettings(); },
    get onTrustedAccessFocusTargetChange() { return sources.onTrustedAccessFocusTargetChange; },
    get loadState() { return store.loadState(); },
    get saveState() { return store.saveState(); },
    get runtimeCapabilityConstrainedPaths() { return store.runtimeCapabilityConstrainedPaths(); },
    get runtimeFailureCode() { return store.runtimeFailureCode(); },
    get runtimeFailurePath() { return store.runtimeFailurePath(); },
    onReportsChange: store.updateReports,
    onMemoryProtectionChange: store.updateMemoryProtection,
    onMemoryCpuTierChange: store.updateMemoryCpuTier,
    get committedMemoryCpuTier() { return store.settings().memoryCpuTier; },
    confirmDialog: (request) => sources.confirmDialog(request),
    get committedMemoryProtection() { return store.settings().memoryProtection; },
    get preciseGpuPlacementAvailable() { return capabilities.gpuPlacementEnabled(); },
    get publicServicesAvailable() { return capabilities.publicServicesEnabled(); },
    get optimizationRuntimeAvailable() { return capabilities.optimizationEnabled(); },
    get isDirty() { return store.isDirty(); },
    onReload: () => void store.refresh(),
    onReapply: () => void store.reapply(),
    onSave: () => void store.saveDraft(),
    onRestoreDefaults: store.resetDraftToDefaults,
    onResetFrontendPreferences: () => void store.resetFrontendPreferences(),
    onControlChange: store.updateControl,
    onPreciseGpuPlacementChange: store.updatePreciseGpuPlacement,
    onKeepSmartRecordHistoryChange: store.updateKeepSmartRecordHistory,
    onAutomaticMemoryCleanupLinesChange: store.updateAutomaticMemoryCleanupLines,
    onMemoryOptimizationTargetLinesChange: store.updateMemoryOptimizationTargetLines,
    onGpuOverflowThresholdsChange: store.updateGpuOverflowThresholds,
    onRealtimeActionBudgetChange: store.updateRealtimeActionBudget,
    onLogicRefreshIntervalChange: store.updateLogicRefreshInterval,
    onUiStyleChange: store.updateUiStyle,
    onThemeChange: store.updateTheme,
    onAnimationStyleChange: store.updateAnimationStyle,
    onResourceBarFrameRateModeChange: store.updateResourceBarFrameRateMode,
    onResourceBarFrameRateChange: store.updateResourceBarFrameRate,
    onBarColorModeChange: store.updateBarColorMode,
    onByteUnitModeChange: store.updateByteUnitMode,
    onFontSmoothingChange: store.updateFontSmoothing,
    onLanguageChange: store.updateLanguage,
    onTaskManagerShortcutReplacementChange: store.updateTaskManagerShortcutReplacement,
    onAutoStartChange: store.updateAutoStart,
    onServiceRecoveryChange: store.updateServiceRecovery,
    get serviceRecovery() { return store.serviceRecovery(); },
    get crashDumps() { return store.crashDumps(); },
    onCrashDumpsChange: store.updateCrashDumps,
    onChooseCrashDumpMode: store.chooseCrashDumpMode,
    get crashDumpKeys() { return store.crashDumpKeys(); },
    onCrashDumpKeysEnabledChange: store.updateCrashDumpKeysEnabled,
    onCpuMonopolyProtectionChange: store.updateCpuMonopolyProtection,
    get committedCpuMonopolyProtection() { return store.settings().cpuMonopolyProtection; },
    productUpdates: sources.productUpdates,
    get committedUpdateSettings() { return store.settings().update; },
    onUpdateModeChange: store.updateUpdateMode,
    onPeriodicUpdateCheckChange: store.updatePeriodicUpdateCheck,
    onCheckIntervalChange: store.updateCheckInterval,
    onNotificationSwitchChange: store.updateNotificationSwitch,
    onEditableHotkeyChange: store.updateEditableHotkey,
    onLocalPublicServiceChange: store.updateLocalPublicService,
    onPublicFileIndexChange: store.updatePublicFileIndex,
    onPublicDatabaseServiceChange: store.updatePublicDatabaseService,
    onPublicAiModelServiceChange: store.updatePublicAiModelService,
    onLmStudioEndpointChange: store.updateLmStudioEndpoint,
    onLmStudioAutoStartChange: store.updateLmStudioAutoStart,
    onDebugModeChange: store.updateDebugMode,
    onDebugLogChange: store.updateDebugLog,
    onHostManagerSmartCoordinatorScoreOnlyChange: store.updateHostManagerSmartCoordinatorScoreOnly,
    onHostManagerSmartCoordinatorPerformanceLogChange: store.updateHostManagerSmartCoordinatorPerformanceLog,
    get overlayEditingAvailable() { return capabilities.mutablePersistenceEnabled(); },
    get softwareDetailOpen() { return sources.softwareDetailOpen(); },
    onOpenSoftwareOverlay: (software) => sources.onOpenSoftwareOverlay(software)
  };
}
