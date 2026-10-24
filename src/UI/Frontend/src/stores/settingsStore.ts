import {
  batch,
  createMemo,
  createSignal,
  onCleanup
} from "solid-js";
import type { Accessor, Setter } from "solid-js";
import {
  AppSettingsRevisionConflictError,
  reapplyAppSettings,
  saveAppSettingsPatch
} from "../data/appSettings/appSettingsApi.ts";
import type {
  CommittedAppSettingsResult
} from "../data/appSettings/appSettingsResultDecoder.ts";
import type { RequestClient } from "../frontendRuntime/request/RequestClient.ts";
import type {
  SourceHandle
} from "../frontendRuntime/source/SourceDescriptor.ts";
import type {
  SourceSnapshot
} from "../frontendRuntime/source/SourceSnapshot.ts";
import { classifySettingsApplicationOutcome } from "../features/settings/settingsApplicationDisposition.ts";
import { normalizeByteUnitMode } from "../presentation/byteUnits.ts";
import { normalizeReadingSourceMode } from "../presentation/readingSources.ts";
import { normalizeLanguageMode } from "../i18n/settingsLanguages.ts";
import type {
  AppAnimationMode,
  AppAiModelServiceSettings,
  AppAdaptiveBooleanMode,
  AppAppearanceSettings,
  AppEditableHotkeySettings,
  AppBarColorMode,
  ByteUnitMode,
  AppFontSmoothing,
  AppGpuPerformanceUseCase,
  AppLanguageMode,
  AppLogicRefreshIntervalKey,
  AppLogicRefreshIntervalPreset,
  AppLocalPublicServiceSettings,
  AppOptimizationMode,
  AppPerformanceSettings,
  AppPresetNumericSetting,
  ResourceManagerSelfCpuGrade,
  ResourceManagerSelfGpuGrade,
  AppPresetNumericSettingMode,
  AppSettings,
  AppSystemIntegrationSettings,
  AppPalette,
  AppReadingSourceMode,
  AppResourceBarFrameRateMode,
  AppUiStyle,
  SettingsSection
} from "../types";
import { normalizeUiAppearance } from "../presentation/uiStyles.ts";
import {
  normalizeResourceBarFrameRate,
  normalizeResourceBarFrameRateMode,
  resourceBarFrameRateDefault
} from "../presentation/resourceBarFrameRate.ts";
import { postShellMessage } from "../utils.ts";
import { normalizeEditableHotkeys } from "../features/settings/editableHotkeys.ts";
import { normalizeOptimizationModeValue } from "../features/settings/optimizationMode.ts";
import { applySettingsPatch, createSettingsPatch } from "../features/settings/settingsPatch.ts";
import { uiText } from "../text.ts";

const prebootAppearanceStorageKey = "resource-manager:appearance";
export const logicRefreshIntervalKeys: AppLogicRefreshIntervalKey[] = [
  "monitor",
  "resourceTable",
  "localSystem"
];
export const logicRefreshIntervalPresetValues: Record<AppLogicRefreshIntervalKey, Record<AppLogicRefreshIntervalPreset, number>> = {
  monitor: { responsive: 1000, balanced: 2000, lowPower: 3000, quiet: 5000 },
  resourceTable: { responsive: 1000, balanced: 2000, lowPower: 3000, quiet: 5000 },
  localSystem: { responsive: 30000, balanced: 60000, lowPower: 120000, quiet: 300000 }
};
const defaultLogicRefreshIntervalPreset: Record<AppLogicRefreshIntervalKey, AppLogicRefreshIntervalPreset> = {
  monitor: "responsive",
  resourceTable: "responsive",
  localSystem: "balanced"
};
const minLogicRefreshIntervalMs = 500;
const maxLogicRefreshIntervalMs = 300000;

