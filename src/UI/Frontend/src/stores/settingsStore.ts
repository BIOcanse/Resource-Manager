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
  AppAppearanceSettings,
  AppEditableHotkeySettings,
  AppBarColorMode,
  ByteUnitMode,
  AppFontSmoothing,
  FrontendRefreshIntervals,
  AppLanguageMode,
  AppLogicRefreshIntervalKey,
  AppLogicRefreshIntervalPreset,
  AppLocalPublicServiceSettings,
  AppOptimizationMode,
  AppPerformanceSettings,
  AppPresetNumericSetting,
  ResourceManagerSelfGpuGrade,
  AppPresetNumericSettingMode,
  AppSettings,
  AppSystemIntegrationSettings,
  ServiceRecoveryDiagnostics,
  AppPalette,
  AppReadingSourceMode,
  AppResourceBarFrameRateMode,
  AppUiStyle,
  AppUpdateMode,
  AppNotificationSettings,
  SettingsSection
} from "../types";
import { normalizeUiAppearance } from "../presentation/uiStyles.ts";
import { motionFeel } from "../presentation/motionFeel.ts";
import type { MotionFeelId } from "../presentation/motionFeel.ts";
import {
  normalizeResourceBarFrameRate,
  normalizeResourceBarFrameRateMode
} from "../presentation/resourceBarFrameRate.ts";
import { postShellMessage } from "../utils.ts";
import { normalizeEditableHotkeys } from "../features/settings/editableHotkeys.ts";
import { normalizeOptimizationModeValue } from "../features/settings/optimizationMode.ts";
import { applySettingsPatch, createSettingsPatch } from "../features/settings/settingsPatch.ts";
import { uiText } from "../text.ts";
import { normalizeReportSettings } from "../features/settings/reportRuleSettingsModel.ts";
import { RequestProblem } from "../frontendRuntime/request/RequestProblem.ts";
import { ResourceManager } from "../preferences/generated/frontendPoints.values.ts";
import {
  applyFrontendPreferences, backendSettings, frontendPreferencesClient, pointPreferences,
  preferencesFromSettings, resolveFrontendRefresh, type FrontendNotifications, type FrontendPreferencesCache, type FrontendPreferencesClient
} from "../preferences/frontendPreferences.ts";

export const logicRefreshIntervalKeys: AppLogicRefreshIntervalKey[] = [
  "monitor",
  "resourceTable",
  "localSystem"
];
const defaultLogicRefreshIntervalPreset: Record<AppLogicRefreshIntervalKey, AppLogicRefreshIntervalPreset> = {
  monitor: ResourceManager.FrontendPoints.Monitor.preset,
  resourceTable: ResourceManager.FrontendPoints.ResourceTable.preset,
  localSystem: ResourceManager.FrontendPoints.LocalSystem.preset
};
const minLogicRefreshIntervalMs = 500;
const maxLogicRefreshIntervalMs = 300000;

