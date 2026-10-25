import { ResourceManager } from "./generated/frontendPoints.values.ts";
import { getHostMessageTransport } from "../host/hostMessageTransport.ts";
import type {
  AppAppearanceSettings, AppNotificationSettings, AppPerformanceSettings, AppSettings, AppPresetNumericSetting, DashboardSettings,
  FrontendRefreshIntervals, PerformanceOverlaySettings
} from "../types.ts";

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
export type OverlayPresentation = Omit<PerformanceOverlaySettings, "softwareId" | "injectionModules">;
/** 新版本提示：两条途径的开关（设置页草稿可改）和界面内提示已提示过的版本（只由界面内提示写入）。 */
export interface FrontendNotifications extends AppNotificationSettings {
  updateInAppNotifiedVersion: string | null;
}
export interface FrontendPreferencesCache {
  overlaySelectionVersion?: 1;
  appearance: AppAppearanceSettings;
  refresh: FrontendRefresh;
  notifications: FrontendNotifications;
  overlays: Record<string, OverlayPresentation>;
  dashboard?: DashboardSettings | null;
}
export interface FrontendPreferencesClient {
  read(): Promise<FrontendPreferencesCache>;
  save(value: FrontendPreferencesCache, scope?: "global" | "dashboard" | { overlay: string }): Promise<FrontendPreferencesCache>;
  reset(): Promise<FrontendPreferencesCache>;
}

export function cachedOverlay(preferences: FrontendPreferencesCache, softwareId: string): OverlayPresentation | undefined {
  return Object.entries(preferences.overlays).find(([id]) => id.toLowerCase() === softwareId.toLowerCase())?.[1];
}

export function pointPreferences(): FrontendPreferencesCache {
  return {
    overlaySelectionVersion: 1, appearance: {
      uiStyle: points.UiStyle, theme: points.Theme, animations: points.AnimationMode,
      motionFeel: points.MotionFeel,
      resourceBarFrameRateMode: points.BarFrameRateMode, resourceBarFrameRate: points.BarFrameRate,
      barColorMode: points.BarColorMode, fontSmoothing: points.FontSmoothing, language: points.Language,
      byteUnitMode: points.ByteUnitMode, readingSourceMode: points.ReadingSourceMode
    },
    refresh: {
      monitor: { ...points.Monitor },
      resourceTable: { ...points.ResourceTable }, localSystem: { ...points.LocalSystem }
    },
    notifications: { updateInApp: points.UpdateInApp, updateSystem: points.UpdateSystem, updateInAppNotifiedVersion: null },
    overlays: {}, dashboard: null
  };
}

export function pointOverlayPresentation(): OverlayPresentation {
  return {
    enabled: false, mode: "external", metrics: [...points.MetricOrder], fontSizePx: points.FontSizePx, sizeMode: points.SizeMode,
    regionWidthRatio: points.RegionWidthRatio, regionHeightRatio: points.RegionHeightRatio,
    anchor: points.Anchor, marginPx: points.MarginPx
  };
}

export function resolveFrontendRefresh(refresh: FrontendRefresh): FrontendRefreshIntervals {
  const presets = { monitor: points.MonitorPresets, resourceTable: points.ResourceTablePresets,
    localSystem: points.LocalSystemPresets };
  const resolve = (key: "monitor" | "resourceTable" | "localSystem", background: boolean) => {
    const setting = refresh[key];
    return setting.mode === "custom" ? setting.customValue : presets[key][setting.mode === "preset"
      ? setting.preset : background ? points.AutoBackgroundPreset : points.AutoForegroundPreset];
  };
  return {
    foreground: { monitor: resolve("monitor", false), resourceTable: resolve("resourceTable", false), localSystem: resolve("localSystem", false) },
    backgroundOptimized: { monitor: resolve("monitor", true), resourceTable: resolve("resourceTable", true), localSystem: resolve("localSystem", true) },
    presets,
    defaults: { monitor: { ...points.Monitor }, resourceTable: { ...points.ResourceTable }, localSystem: { ...points.LocalSystem } }
  };
}

export function applyFrontendPreferences(settings: Omit<AppSettings, "appearance" | "performance" | "notifications"> & { performance?: Partial<AppPerformanceSettings> }, preferences: FrontendPreferencesCache): AppSettings {
  return {
    ...settings, appearance: structuredClone(preferences.appearance),
    notifications: { updateInApp: preferences.notifications.updateInApp, updateSystem: preferences.notifications.updateSystem },
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
    notifications: { ...value.notifications!, updateInAppNotifiedVersion: existing.notifications.updateInAppNotifiedVersion },
    overlays: existing.overlays, dashboard: existing.dashboard
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
    const request = (type: string, value?: FrontendPreferencesCache, scope?: "global" | "dashboard" | { overlay: string }) => new Promise<FrontendPreferencesCache>((resolve, reject) => {
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
      host.postMessage({ type, requestId, ...(value ? { value } : {}),
        ...(typeof scope === "string" ? { scope } : scope ? { scope: "overlay", softwareId: scope.overlay } : {}) });
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
  const readCache = () => {
    const text = browserStorage?.getItem(key);
    if (text == null) return pointPreferences();
    const value = JSON.parse(text) as FrontendPreferencesCache;
    // 与原生壳相同：缺少整个提示分区的旧缓存按本次构建的点值读取该分区。
    return value.notifications ? value : { ...value, notifications: pointPreferences().notifications };
  };
  let writes = Promise.resolve();
  const save = (value: FrontendPreferencesCache, scope?: "global" | "dashboard" | { overlay: string }) => {
    const write = writes.then(() => {
      if (!browserStorage) throw new Error("preferences-unavailable");
      const current = readCache();
      let next = value;
      if (scope === "global") next = { ...current, appearance: value.appearance, refresh: value.refresh, notifications: value.notifications };
      else if (scope === "dashboard") next = { ...current, dashboard: value.dashboard ?? null };
      else if (scope) {
        const overlays = { ...current.overlays };
        const presentation = cachedOverlay(value, scope.overlay);
        for (const id of Object.keys(overlays)) if (id.toLowerCase() === scope.overlay.toLowerCase()) delete overlays[id];
        if (presentation) overlays[scope.overlay] = presentation;
        next = { ...current, overlays };
      }
      browserStorage.setItem(key, JSON.stringify(next));
      if (scope === "dashboard" && value.dashboard == null) notifyReset();
      return structuredClone(next);
    });
    writes = write.then(() => undefined, () => undefined);
    return write;
  };
  return { read: async () => { await writes; return readCache(); }, save, reset: async () => {
    await writes;
    if (!browserStorage) throw new Error("preferences-unavailable");
    const current = readCache();
    const value = pointPreferences();
    value.overlays = Object.fromEntries(Object.entries(current.overlays).map(([id, overlay]) =>
      [id, { ...pointOverlayPresentation(), enabled: overlay.enabled ?? false, mode: overlay.mode ?? "external" }]));
    // 与原生壳相同：已提示版本是“提示过哪个版本”的记录，不是偏好，重置偏好点时保留。
    value.notifications.updateInAppNotifiedVersion = current.notifications.updateInAppNotifiedVersion;
    if (Object.keys(value.overlays).length || value.notifications.updateInAppNotifiedVersion !== null)
      browserStorage.setItem(key, JSON.stringify(value));
    else browserStorage.removeItem(key);
    notifyReset();
    return value;
  } };
}

let client: FrontendPreferencesClient | undefined;
export function frontendPreferencesClient(): FrontendPreferencesClient {
  return client ??= createFrontendPreferencesClient();
}