export interface SettingsStore {
  settings: Accessor<AppSettings>;
  draftSettings: Accessor<AppSettings>;
  isDirty: Accessor<boolean>;
  loadState: Accessor<"loading" | "refreshing" | "ready" | "stale" | "error">;
  saveState: Accessor<"idle" | "saving" | "saved" | "constrained" | "partial" | "error" | "conflict">;
  runtimeCapabilityConstrainedPaths: Accessor<readonly string[]>;
  activeSection: Accessor<SettingsSection>;
  setActiveSection: Setter<SettingsSection>;
  refresh: () => Promise<void>;
  saveDraft: () => Promise<void>;
  reapply: () => Promise<void>;
  discardDraftChanges: () => void;
  resetDraftToDefaults: () => void;
  updateControl: (control: NonNullable<AppSettings["control"]>) => void;
  updateSmartMonitoringMode: (mode: AppAdaptiveBooleanMode) => void;
  updatePreciseGpuPlacement: (enabled: boolean) => void;
  updateGpuPerformanceUseCases: (useCases: AppGpuPerformanceUseCase[]) => void;
  updateAutomaticMemoryCleanupLines: (physicalMemoryPercent: number, virtualMemoryPercent: number) => void;
  updateMemoryOptimizationTargetLines: (physicalMemoryPercent: number, virtualMemoryPercent: number) => void;
  updateGpuOverflowThresholds: (gpuUsagePercent: number, dedicatedMemoryPercent: number) => void;
  updateLogicRefreshInterval: (key: AppLogicRefreshIntervalKey, setting: AppPresetNumericSetting) => void;
  updateUiStyle: (uiStyle: AppUiStyle) => void;
  updateTheme: (theme: AppPalette) => void;
  updateAnimations: (animations: AppAnimationMode) => void;
  updateResourceBarFrameRateMode: (mode: AppResourceBarFrameRateMode) => void;
  updateResourceBarFrameRate: (framesPerSecond: number) => void;
  updateBarColorMode: (barColorMode: AppBarColorMode) => void;
  updateByteUnitMode: (byteUnitMode: ByteUnitMode) => void;
  updateReadingSourceMode: (mode: AppReadingSourceMode) => void;
  updateFontSmoothing: (fontSmoothing: AppFontSmoothing) => void;
  updateLanguage: (language: AppLanguageMode) => void;
  updateTaskManagerShortcutReplacement: (enabled: boolean) => void;
  updateAutoStart: (enabled: boolean) => void;
  updateAutoUpdate: (enabled: boolean) => void;
  updateEditableHotkey: (hotkey: AppEditableHotkeySettings) => void;
  updateLocalPublicService: (enabled: boolean) => void;
  updatePublicFileIndex: (enabled: boolean) => void;
  updatePublicDatabaseService: (enabled: boolean) => void;
  updatePublicAiModelService: (enabled: boolean) => void;
  updateLmStudioEndpoint: (endpoint: string) => void;
  updateLmStudioAutoStart: (enabled: boolean) => void;
  updateDebugMode: (enabled: boolean) => void;
  updateDebugLog: (enabled: boolean) => void;
  updateHostManagerSmartCoordinatorScoreOnly: (enabled: boolean) => void;
  updateHostManagerSmartCoordinatorPerformanceLog: (enabled: boolean) => void;
}

interface SettingsStoreOptions {
  readonly source: SourceHandle<CommittedAppSettingsResult>;
  readonly requestClient: Pick<RequestClient, "request">;
}

type SourceProjectionMode = "replace-draft" | "preserve-draft";