export interface SettingsStore {
  settings: Accessor<AppSettings>;
  frontendRefreshIntervals: Accessor<FrontendRefreshIntervals | null>;
  draftSettings: Accessor<AppSettings>;
  isDirty: Accessor<boolean>;
  loadState: Accessor<"loading" | "refreshing" | "ready" | "stale" | "error">;
  saveState: Accessor<"idle" | "saving" | "saved" | "constrained" | "partial" | "error" | "conflict">;
  runtimeCapabilityConstrainedPaths: Accessor<readonly string[]>;
  runtimeFailureCode: Accessor<string | null>;
  serviceRecovery: Accessor<ServiceRecoveryDiagnostics | undefined>;
  activeSection: Accessor<SettingsSection>;
  setActiveSection: Setter<SettingsSection>;
  refresh: () => Promise<void>;
  saveDraft: () => Promise<void>;
  reapply: () => Promise<void>;
  discardDraftChanges: () => void;
  resetDraftToDefaults: () => void;
  resetFrontendPreferences: () => Promise<void>;
  updateReports: (reports: NonNullable<AppSettings["reports"]>) => void;
  updateControl: (control: NonNullable<AppSettings["control"]>) => void;
  updatePreciseGpuPlacement: (enabled: boolean) => void;
  updateAutomaticMemoryCleanupLines: (physicalMemoryPercent: number, virtualMemoryPercent: number) => void;
  updateMemoryOptimizationTargetLines: (physicalMemoryPercent: number, virtualMemoryPercent: number) => void;
  updateGpuOverflowThresholds: (gpuUsagePercent: number, dedicatedMemoryPercent: number) => void;
  updateRealtimeActionBudget: (mode: AppPerformanceSettings["realtimeActionBudgetMode"], count: number, percent: number) => void;
  updateLogicRefreshInterval: (key: AppLogicRefreshIntervalKey, setting: AppPresetNumericSetting) => void;
  updateUiStyle: (uiStyle: AppUiStyle) => void;
  updateTheme: (theme: AppPalette) => void;
  updateAnimations: (animations: AppAnimationMode) => void;
  updateMotionFeel: (feel: MotionFeelId) => void;
  updateResourceBarFrameRateMode: (mode: AppResourceBarFrameRateMode) => void;
  updateResourceBarFrameRate: (framesPerSecond: number) => void;
  updateBarColorMode: (barColorMode: AppBarColorMode) => void;
  updateByteUnitMode: (byteUnitMode: ByteUnitMode) => void;
  updateReadingSourceMode: (mode: AppReadingSourceMode) => void;
  updateFontSmoothing: (fontSmoothing: AppFontSmoothing) => void;
  updateLanguage: (language: AppLanguageMode) => void;
  updateTaskManagerShortcutReplacement: (enabled: boolean) => void;
  updateAutoStart: (enabled: boolean) => void;
  updateServiceRecovery: (enabled: boolean, delay: number, count: number) => void;
  updateUpdateMode: (mode: AppUpdateMode) => void;
  updatePeriodicUpdateCheck: (enabled: boolean) => void;
  updateCheckInterval: (hours: number) => void;
  updateNotificationSwitch: (path: keyof AppNotificationSettings, enabled: boolean) => void;
  /** 偏好缓存里已保存的提示分区（开关与界面内提示已提示过的版本）；缓存读到之前为 undefined。 */
  frontendNotifications: Accessor<FrontendNotifications | undefined>;
  recordUpdateInAppNotified: (version: string) => Promise<void>;
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
  readonly preferences?: FrontendPreferencesClient;
}

type SourceProjectionMode = "replace-draft" | "preserve-draft";

