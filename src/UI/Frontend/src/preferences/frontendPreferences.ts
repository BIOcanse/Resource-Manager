import { ResourceManager } from "./generated/frontendPoints.values.ts";
import { getHostMessageTransport } from "../host/hostMessageTransport.ts";
import type {
  AppAppearanceSettings, AppNotificationSettings, AppPerformanceSettings, AppSettings, AppPresetNumericSetting, DashboardSettings,
  FrontendRefreshIntervals
} from "../types.ts";
import type { TargetedReportViewTemplate } from "../features/targetedReport/views/viewTemplates.ts";

const points = ResourceManager.FrontendPoints;
const resetListeners = new Set<() => void>();
export function subscribeFrontendPreferencesReset(listener: () => void): () => void {
  resetListeners.add(listener);
  return () => { resetListeners.delete(listener); };
}
function notifyReset() { for (const listener of resetListeners) listener(); }
export type FrontendRefresh = {
  monitor: AppPresetNumericSetting;
  resourceTable: AppPresetNumericSetting;
  localSystem: AppPresetNumericSetting;
};
/** Presentation preferences; performance overlay settings belong to the backend. */
export interface FrontendPreferencesCache {
  overlaySelectionVersion?: 1;
  appearance: AppAppearanceSettings;
  refresh: FrontendRefresh;
  notifications: AppNotificationSettings;
  dashboard?: DashboardSettings | null;
  /** Targeted report view templates; null or absent means no templates. */
  targetedReportViewTemplates?: TargetedReportViewTemplate[] | null;
}
/** Scoped saves replace only that section using current values elsewhere; unscoped saves replace all sections. */
export type FrontendPreferencesScope = "global" | "dashboard" | "targetedReportViewTemplates";
export interface FrontendPreferencesClient {
  read(): Promise<FrontendPreferencesCache>;
  save(value: FrontendPreferencesCache, scope?: FrontendPreferencesScope): Promise<FrontendPreferencesCache>;
  reset(): Promise<FrontendPreferencesCache>;
}

export function pointPreferences(): FrontendPreferencesCache {
  return {
    overlaySelectionVersion: 1, appearance: {
      uiStyle: points.UiStyle, theme: points.Theme,
      animationStyle: points.AnimationStyle,
      resourceBarFrameRateMode: points.BarFrameRateMode, resourceBarFrameRate: points.BarFrameRate,
      barColorMode: points.BarColorMode, fontSmoothing: points.FontSmoothing, language: points.Language,
      byteUnitMode: points.ByteUnitMode
    },
    refresh: {
      monitor: { ...points.Monitor },
      resourceTable: { ...points.ResourceTable }, localSystem: { ...points.LocalSystem }
    },
    notifications: { updateNotify: points.UpdateNotify, updateInApp: points.UpdateInApp, updateSystem: points.UpdateSystem,
      accessRequestNotify: points.AccessRequestNotify, accessRequestInApp: points.AccessRequestInApp,
      accessRequestSystem: points.AccessRequestSystem, memoryProtectionNotify: points.MemoryProtectionNotify,
      memoryProtectionInApp: points.MemoryProtectionInApp, memoryProtectionSystem: points.MemoryProtectionSystem,
      cpuMonopolyProtectionNotify: points.CpuMonopolyProtectionNotify, cpuMonopolyProtectionInApp: points.CpuMonopolyProtectionInApp,
      crashReportNotify: points.CrashReportNotify, crashReportInApp: points.CrashReportInApp, crashReportSystem: points.CrashReportSystem },
    dashboard: null, targetedReportViewTemplates: null
  };
}

/** Convert legacy animation settings to animationStyle: preserve none/ultra, map smooth feel to smooth and other enabled styles to desktop. */
export function migrateAnimationAppearance(appearance: AppAppearanceSettings): AppAppearanceSettings {
  const legacy = appearance as AppAppearanceSettings & { animations?: unknown; motionFeel?: unknown };
  if (!("animations" in legacy) && !("motionFeel" in legacy)) return appearance;
  const { animations, motionFeel, ...rest } = legacy;
  if (rest.animationStyle !== undefined) return rest;
  return { ...rest, animationStyle: animations === "none" || animations === "ultra" ? animations
    : motionFeel === "smooth" ? "smooth" : "desktop" };
}