export function createSettingsStore(options: SettingsStoreOptions): SettingsStore {
  const [settings, setSettings] = createSignal<AppSettings>(defaultAppSettings());
  const [draftSettings, setDraftSettings] = createSignal<AppSettings>(defaultAppSettings());
  const [revision, setRevision] = createSignal<string | null>(null);
  const [loadState, setLoadState] = createSignal<"loading" | "refreshing" | "ready" | "stale" | "error">("loading");
  const [saveState, setSaveState] = createSignal<"idle" | "saving" | "saved" | "constrained" | "partial" | "error" | "conflict">("idle");
  const [runtimeCapabilityConstrainedPaths, setRuntimeCapabilityConstrainedPaths] = createSignal<readonly string[]>([]);
  const [activeSection, setActiveSection] = createSignal<SettingsSection>("performance");
  const isDirty = createMemo(() => revision() !== null
    && !areSettingsEqual(settings(), draftSettings()));
  const source = options.source.acquire({ active: false, refreshIntervalMs: null });
  let lastProjectedSourceRevision = -1;
  let nextProjectionMode: SourceProjectionMode | null = null;
  const unsubscribeSource = source.subscribe((snapshot) => projectSource(snapshot));
  source.setDemand({ active: true, refreshIntervalMs: null });
  projectSource(source.snapshot);
  onCleanup(() => {
    unsubscribeSource();
    source.release();
  });

  async function refresh() {
    if (saveState() === "saving") return;
    nextProjectionMode = "replace-draft";
    try {
      const snapshot = await source.refresh();
      projectSource(snapshot, "replace-draft");
      if (snapshot.status === "ready" && snapshot.data) {
        setSaveState("idle");
        setRuntimeCapabilityConstrainedPaths([]);
      }
    } finally {
      nextProjectionMode = null;
    }
  }

  function updateDraft(updater: (settings: AppSettings) => AppSettings) {
    if (loadState() !== "ready" || saveState() === "conflict") {
      return;
    }
    const next = normalizeAppSettings(updater(structuredClone(draftSettings())));
    setDraftSettings(next);
    setSaveState((state) => state === "saved" || state === "constrained" ? "idle" : state);
    setRuntimeCapabilityConstrainedPaths([]);
  }

  async function saveDraft() {
    const expectedRevision = revision();
    if (loadState() !== "ready" || !expectedRevision || saveState() === "conflict" || saveState() === "saving") {
      return;
    }
    const submittedDraft = draftSettings();
    const next = alignRuntimeOwnedSettings(
      normalizeAppSettings(submittedDraft),
      settings());
    const changes = createSettingsPatch(
      settings() as unknown as Record<string, unknown>,
      next as unknown as Record<string, unknown>);
    if (Object.keys(changes).length === 0) {
      setSaveState("idle");
      return;
    }
    setSaveState("saving");
    setRuntimeCapabilityConstrainedPaths([]);
    try {
      const result = await saveAppSettingsPatch(
        options.requestClient,
        expectedRevision,
        changes);
      const outcome = classifySettingsApplicationOutcome(result.runtimeApplicationDisposition);
      if (outcome === "rejected") {
        throw new Error(uiText.misc.settingsNotCommitted);
      }
      const newerEdits = createSettingsPatch(
        submittedDraft as unknown as Record<string, unknown>,
        draftSettings() as unknown as Record<string, unknown>);
      batch(() => {
        acceptCommitted(result, "replace-draft");
        setDraftSettings(alignRuntimeOwnedSettings(
          applySettingsPatch(settings(), newerEdits), settings()));
      });
      setRuntimeCapabilityConstrainedPaths(result.runtimeCapabilityConstrainedPaths ?? []);
      setSaveState(outcome === "applied"
        ? isDirty() ? "idle" : "saved"
        : outcome === "capability-constrained"
          ? "constrained"
          : "partial");
      if (outcome === "applied") {
        window.setTimeout(() => setSaveState((state) => state === "saved" ? "idle" : state), 1600);
      }
    } catch (error) {
      if (error instanceof AppSettingsRevisionConflictError) {
        acceptCommitted(error.result, "preserve-draft");
        setSaveState("conflict");
        return;
      }
      setSaveState("error");
    }
  }

  async function reapply() {
    if (loadState() !== "ready" || saveState() !== "partial") {
      return;
    }
    setSaveState("saving");
    try {
      const result = await reapplyAppSettings(options.requestClient);
      acceptCommitted(result, "preserve-draft");
      const outcome = classifySettingsApplicationOutcome(result.runtimeApplicationDisposition);
      setRuntimeCapabilityConstrainedPaths(result.runtimeCapabilityConstrainedPaths ?? []);
      setSaveState(outcome === "applied"
        ? isDirty() ? "idle" : "saved"
        : outcome === "capability-constrained"
          ? "constrained"
          : outcome === "persisted-pending"
            ? "partial"
            : "error");
      if (outcome === "applied") {
        window.setTimeout(() => setSaveState((state) => state === "saved" ? "idle" : state), 1600);
      }
    } catch {
      setSaveState("partial");
    }
  }

  function acceptCommitted(
    result: CommittedAppSettingsResult,
    mode: SourceProjectionMode
  ): void {
    nextProjectionMode = mode;
    try {
      projectSource(source.acceptAuthoritative(result), mode);
    } finally {
      nextProjectionMode = null;
    }
  }

  function projectSource(
    snapshot: SourceSnapshot<CommittedAppSettingsResult>,
    requestedMode?: SourceProjectionMode
  ): void {
    const nextLoadState = (() => {
      switch (snapshot.status) {
      case "ready":
        return "ready" as const;
      case "stale":
        return "stale" as const;
      case "refreshing":
        return snapshot.data ? "refreshing" as const : "loading" as const;
      case "error":
      case "unavailable":
      case "disposed":
        return "error" as const;
      default:
        return "loading" as const;
      }
    })();
    if (snapshot.status !== "ready"
      || !snapshot.data
      || snapshot.revision === lastProjectedSourceRevision) {
      setLoadState(nextLoadState);
      return;
    }

    const mode = requestedMode
      ?? nextProjectionMode
      ?? (areSettingsEqual(settings(), draftSettings())
        ? "replace-draft"
        : "preserve-draft");
    const loaded = normalizeAppSettings(snapshot.data.settings);
    const nextDraft = mode === "replace-draft"
      ? loaded
      : alignRuntimeOwnedSettings(applySettingsPatch(loaded, createSettingsPatch(
          settings() as unknown as Record<string, unknown>,
          draftSettings() as unknown as Record<string, unknown>)), loaded);
    setSettings(loaded);
    setDraftSettings(nextDraft);
    setRevision(snapshot.data.revision);
    syncPrebootAppearance(loaded);
    syncShellSettings(loaded);
    lastProjectedSourceRevision = snapshot.revision;
    nextProjectionMode = null;
    setLoadState(nextLoadState);
  }

  function resetDraftToDefaults() {
    if (loadState() !== "ready" || saveState() === "conflict") {
      return;
    }
    setDraftSettings(defaultAppSettings());
    setSaveState((state) => state === "saved" || state === "constrained" ? "idle" : state);
    setRuntimeCapabilityConstrainedPaths([]);
  }

  function discardDraftChanges() {
    setDraftSettings(normalizeAppSettings(settings()));
    setSaveState((state) => state === "saving" ? state : "idle");
    setRuntimeCapabilityConstrainedPaths([]);
  }

  function updatePerformance(updater: (performance: AppPerformanceSettings) => AppPerformanceSettings) {
    updateDraft((currentSettings) => {
      const current = normalizeAppSettings(currentSettings).performance!;
      return {
        ...currentSettings,
        performance: updater(current)
      };
    });
  }

  function updateSystemIntegration(updater: (systemIntegration: AppSystemIntegrationSettings) => AppSystemIntegrationSettings) {
    updateDraft((currentSettings) => {
      const current = normalizeAppSettings(currentSettings).systemIntegration!;
      return {
        ...currentSettings,
        systemIntegration: updater(current)
      };
    });
  }

  function updatePublicService(updater: (publicService: AppLocalPublicServiceSettings) => AppLocalPublicServiceSettings) {
    updateDraft((currentSettings) => {
      const current = normalizeAppSettings(currentSettings).publicService!;
      return {
        ...currentSettings,
        publicService: updater(current)
      };
    });
  }

  function updateAiModelService(updater: (aiModelService: AppAiModelServiceSettings) => AppAiModelServiceSettings) {
    updateDraft((currentSettings) => {
      const current = normalizeAppSettings(currentSettings).aiModelService!;
      return {
        ...currentSettings,
        aiModelService: updater(current)
      };
    });
  }

  return {
    settings,
    draftSettings,
    isDirty,
    loadState,
    saveState,
    runtimeCapabilityConstrainedPaths,
    activeSection,
    setActiveSection,
    refresh,
    saveDraft,
    reapply,
    discardDraftChanges,
    resetDraftToDefaults,
    updateControl: (control) => updateDraft(current => ({ ...current, control })),
    updateSmartMonitoringMode: (mode) => updatePerformance((performance) => ({
      ...performance,
      smartMonitoringMode: normalizeAdaptiveBooleanMode(mode)
    })),
    updatePreciseGpuPlacement: (enabled) => updatePerformance((performance) => ({
      ...performance,
      preciseGpuPlacementEnabled: enabled
    })),
    updateGpuPerformanceUseCases: (useCases) => updatePerformance((performance) => ({
      ...performance,
      gpuPerformanceUseCases: normalizeGpuPerformanceUseCases(useCases)
    })),
    updateAutomaticMemoryCleanupLines: (physicalMemoryPercent, virtualMemoryPercent) => updatePerformance((performance) => ({
      ...performance,
      physicalMemoryAutomaticCleanupPercent: normalizeDangerPercent(physicalMemoryPercent, 6),
      virtualMemoryAutomaticCleanupPercent: normalizeDangerPercent(virtualMemoryPercent, 8)
    })),
    updateMemoryOptimizationTargetLines: (physicalMemoryPercent, virtualMemoryPercent) => updatePerformance((performance) => ({
      ...performance,
      physicalMemoryOptimizationTargetUsagePercent: normalizeTargetUsagePercent(physicalMemoryPercent, 70),
      virtualMemoryOptimizationTargetUsagePercent: normalizeTargetUsagePercent(virtualMemoryPercent, 70)
    })),
    updateGpuOverflowThresholds: (gpuUsagePercent, dedicatedMemoryPercent) => updatePerformance((performance) => ({
      ...performance,
      gpuOverflowUsagePercent: normalizeGpuOverflowPercent(gpuUsagePercent, performance.gpuOverflowUsagePercent),
      gpuOverflowDedicatedMemoryPercent: normalizeGpuOverflowPercent(dedicatedMemoryPercent, performance.gpuOverflowDedicatedMemoryPercent)
    })),
    updateLogicRefreshInterval: (key, setting) => updatePerformance((performance) =>
      assignLogicRefreshInterval(performance, key, normalizeLogicRefreshIntervalSetting(key, setting))),
    // 换风格时配色在新风格里也有就保留，没有就用新风格的默认配色。
    updateUiStyle: (uiStyle) => {
      updateDraft((currentSettings) => {
        const appearance = normalizeAppSettings(currentSettings).appearance!;
        return {
          ...currentSettings,
          appearance: { ...appearance, ...normalizeUiAppearance(uiStyle, appearance.theme) }
        };
      });
    },
    updateTheme: (theme) => {
      updateDraft((currentSettings) => {
        const appearance = normalizeAppSettings(currentSettings).appearance!;
        return {
          ...currentSettings,
          appearance: { ...appearance, ...normalizeUiAppearance(appearance.uiStyle, theme) }
        };
      });
    },
    updateAnimations: (animations) => {
      updateDraft((currentSettings) => ({
        ...currentSettings,
        appearance: {
          ...normalizeAppSettings(currentSettings).appearance!,
          animations
        }
      }));
    },
    updateResourceBarFrameRateMode: (mode) => {
      updateDraft((currentSettings) => ({
        ...currentSettings,
        appearance: {
          ...normalizeAppSettings(currentSettings).appearance!,
          resourceBarFrameRateMode: normalizeResourceBarFrameRateMode(mode)
        }
      }));
    },
    updateResourceBarFrameRate: (framesPerSecond) => {
      updateDraft((currentSettings) => ({
        ...currentSettings,
        appearance: {
          ...normalizeAppSettings(currentSettings).appearance!,
          resourceBarFrameRate: normalizeResourceBarFrameRate(framesPerSecond)
        }
      }));
    },
    updateBarColorMode: (barColorMode) => {
      updateDraft((currentSettings) => ({
        ...currentSettings,
        appearance: {
          ...normalizeAppSettings(currentSettings).appearance!,
          barColorMode
        }
      }));
    },
    updateByteUnitMode: (byteUnitMode) => {
      updateDraft((currentSettings) => ({
        ...currentSettings,
        appearance: {
          ...normalizeAppSettings(currentSettings).appearance!,
          byteUnitMode
        }
      }));
    },
    updateReadingSourceMode: (mode) => {
      updateDraft((currentSettings) => ({
        ...currentSettings,
        appearance: {
          ...normalizeAppSettings(currentSettings).appearance!,
          readingSourceMode: normalizeReadingSourceMode(mode)
        }
      }));
    },
    updateFontSmoothing: (fontSmoothing) => {
      updateDraft((currentSettings) => ({
        ...currentSettings,
        appearance: {
          ...normalizeAppSettings(currentSettings).appearance!,
          fontSmoothing
        }
      }));
    },
    updateLanguage: (language) => {
      updateDraft((currentSettings) => ({
        ...currentSettings,
        appearance: {
          ...normalizeAppSettings(currentSettings).appearance!,
          language: normalizeLanguageMode(language)
        }
      }));
    },
    updateAutoStart: (enabled) => updateSystemIntegration((systemIntegration) => ({
      ...systemIntegration,
      autoStartEnabled: enabled
    })),
    updateAutoUpdate: (enabled) => updateDraft((currentSettings) => ({
      ...currentSettings,
      update: { autoUpdateEnabled: enabled }
    })),
    updateTaskManagerShortcutReplacement: (enabled) => updateSystemIntegration((systemIntegration) => ({
      ...systemIntegration,
      taskManagerShortcutReplacementEnabled: enabled
    })),
    updateEditableHotkey: (hotkey) => updateSystemIntegration((systemIntegration) => ({
      ...systemIntegration,
      hotkeys: normalizeEditableHotkeys([
        ...(systemIntegration.hotkeys ?? []).filter((item) => item.actionId !== hotkey.actionId),
        hotkey
      ])
    })),
    updateLocalPublicService: (enabled) => updatePublicService((publicService) => ({
      ...publicService,
      enabled
    })),
    updatePublicFileIndex: (enabled) => updatePublicService((publicService) => ({
      ...publicService,
      fileIndexEnabled: enabled
    })),
    updatePublicDatabaseService: (enabled) => updatePublicService((publicService) => ({
      ...publicService,
      databaseServiceEnabled: enabled
    })),
    updatePublicAiModelService: (enabled) => updatePublicService((publicService) => ({
      ...publicService,
      aiModelCatalogEnabled: enabled
    })),
    updateLmStudioEndpoint: (endpoint) => updateAiModelService((aiModelService) => ({
      ...aiModelService,
      endpoint
    })),
    updateLmStudioAutoStart: (enabled) => updateAiModelService((aiModelService) => ({
      ...aiModelService,
      autoStartEnabled: enabled
    })),
    updateDebugMode: (enabled) => {
      updateDraft((currentSettings) => ({
        ...currentSettings,
        debug: {
          ...normalizeDebugSettings(currentSettings.debug),
          debugModeEnabled: enabled
        }
      }));
    },
    updateDebugLog: (enabled) => {
      updateDraft((currentSettings) => ({
        ...currentSettings,
        debug: {
          ...normalizeDebugSettings(currentSettings.debug),
          debugLogEnabled: enabled
        }
      }));
    },
    updateHostManagerSmartCoordinatorScoreOnly: (enabled) => {
      updateDraft((currentSettings) => ({
        ...currentSettings,
        debug: {
          ...normalizeDebugSettings(currentSettings.debug),
          hostManagerSmartCoordinatorScoreOnlyEnabled: enabled
        }
      }));
    },
    updateHostManagerSmartCoordinatorPerformanceLog: (enabled) => {
      updateDraft((currentSettings) => ({
        ...currentSettings,
        debug: {
          ...normalizeDebugSettings(currentSettings.debug),
          hostManagerSmartCoordinatorPerformanceLogEnabled: enabled
        }
      }));
    }
  };
}