export function createSettingsStore(options: SettingsStoreOptions): SettingsStore {
  const preferenceOwner = options.preferences ?? frontendPreferencesClient();
  let preferences = pointPreferences();
  let preferencesReady = false;
  // 偏好缓存的写入按顺序执行，每次都从上一次写入的结果出发，
  // 设置页的保存和界面内提示记下的版本不会互相用旧值覆盖。
  let pendingPreferenceWrite: Promise<unknown> | null = null;
  const [frontendNotifications, setFrontendNotifications] = createSignal<FrontendNotifications | undefined>(undefined);
  function writePreferences(build: (current: FrontendPreferencesCache) => FrontendPreferencesCache) {
    const run = async () => {
      preferences = await preferenceOwner.save(build(preferences), "global");
      setFrontendNotifications(preferences.notifications);
      return preferences;
    };
    // 没有进行中的写入时立即写；有时排在它之后。
    const write = pendingPreferenceWrite ? pendingPreferenceWrite.then(run, run) : run();
    const settled = write.catch(() => undefined);
    pendingPreferenceWrite = settled;
    void settled.then(() => { if (pendingPreferenceWrite === settled) pendingPreferenceWrite = null; });
    return write;
  }
  const [settings, setSettings] = createSignal<AppSettings>(defaultAppSettings());
  const [draftSettings, setDraftSettings] = createSignal<AppSettings>(defaultAppSettings());
  const [frontendRefreshIntervals, setFrontendRefreshIntervals] = createSignal<FrontendRefreshIntervals | null>(null);
  const [revision, setRevision] = createSignal<string | null>(null);
  const [loadState, setLoadState] = createSignal<"loading" | "refreshing" | "ready" | "stale" | "error">("loading");
  const [saveState, setSaveState] = createSignal<"idle" | "saving" | "saved" | "constrained" | "partial" | "error" | "conflict">("idle");
  const [runtimeCapabilityConstrainedPaths, setRuntimeCapabilityConstrainedPaths] = createSignal<readonly string[]>([]);
  const [runtimeFailureCode, setRuntimeFailureCode] = createSignal<string | null>(null);
  const [serviceRecovery, setServiceRecovery] = createSignal<ServiceRecoveryDiagnostics>();
  const [activeSection, setActiveSection] = createSignal<SettingsSection>("performance");
  const isDirty = createMemo(() => revision() !== null
    && !areSettingsEqual(settings(), draftSettings()));
  const source = options.source.acquire({ active: false, refreshIntervalMs: null });
  let lastProjectedSourceRevision = -1;
  let nextProjectionMode: SourceProjectionMode | null = null;
  const unsubscribeSource = source.subscribe((snapshot) => projectSource(snapshot));
  void preferenceOwner.read().then(value => {
    preferences = value;
    preferencesReady = true;
    setFrontendNotifications(value.notifications);
    setFrontendRefreshIntervals(resolveFrontendRefresh(value.refresh));
    projectSource(source.snapshot);
  }).catch(() => setLoadState("error"));
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
    setRuntimeFailureCode(null);
  }

  async function saveDraft() {
    const expectedRevision = revision();
    if (loadState() !== "ready" || !expectedRevision || saveState() === "conflict" || saveState() === "saving") {
      return;
    }
    setRuntimeFailureCode(null);
    const submittedDraft = draftSettings();
    const next = alignRuntimeOwnedSettings(
      normalizeAppSettings(submittedDraft),
      settings());
    const changes = createSettingsPatch(
      backendSettings(settings()), backendSettings(next));
    const desiredPreferences = preferencesFromSettings(next, preferences);
    const preferenceChanges = JSON.stringify(preferences) !== JSON.stringify(desiredPreferences);
    if (Object.keys(changes).length === 0 && !preferenceChanges) {
      setSaveState("idle");
      return;
    }
    setSaveState("saving");
    setRuntimeCapabilityConstrainedPaths([]);
    try {
      if (Object.keys(changes).length === 0) {
        await writePreferences(current => preferencesFromSettings(next, current));
        const newerEdits = createSettingsPatch(
          submittedDraft as unknown as Record<string, unknown>,
          draftSettings() as unknown as Record<string, unknown>);
        batch(() => {
          setSettings(applyFrontendPreferences(settings(), preferences));
          setDraftSettings(applySettingsPatch(settings(), newerEdits));
          setFrontendRefreshIntervals(resolveFrontendRefresh(preferences.refresh));
        });
        setSaveState(isDirty() ? "idle" : "saved");
        return;
      }
      const result = await saveAppSettingsPatch(
        options.requestClient,
        expectedRevision,
        changes);
      const outcome = classifySettingsApplicationOutcome(result.runtimeApplicationDisposition);
      if (outcome === "rejected") {
        setRuntimeFailureCode(result.runtimeFailureCode ?? null);
        throw new Error(uiText.misc.settingsNotCommitted);
      }
      if (preferenceChanges) await writePreferences(current => preferencesFromSettings(next, current));
      const newerEdits = createSettingsPatch(
        submittedDraft as unknown as Record<string, unknown>,
        draftSettings() as unknown as Record<string, unknown>);
      batch(() => {
        acceptCommitted(result, "replace-draft");
        setDraftSettings(alignRuntimeOwnedSettings(
          applySettingsPatch(settings(), newerEdits), settings()));
      });
      setRuntimeCapabilityConstrainedPaths(result.runtimeCapabilityConstrainedPaths ?? []);
      setSaveState(outcome === "published"
        ? isDirty() ? "idle" : "saved"
        : outcome === "capability-constrained"
          ? "constrained"
          : "partial");
      if (outcome === "published") {
        window.setTimeout(() => setSaveState((state) => state === "saved" ? "idle" : state), 1600);
      }
    } catch (error) {
      if (error instanceof AppSettingsRevisionConflictError) {
        acceptCommitted(error.result, "preserve-draft");
        setSaveState("conflict");
        return;
      }
      if (error instanceof RequestProblem && error.status === 409) {
        const payload = error.payload as { runtimeApplicationDisposition?: unknown; runtimeFailureCode?: unknown } | null;
        if (payload?.runtimeApplicationDisposition === "rejectedBeforeCommit"
          && typeof payload.runtimeFailureCode === "string") setRuntimeFailureCode(payload.runtimeFailureCode);
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
      setSaveState(outcome === "published"
        ? isDirty() ? "idle" : "saved"
        : outcome === "capability-constrained"
          ? "constrained"
          : outcome === "persisted-pending"
            ? "partial"
            : "error");
      if (outcome === "published") {
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
    if (!preferencesReady) { setLoadState("loading"); return; }
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
    const loaded = normalizeAppSettings(applyFrontendPreferences(snapshot.data.settings, preferences));
    const nextDraft = mode === "replace-draft"
      ? loaded
      : alignRuntimeOwnedSettings(applySettingsPatch(loaded, createSettingsPatch(
          settings() as unknown as Record<string, unknown>,
          draftSettings() as unknown as Record<string, unknown>)), loaded);
    setFrontendRefreshIntervals(resolveFrontendRefresh(preferences.refresh));
    setSettings(loaded);
    setDraftSettings(nextDraft);
    setRevision(snapshot.data.revision);
    syncShellSettings(loaded);
    lastProjectedSourceRevision = snapshot.revision;
    nextProjectionMode = null;
    const outcome = classifySettingsApplicationOutcome(snapshot.data.runtimeApplicationDisposition);
    setSaveState(outcome === "capability-constrained" ? "constrained"
      : outcome === "persisted-pending" ? "partial" : "idle");
    setRuntimeCapabilityConstrainedPaths(snapshot.data.runtimeCapabilityConstrainedPaths ?? []);
    setRuntimeFailureCode(snapshot.data.runtimeFailureCode ?? null);
    setServiceRecovery(snapshot.data.serviceRecovery);
    setLoadState(nextLoadState);
  }

  function resetDraftToDefaults() {
    if (loadState() !== "ready" || saveState() === "conflict") {
      return;
    }
    const defaults = defaultAppSettings();
    const published = frontendRefreshIntervals();
    if (!published) return;
    defaults.performance!.monitorRefreshIntervalMs = structuredClone(published.defaults.monitor);
    defaults.performance!.resourceTableRefreshIntervalMs = structuredClone(published.defaults.resourceTable);
    defaults.performance!.localSystemRefreshIntervalMs = structuredClone(published.defaults.localSystem);
    setDraftSettings(defaults);
    setSaveState((state) => state === "saved" || state === "constrained" ? "idle" : state);
    setRuntimeCapabilityConstrainedPaths([]);
    setRuntimeFailureCode(null);
  }

  async function resetFrontendPreferences() {
    if (saveState() === "saving") return;
    setSaveState("saving");
    try {
      preferences = await preferenceOwner.reset();
      setFrontendNotifications(preferences.notifications);
      batch(() => {
        setSettings(applyFrontendPreferences(settings(), preferences));
        setDraftSettings(applyFrontendPreferences(draftSettings(), preferences));
        setFrontendRefreshIntervals(resolveFrontendRefresh(preferences.refresh));
      });
      setSaveState(isDirty() ? "idle" : "saved");
    } catch { setSaveState("error"); }
  }

  function discardDraftChanges() {
    setDraftSettings(normalizeAppSettings(settings()));
    setSaveState((state) => state === "saving" ? state : "idle");
    setRuntimeCapabilityConstrainedPaths([]);
    setRuntimeFailureCode(null);
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
    resetFrontendPreferences,
    frontendRefreshIntervals,
    draftSettings,
    isDirty,
    loadState,
    saveState,
    runtimeCapabilityConstrainedPaths,
    runtimeFailureCode,
    serviceRecovery,
    activeSection,
    setActiveSection,
    frontendNotifications,
    recordUpdateInAppNotified: async (version) => {
      await writePreferences(current => ({ ...current,
        notifications: { ...current.notifications, updateInAppNotifiedVersion: version } }));
    },
    refresh,
    saveDraft,
    reapply,
    discardDraftChanges,
    resetDraftToDefaults,
    updateReports: (reports) => updateDraft(current => ({ ...current, reports })),
    updateControl: (control) => updateDraft(current => ({ ...current, control })),
    updatePreciseGpuPlacement: (enabled) => updatePerformance((performance) => ({
      ...performance,
      preciseGpuPlacementEnabled: enabled
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
    updateRealtimeActionBudget: (mode, count, percent) => updatePerformance((performance) => ({
      ...performance,
      realtimeActionBudgetMode: mode === "percent" ? "percent" : "fixed",
      realtimeActionBudgetCount: normalizeInteger(count, 1, 8192, performance.realtimeActionBudgetCount),
      realtimeActionBudgetPercent: normalizeInteger(percent, 1, 100, performance.realtimeActionBudgetPercent)
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
    updateMotionFeel: (feel) => {
      updateDraft((currentSettings) => ({
        ...currentSettings,
        appearance: {
          ...normalizeAppSettings(currentSettings).appearance!,
          motionFeel: motionFeel(feel).id
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
    updateServiceRecovery: (enabled, delay, count) => updateSystemIntegration((current) => ({
      ...current,
      restartAfterCrashEnabled: enabled,
      crashRestartDelaySeconds: normalizeInteger(delay, 1, 3600, current.crashRestartDelaySeconds ?? 10),
      crashRestartCount: normalizeInteger(count, 1, 10, current.crashRestartCount ?? 3)
    })),
    updateAutoStart: (enabled) => updateSystemIntegration((systemIntegration) => ({
      ...systemIntegration,
      autoStartEnabled: enabled
    })),
    updateUpdateMode: (mode) => updateDraft((currentSettings) => ({
      ...currentSettings,
      update: { ...currentSettings.update!, mode }
    })),
    updatePeriodicUpdateCheck: (enabled) => updateDraft((currentSettings) => ({
      ...currentSettings,
      update: { ...currentSettings.update!, periodicCheckEnabled: enabled }
    })),
    updateCheckInterval: (hours) => updateDraft((currentSettings) => ({
      ...currentSettings,
      update: { ...currentSettings.update!, checkIntervalHours: normalizeInteger(hours,
        updateCheckIntervalHours.minimum, updateCheckIntervalHours.maximum, updateCheckIntervalHours.defaultValue) }
    })),
    updateNotificationSwitch: (path, enabled) => updateDraft((currentSettings) => ({
      ...currentSettings,
      notifications: { ...normalizeAppSettings(currentSettings).notifications!, [path]: enabled ? "on" : "off" }
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
    version: "1.0.33",
    control: { reassertionIntervalSeconds: 120 },
    reports: { ruleOverrides: [] },
    performance: {
      optimizationMode: "normal",
      preciseGpuPlacementEnabled: false,
      gpuOverflowUsagePercent: 95,
      gpuOverflowDedicatedMemoryPercent: 80,
      realtimeActionBudgetMode: "fixed",
      realtimeActionBudgetCount: 32,
      realtimeActionBudgetPercent: 5,
      physicalMemoryAutomaticCleanupPercent: 6,
      virtualMemoryAutomaticCleanupPercent: 8,
      physicalMemoryOptimizationTargetUsagePercent: 70,
      virtualMemoryOptimizationTargetUsagePercent: 70,
      monitorRefreshIntervalMs: defaultLogicRefreshIntervalSetting("monitor"),
      resourceTableRefreshIntervalMs: defaultLogicRefreshIntervalSetting("resourceTable"),
      localSystemRefreshIntervalMs: defaultLogicRefreshIntervalSetting("localSystem")
    },
    appearance: pointPreferences().appearance,
    notifications: notificationSwitches(pointPreferences().notifications),
    systemIntegration: {
      restartAfterCrashEnabled: true,
      crashRestartDelaySeconds: 10,
      crashRestartCount: 3,
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
    update: { mode: "automatic", periodicCheckEnabled: false, checkIntervalHours: updateCheckIntervalHours.defaultValue }
  };
}

export function normalizeAppSettings(settings?: AppSettings | null): AppSettings {
  const defaults = defaultAppSettings();
  return {
    version: "1.0.33",
    control: {
      reassertionIntervalSeconds: normalizeInteger(settings?.control?.reassertionIntervalSeconds, 1, 86400, defaults.control!.reassertionIntervalSeconds)
    },
    reports: normalizeReportSettings(settings?.reports),
    // 只列出当前模型里的字段：旧文件里已删除的键（旧布尔副本、隐藏时刷新、三项无读取方的刷新频率等）在这里自然丢弃。
    performance: {
      optimizationMode: normalizeOptimizationMode(settings?.performance?.optimizationMode),
      preciseGpuPlacementEnabled: settings?.performance?.preciseGpuPlacementEnabled ?? defaults.performance!.preciseGpuPlacementEnabled,
      gpuOverflowUsagePercent: normalizeGpuOverflowPercent(settings?.performance?.gpuOverflowUsagePercent, defaults.performance!.gpuOverflowUsagePercent),
      gpuOverflowDedicatedMemoryPercent: normalizeGpuOverflowPercent(settings?.performance?.gpuOverflowDedicatedMemoryPercent, defaults.performance!.gpuOverflowDedicatedMemoryPercent),
      realtimeActionBudgetMode: settings?.performance?.realtimeActionBudgetMode === "percent" ? "percent" : "fixed",
      realtimeActionBudgetCount: normalizeInteger(settings?.performance?.realtimeActionBudgetCount, 1, 8192, defaults.performance!.realtimeActionBudgetCount),
      realtimeActionBudgetPercent: normalizeInteger(settings?.performance?.realtimeActionBudgetPercent, 1, 100, defaults.performance!.realtimeActionBudgetPercent),
      physicalMemoryAutomaticCleanupPercent: normalizeDangerPercent(settings?.performance?.physicalMemoryAutomaticCleanupPercent, defaults.performance!.physicalMemoryAutomaticCleanupPercent),
      virtualMemoryAutomaticCleanupPercent: normalizeDangerPercent(settings?.performance?.virtualMemoryAutomaticCleanupPercent, defaults.performance!.virtualMemoryAutomaticCleanupPercent),
      physicalMemoryOptimizationTargetUsagePercent: normalizeTargetUsagePercent(settings?.performance?.physicalMemoryOptimizationTargetUsagePercent, defaults.performance!.physicalMemoryOptimizationTargetUsagePercent),
      virtualMemoryOptimizationTargetUsagePercent: normalizeTargetUsagePercent(settings?.performance?.virtualMemoryOptimizationTargetUsagePercent, defaults.performance!.virtualMemoryOptimizationTargetUsagePercent),
      monitorRefreshIntervalMs: normalizeLogicRefreshIntervalSetting("monitor", settings?.performance?.monitorRefreshIntervalMs),
      resourceTableRefreshIntervalMs: normalizeLogicRefreshIntervalSetting("resourceTable", settings?.performance?.resourceTableRefreshIntervalMs),
      localSystemRefreshIntervalMs: normalizeLogicRefreshIntervalSetting("localSystem", settings?.performance?.localSystemRefreshIntervalMs)
    },
    appearance: normalizeAppearanceSettings(settings?.appearance),
    notifications: notificationSwitches(settings?.notifications ?? defaults.notifications!),
    systemIntegration: {
      restartAfterCrashEnabled: settings?.systemIntegration?.restartAfterCrashEnabled !== false,
      crashRestartDelaySeconds: normalizeInteger(settings?.systemIntegration?.crashRestartDelaySeconds, 1, 3600, 10),
      crashRestartCount: normalizeInteger(settings?.systemIntegration?.crashRestartCount, 1, 10, 3),
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
    update: {
      mode: updateModes.includes(settings?.update?.mode as AppUpdateMode) ? settings!.update!.mode : defaults.update!.mode,
      periodicCheckEnabled: settings?.update?.periodicCheckEnabled === true,
      checkIntervalHours: normalizeInteger(settings?.update?.checkIntervalHours,
        updateCheckIntervalHours.minimum, updateCheckIntervalHours.maximum, updateCheckIntervalHours.defaultValue)
    }
  };
}

const updateModes: readonly AppUpdateMode[] = ["off", "notify", "automatic"];
/** 后端限制的自动检查间隔（小时）；设置页的输入框显示同一范围。 */
export const updateCheckIntervalHours = { minimum: 24, maximum: 168, defaultValue: 24 } as const;

function notificationSwitches(value: AppNotificationSettings): AppNotificationSettings {
  return { updateInApp: value.updateInApp === "off" ? "off" : "on", updateSystem: value.updateSystem === "off" ? "off" : "on",
    accessRequestSystem: value.accessRequestSystem === "off" ? "off" : "on" };
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
  return { ...pointPreferences().refresh[key] };
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
  if (preset === "responsive" || preset === "balanced" || preset === "lowPower" || preset === "quiet") {
    return preset;
  }

  return fallback;
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

  return animations === "normal" ? "normal" : ResourceManager.FrontendPoints.AnimationMode;
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
  return barColorMode === "distinct" || barColorMode === "type" ? barColorMode : ResourceManager.FrontendPoints.BarColorMode;
}

export function normalizeFontSmoothing(fontSmoothing?: string | null): AppFontSmoothing {
  if (fontSmoothing === "auto" || fontSmoothing === "system" || fontSmoothing === "grayscale" || fontSmoothing === "disabled") {
    return fontSmoothing;
  }

  return ResourceManager.FrontendPoints.FontSmoothing;
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
    motionFeel: motionFeel(settings?.motionFeel).id,
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