/** Rename the legacy NVML used-memory field before validation. Preserve malformed values for the normal validator. */
const retiredNvmlFbUsedField = "gpu_nvidia_fb_used_including_reserved_bytes";
const nvmlFbUsedField = "gpu_nvidia_fb_used_excluding_reserved_bytes";
export function migrateNvmlFbUsedField(value: FrontendPreferencesCache): FrontendPreferencesCache {
  const isObject = (item: unknown): item is Record<string, unknown> => typeof item === "object" && item !== null && !Array.isArray(item);
  const retiredSlot = (slot: unknown) => isObject(slot) && typeof slot.metricId === "string" && slot.metricId.startsWith(`${retiredNvmlFbUsedField}@`);
  const result = { ...value };
  const cards: unknown = isObject(value.dashboard) ? value.dashboard.cards : undefined;
  if (value.dashboard && Array.isArray(cards) && cards.some(card => isObject(card) && Array.isArray(card.slots) && card.slots.some(retiredSlot))) {
    result.dashboard = { ...value.dashboard, cards: cards.map(card => !isObject(card) || !Array.isArray(card.slots) ? card : { ...card,
      slots: card.slots.map(slot => !retiredSlot(slot) ? slot
        : { ...slot, metricId: nvmlFbUsedField + slot.metricId.slice(retiredNvmlFbUsedField.length) }) }) };
  }
  return result;
}

export function resolveFrontendRefresh(refresh: FrontendRefresh): FrontendRefreshIntervals {
  const presets = { monitor: points.MonitorPresets, resourceTable: points.ResourceTablePresets,
    localSystem: points.LocalSystemPresets };
  // Use the selected custom value or preset; automatic mode uses the foreground preset.
  const resolve = (key: "monitor" | "resourceTable" | "localSystem") => {
    const setting = refresh[key];
    return setting.mode === "custom" ? setting.customValue
      : presets[key][setting.mode === "preset" ? setting.preset : points.AutoForegroundPreset];
  };
  return {
    resolved: { monitor: resolve("monitor"), resourceTable: resolve("resourceTable"), localSystem: resolve("localSystem") },
    presets,
    defaults: { monitor: { ...points.Monitor }, resourceTable: { ...points.ResourceTable }, localSystem: { ...points.LocalSystem } }
  };
}

export function applyFrontendPreferences(settings: Omit<AppSettings, "appearance" | "performance" | "notifications"> & { performance?: Partial<AppPerformanceSettings> }, preferences: FrontendPreferencesCache): AppSettings {
  return {
    ...settings, appearance: structuredClone(preferences.appearance),
    notifications: { ...preferences.notifications },
    performance: settings.performance ? { ...settings.performance,
      monitorRefreshIntervalMs: { ...preferences.refresh.monitor },
      resourceTableRefreshIntervalMs: { ...preferences.refresh.resourceTable },
      localSystemRefreshIntervalMs: { ...preferences.refresh.localSystem } } as AppPerformanceSettings : undefined
  };
}

export function preferencesFromSettings(value: AppSettings, existing: FrontendPreferencesCache): FrontendPreferencesCache {
  const performance = value.performance!;
  return {
    appearance: structuredClone(value.appearance!),
    refresh: {
      monitor: { ...performance.monitorRefreshIntervalMs }, resourceTable: { ...performance.resourceTableRefreshIntervalMs },
      localSystem: { ...performance.localSystemRefreshIntervalMs } },
    notifications: { ...value.notifications! },
    dashboard: existing.dashboard,
    targetedReportViewTemplates: existing.targetedReportViewTemplates ?? null
  };
}

/** Presentation fields never enter an app-settings request. */
export function backendSettings(value: AppSettings): Record<string, unknown> {
  const { appearance: _appearance, notifications: _notifications, ...backend } = value;
  if (!backend.performance) return backend;
  const { monitorRefreshIntervalMs: _monitor,
    resourceTableRefreshIntervalMs: _table, localSystemRefreshIntervalMs: _system, ...performance } = backend.performance;
  return { ...backend, performance };
}