export function defaultAppSettings(): AppSettings {
  return {
    version: "1.0.28",
    control: { reassertionIntervalSeconds: 120 },
    performance: {
      optimizationMode: "normal",
      preciseGpuPlacementEnabled: false,
      gpuOverflowUsagePercent: 95,
      gpuOverflowDedicatedMemoryPercent: 80,
      physicalMemoryAutomaticCleanupPercent: 6,
      virtualMemoryAutomaticCleanupPercent: 8,
      physicalMemoryOptimizationTargetUsagePercent: 70,
      virtualMemoryOptimizationTargetUsagePercent: 70,
      gpuPerformanceUseCases: ["general"],
      smartMonitoringMode: "enabled",
      monitorRefreshIntervalMs: defaultLogicRefreshIntervalSetting("monitor"),
      resourceTableRefreshIntervalMs: defaultLogicRefreshIntervalSetting("resourceTable"),
      localSystemRefreshIntervalMs: defaultLogicRefreshIntervalSetting("localSystem")
    },
    appearance: {
      uiStyle: "standard",
      theme: "system",
      animations: "auto",
      resourceBarFrameRateMode: "display",
      resourceBarFrameRate: resourceBarFrameRateDefault,
      barColorMode: "type",
      fontSmoothing: "auto",
      language: "system",
      readingSourceMode: "preferred"
    },
    systemIntegration: {
      autoStartEnabled: false,
      taskManagerShortcutReplacementEnabled: false,
      hotkeys: normalizeEditableHotkeys([])
    },
    debug: {
      debugModeEnabled: false,
      debugLogEnabled: false,
      hostManagerSmartCoordinatorScoreOnlyEnabled: false,
      hostManagerSmartCoordinatorPerformanceLogEnabled: false
    },
    publicService: {
      enabled: false,
      fileIndexEnabled: false,
      databaseServiceEnabled: false,
      aiModelCatalogEnabled: false
    },
    aiModelService: {
      provider: "lm-studio",
      endpoint: "http://127.0.0.1:1234",
      autoStartEnabled: false
    },
    update: { autoUpdateEnabled: false }
  };
}

export function normalizeAppSettings(settings?: AppSettings | null): AppSettings {
  const defaults = defaultAppSettings();
  return {
    version: "1.0.28",
    control: {
      reassertionIntervalSeconds: normalizeInteger(settings?.control?.reassertionIntervalSeconds, 1, 86400, defaults.control!.reassertionIntervalSeconds)
    },
    // 只列出当前模型里的字段：旧文件里已删除的键（旧布尔副本、隐藏时刷新、三项无读取方的刷新频率等）在这里自然丢弃。
    performance: {
      optimizationMode: normalizeOptimizationMode(settings?.performance?.optimizationMode),
      preciseGpuPlacementEnabled: settings?.performance?.preciseGpuPlacementEnabled ?? defaults.performance!.preciseGpuPlacementEnabled,
      gpuOverflowUsagePercent: normalizeGpuOverflowPercent(settings?.performance?.gpuOverflowUsagePercent, defaults.performance!.gpuOverflowUsagePercent),
      gpuOverflowDedicatedMemoryPercent: normalizeGpuOverflowPercent(settings?.performance?.gpuOverflowDedicatedMemoryPercent, defaults.performance!.gpuOverflowDedicatedMemoryPercent),
      physicalMemoryAutomaticCleanupPercent: normalizeDangerPercent(settings?.performance?.physicalMemoryAutomaticCleanupPercent, defaults.performance!.physicalMemoryAutomaticCleanupPercent),
      virtualMemoryAutomaticCleanupPercent: normalizeDangerPercent(settings?.performance?.virtualMemoryAutomaticCleanupPercent, defaults.performance!.virtualMemoryAutomaticCleanupPercent),
      physicalMemoryOptimizationTargetUsagePercent: normalizeTargetUsagePercent(settings?.performance?.physicalMemoryOptimizationTargetUsagePercent, defaults.performance!.physicalMemoryOptimizationTargetUsagePercent),
      virtualMemoryOptimizationTargetUsagePercent: normalizeTargetUsagePercent(settings?.performance?.virtualMemoryOptimizationTargetUsagePercent, defaults.performance!.virtualMemoryOptimizationTargetUsagePercent),
      gpuPerformanceUseCases: normalizeGpuPerformanceUseCases(settings?.performance?.gpuPerformanceUseCases),
      smartMonitoringMode: normalizeAdaptiveBooleanMode(settings?.performance?.smartMonitoringMode),
      monitorRefreshIntervalMs: normalizeLogicRefreshIntervalSetting("monitor", settings?.performance?.monitorRefreshIntervalMs),
      resourceTableRefreshIntervalMs: normalizeLogicRefreshIntervalSetting("resourceTable", settings?.performance?.resourceTableRefreshIntervalMs),
      localSystemRefreshIntervalMs: normalizeLogicRefreshIntervalSetting("localSystem", settings?.performance?.localSystemRefreshIntervalMs)
    },
    appearance: normalizeAppearanceSettings(settings?.appearance),
    systemIntegration: {
      autoStartEnabled: settings?.systemIntegration?.autoStartEnabled === true,
      taskManagerShortcutReplacementEnabled: settings?.systemIntegration?.taskManagerShortcutReplacementEnabled === true,
      hotkeys: normalizeEditableHotkeys(settings?.systemIntegration?.hotkeys)
    },
    debug: normalizeDebugSettings(settings?.debug),
    publicService: {
      enabled: settings?.publicService?.enabled ?? defaults.publicService!.enabled,
      fileIndexEnabled: settings?.publicService?.fileIndexEnabled ?? defaults.publicService!.fileIndexEnabled,
      databaseServiceEnabled: settings?.publicService?.databaseServiceEnabled ?? defaults.publicService!.databaseServiceEnabled,
      aiModelCatalogEnabled: settings?.publicService?.aiModelCatalogEnabled ?? defaults.publicService!.aiModelCatalogEnabled
    },
    aiModelService: {
      provider: "lm-studio",
      endpoint: normalizeLmStudioEndpoint(settings?.aiModelService?.endpoint),
      autoStartEnabled: settings?.aiModelService?.autoStartEnabled ?? defaults.aiModelService!.autoStartEnabled
    },
    update: { autoUpdateEnabled: settings?.update?.autoUpdateEnabled === true }
  };
}