export function createFrontendPreferencesClient(storage?: Pick<Storage, "getItem" | "setItem" | "removeItem">): FrontendPreferencesClient {
  const host = getHostMessageTransport();
  // Native mode has exactly one owner. A failed native request never switches to browser storage.
  if (host) {
    const request = (type: string, value?: FrontendPreferencesCache, scope?: FrontendPreferencesScope) => new Promise<FrontendPreferencesCache>((resolve, reject) => {
      const requestId = crypto.randomUUID();
      let unsubscribe: (() => void) | null = null;
      const timer = setTimeout(() => { unsubscribe?.(); reject(new Error("preferences-unavailable")); }, 10000);
      unsubscribe = host.subscribeMessages(message => {
        if (!message || typeof message !== "object") return;
        const response = message as { type?: string; requestId?: string; value?: FrontendPreferencesCache; error?: string };
        if (response.type !== "preferences.result" || response.requestId !== requestId) return;
        clearTimeout(timer);
        unsubscribe?.();
        if (response.error || !response.value) reject(new Error(response.error ?? "preferences-unavailable"));
        else resolve(response.value);
      });
      if (!unsubscribe) { clearTimeout(timer); reject(new Error("preferences-unavailable")); return; }
      host.postMessage({ type, requestId, ...(value ? { value } : {}), ...(scope ? { scope } : {}) });
    });
    return { read: () => request("preferences.get"), save: async (value, scope) => {
      const result = await request("preferences.save", value, scope);
      if (scope === "dashboard" && value.dashboard == null) notifyReset();
      return result;
    }, reset: async () => { const result = await request("preferences.reset"); notifyReset(); return result; } };
  }
  // Standalone browser frontend uses its own WebView/browser storage, never the API.
  const key = "resource-manager:frontend-preferences:v1";
  const browserStorage = storage ?? (typeof window === "undefined" ? undefined : window.localStorage);
  const readStored = () => {
    const text = browserStorage?.getItem(key);
    return text == null ? undefined : JSON.parse(text) as FrontendPreferencesCache & { overlays?: unknown };
  };
  const readCache = () => {
    const value = readStored();
    if (value === undefined) return pointPreferences();
    // Fill missing notification preferences from declared values and migrate legacy animation settings.
    const appearance = value.appearance ? migrateAnimationAppearance(value.appearance) : undefined;
    // Discard obsolete reading-source preferences.
    if (appearance) delete (appearance as AppAppearanceSettings & { readingSourceMode?: unknown }).readingSourceMode;
    // Discard obsolete self-scheduling animation preferences.



    if (appearance) delete (appearance as AppAppearanceSettings & { autoDisableAnimations?: unknown }).autoDisableAnimations;
    // Notification sequence numbers replace obsolete version-based notification tracking.

    const { updateInAppNotifiedVersion: _notified, ...notifications } =
      (value.notifications ?? {}) as Partial<AppNotificationSettings> & { updateInAppNotifiedVersion?: unknown };
    // Preserve legacy overlay input until a native process receives backend commit confirmation.
    // Browser presentation ignores it; saves and resets retain only the stored input.

    const { overlays: _overlays, ...current } = migrateNvmlFbUsedField(value) as FrontendPreferencesCache & { overlays?: unknown };
    return { ...current, ...(appearance ? { appearance } : {}),
      notifications: { ...pointPreferences().notifications, ...notifications } };
  };
  let writes = Promise.resolve();
  const enqueue = (operation: () => FrontendPreferencesCache) => {
    const write = writes.then(operation);
    writes = write.then(() => undefined, () => undefined);
    return write;
  };
  const preservePendingOverlays = (value: FrontendPreferencesCache) => {
    const stored = readStored();
    const { overlays: _overlays, ...preferences } = value as FrontendPreferencesCache & { overlays?: unknown };
    // Only carry forward the stored migration input. A stale caller cannot supply or overwrite backend choices.
    return stored && Object.hasOwn(stored, "overlays") ? { ...preferences, overlays: stored.overlays } : preferences;
  };
  const save = (value: FrontendPreferencesCache, scope?: FrontendPreferencesScope) => {
    return enqueue(() => {
      if (!browserStorage) throw new Error("preferences-unavailable");
      const current = readCache();
      let next = value;
      if (scope === "global") next = { ...current, appearance: value.appearance, refresh: value.refresh, notifications: value.notifications };
      else if (scope === "dashboard") next = { ...current, dashboard: value.dashboard ?? null };
      else if (scope === "targetedReportViewTemplates") next = { ...current, targetedReportViewTemplates: value.targetedReportViewTemplates ?? null };
      browserStorage.setItem(key, JSON.stringify(preservePendingOverlays(next)));
      if (scope === "dashboard" && value.dashboard == null) notifyReset();
      const { overlays: _overlays, ...preferences } = next as FrontendPreferencesCache & { overlays?: unknown };
      return structuredClone(preferences);
    });
  };
  return { read: async () => { await writes; return readCache(); }, save, reset: () => enqueue(() => {
    if (!browserStorage) throw new Error("preferences-unavailable");
    const value = pointPreferences();
    const persisted = preservePendingOverlays(value);
    if (Object.hasOwn(persisted, "overlays")) browserStorage.setItem(key, JSON.stringify(persisted));
    else browserStorage.removeItem(key);
    notifyReset();
    return value;
  }) };
}

let client: FrontendPreferencesClient | undefined;
export function frontendPreferencesClient(): FrontendPreferencesClient {
  return client ??= createFrontendPreferencesClient();
}