function normalizeLmStudioEndpoint(endpoint?: string | null): string {
  const fallback = "http://127.0.0.1:1234";
  try {
    const parsed = new URL(endpoint?.trim() || fallback);
    const isLoopback = parsed.hostname === "127.0.0.1"
      || parsed.hostname === "localhost"
      || parsed.hostname === "[::1]"
      || parsed.hostname === "::1";
    return parsed.protocol === "http:" && isLoopback ? parsed.origin : fallback;
  } catch {
    return fallback;
  }
}

export function defaultLogicRefreshIntervalSetting(key: AppLogicRefreshIntervalKey): AppPresetNumericSetting {
  const preset = defaultLogicRefreshIntervalPreset[key];
  return {
    mode: "aotu",
    preset,
    customValue: logicRefreshIntervalPresetValues[key][preset]
  };
}

export function normalizeLogicRefreshIntervalSetting(
  key: AppLogicRefreshIntervalKey,
  setting?: AppPresetNumericSetting | null
): AppPresetNumericSetting {
  const fallback = defaultLogicRefreshIntervalSetting(key);
  return {
    mode: normalizePresetNumericSettingMode(setting?.mode),
    preset: normalizeLogicRefreshIntervalPreset(key, setting?.preset, fallback.preset),
    customValue: normalizeLogicRefreshIntervalMs(setting?.customValue, fallback.customValue)
  };
}

export function resolveLogicRefreshIntervalMs(
  key: AppLogicRefreshIntervalKey,
  setting?: AppPresetNumericSetting | null,
  selfCpuGrade?: ResourceManagerSelfCpuGrade | null,
  foregroundInteractive = true
) {
  const normalized = normalizeLogicRefreshIntervalSetting(key, setting);
  if (normalized.mode === "custom") {
    return normalized.customValue;
  }

  if (normalized.mode === "preset") {
    return logicRefreshIntervalPresetValues[key][normalized.preset];
  }

  const preset = selfCpuGrade === "optimize" && !foregroundInteractive
    ? "lowPower"
    : "responsive";
  return logicRefreshIntervalPresetValues[key][preset];
}

export function normalizePresetNumericSettingMode(mode?: string | null): AppPresetNumericSettingMode {
  if (mode === "preset" || mode === "custom") {
    return mode;
  }

  return "aotu";
}

export function normalizeLogicRefreshIntervalPreset(
  key: AppLogicRefreshIntervalKey,
  preset?: string | null,
  fallback: AppLogicRefreshIntervalPreset = defaultLogicRefreshIntervalPreset[key]
): AppLogicRefreshIntervalPreset {
  const supported = logicRefreshIntervalPresetValues[key];
  if (preset === "responsive" || preset === "balanced" || preset === "lowPower" || preset === "quiet") {
    return preset in supported ? preset : fallback;
  }

  return fallback;
}

export function formatLogicRefreshIntervalPresetMs(
  key: AppLogicRefreshIntervalKey,
  preset: AppLogicRefreshIntervalPreset
) {
  return logicRefreshIntervalPresetValues[key][preset];
}

export function normalizeDebugSettings(settings?: AppSettings["debug"] | null) {
  const debugModeEnabled = settings?.debugModeEnabled === true;
  return {
    debugModeEnabled,
    debugLogEnabled: settings?.debugLogEnabled === true,
    hostManagerSmartCoordinatorScoreOnlyEnabled: settings?.hostManagerSmartCoordinatorScoreOnlyEnabled === true,
    hostManagerSmartCoordinatorPerformanceLogEnabled: settings?.hostManagerSmartCoordinatorPerformanceLogEnabled === true
  };
}

function normalizeInteger(value: number | null | undefined, min: number, max: number, fallback: number) {
  const number = Number(value);
  if (!Number.isFinite(number)) {
    return fallback;
  }

  return Math.round(Math.min(max, Math.max(min, number)));
}

function normalizeLogicRefreshIntervalMs(value: number | null | undefined, fallback: number) {
  return normalizeInteger(value, minLogicRefreshIntervalMs, maxLogicRefreshIntervalMs, fallback);
}

function assignLogicRefreshInterval(
  performance: AppPerformanceSettings,
  key: AppLogicRefreshIntervalKey,
  setting: AppPresetNumericSetting
): AppPerformanceSettings {
  switch (key) {
    case "monitor":
      return { ...performance, monitorRefreshIntervalMs: setting };
    case "resourceTable":
      return { ...performance, resourceTableRefreshIntervalMs: setting };
    case "localSystem":
      return { ...performance, localSystemRefreshIntervalMs: setting };
  }

  return performance;
}


export function normalizeAnimationMode(animations?: string | null): AppAnimationMode {
  if (animations === "auto") {
    return "auto";
  }

  if (animations === "none") {
    return "none";
  }

  if (animations === "ultra") {
    return "ultra";
  }

  return "normal";
}

// 只有显式 disabled 是关；旧值 auto、缺省和认不出的值都读成开（与后端归一化一致）。
export function normalizeAdaptiveBooleanMode(mode?: string | null): AppAdaptiveBooleanMode {
  return mode === "disabled" ? "disabled" : "enabled";
}

export function resolveEffectiveAnimationMode(
  animations?: string | null,
  selfGpuGrade?: ResourceManagerSelfGpuGrade | null,
  frontendFocused = true
): Exclude<AppAnimationMode, "auto"> {
  const mode = normalizeAnimationMode(animations);
  if (mode !== "auto") {
    return mode;
  }

  return selfGpuGrade === "optimize" && !frontendFocused ? "ultra" : "normal";
}

export function normalizeBarColorMode(barColorMode?: string | null): AppBarColorMode {
  return barColorMode === "distinct" ? "distinct" : "type";
}

export function normalizeFontSmoothing(fontSmoothing?: string | null): AppFontSmoothing {
  if (fontSmoothing === "system" || fontSmoothing === "grayscale" || fontSmoothing === "disabled") {
    return fontSmoothing;
  }

  return "auto";
}

export function resolveEffectiveFontSmoothing(
  fontSmoothing: string | null | undefined,
  animations: Exclude<AppAnimationMode, "auto">
): Exclude<AppFontSmoothing, "auto"> {
  const mode = normalizeFontSmoothing(fontSmoothing);
  if (mode !== "auto") {
    return mode;
  }

  return animations === "ultra" ? "disabled" : "system";
}

export function normalizeOptimizationMode(mode?: string | null): AppOptimizationMode {
  return normalizeOptimizationModeValue(mode);
}

export function normalizeGpuPerformanceUseCases(useCases?: readonly string[] | null): AppGpuPerformanceUseCase[] {
  const supported = new Set<AppGpuPerformanceUseCase>(["general", "ai", "gaming"]);
  const normalized = (useCases ?? [])
    .filter((useCase): useCase is AppGpuPerformanceUseCase =>
      typeof useCase === "string" && supported.has(useCase as AppGpuPerformanceUseCase))
    .filter((useCase, index, values) => values.indexOf(useCase) === index);
  return normalized.length > 0 ? normalized : ["general"];
}

function syncShellSettings(settings: AppSettings) {
  postShellMessage("editableHotkeys:reload");
  postShellMessage(settings.systemIntegration?.taskManagerShortcutReplacementEnabled === true
    ? "taskManager.shortcutReplacement:on"
    : "taskManager.shortcutReplacement:off");
}

function normalizeAppearanceSettings(
  settings?: AppAppearanceSettings | null
): AppAppearanceSettings {
  return {
    ...normalizeUiAppearance(settings?.uiStyle, settings?.theme),
    animations: normalizeAnimationMode(settings?.animations),
    resourceBarFrameRateMode: normalizeResourceBarFrameRateMode(settings?.resourceBarFrameRateMode),
    resourceBarFrameRate: normalizeResourceBarFrameRate(settings?.resourceBarFrameRate),
    barColorMode: normalizeBarColorMode(settings?.barColorMode),
    byteUnitMode: normalizeByteUnitMode(settings?.byteUnitMode),
    fontSmoothing: normalizeFontSmoothing(settings?.fontSmoothing),
    language: normalizeLanguageMode(settings?.language),
    readingSourceMode: normalizeReadingSourceMode(settings?.readingSourceMode)
  };
}

function normalizeGpuOverflowPercent(value: number | null | undefined, fallback: number) {
  return normalizeInteger(value, 1, 100, fallback);
}

function normalizeDangerPercent(value: number | null | undefined, fallback: number) {
  return normalizeInteger(value, 0, 95, fallback);
}

function normalizeTargetUsagePercent(value: number | null | undefined, fallback: number) {
  return normalizeInteger(value, 5, 95, fallback);
}

function areSettingsEqual(left: AppSettings, right: AppSettings) {
  const normalizedLeft = normalizeAppSettings(left);
  const normalizedRight = alignRuntimeOwnedSettings(
    normalizeAppSettings(right),
    normalizedLeft);
  return JSON.stringify(normalizedLeft) === JSON.stringify(normalizedRight);
}

function alignRuntimeOwnedSettings(
  candidate: AppSettings,
  runtimeSource: AppSettings
): AppSettings {
  const normalizedCandidate = normalizeAppSettings(candidate);
  const normalizedRuntimeSource = normalizeAppSettings(runtimeSource);
  return {
    ...normalizedCandidate,
    performance: {
      ...normalizedCandidate.performance!,
      optimizationMode: normalizedRuntimeSource.performance!.optimizationMode
    }
  };
}

function syncPrebootAppearance(settings: AppSettings) {
  try {
    localStorage.setItem(prebootAppearanceStorageKey, JSON.stringify(
      normalizeUiAppearance(settings.appearance?.uiStyle, settings.appearance?.theme)));
  } catch {
    // Preboot theming is a visual fallback only; settings still live in the backend.
  }
}
