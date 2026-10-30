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
export interface FrontendPreferencesCache {
  overlaySelectionVersion?: 1;
  appearance: AppAppearanceSettings;
  refresh: FrontendRefresh;
  notifications: AppNotificationSettings;
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
      memoryProtectionInApp: points.MemoryProtectionInApp, memoryProtectionSystem: points.MemoryProtectionSystem },
    overlays: {}, dashboard: null
  };
}

/**
 * Migration frontend-preferences-animation-style-v1（docs/animation_style_2026-10-26.md「归属」）。
 * 2026-10-26 之前写下的外观段有 animations（auto、normal、none、ultra）和 motionFeel（standard、smooth、crisp），
 * 一次换成 animationStyle：none、ultra 照搬；auto、normal 按原手感。原手感 smooth → smooth，standard、crisp 和没有手感
 * （手感之前的缓存，当时就是标准）→ desktop。
 * 原生壳的 FrontendPreferencesOwner 对它的缓存文件做同一份迁移。删除条件：下一个不再读旧版本缓存的发行。
 */
export function migrateAnimationAppearance(appearance: AppAppearanceSettings): AppAppearanceSettings {
  const legacy = appearance as AppAppearanceSettings & { animations?: unknown; motionFeel?: unknown };
  if (!("animations" in legacy) && !("motionFeel" in legacy)) return appearance;
  const { animations, motionFeel, ...rest } = legacy;
  if (rest.animationStyle !== undefined) return rest;
  return { ...rest, animationStyle: animations === "none" || animations === "ultra" ? animations
    : motionFeel === "smooth" ? "smooth" : "desktop" };
}

/**
 * Migration frontend-preferences-nvml-fb-used-v1：2026-10-29 后端把 NVML v2 已用显存字段 gpu_nvidia_fb_used_including_reserved_bytes
 * 改名为 gpu_nvidia_fb_used_excluding_reserved_bytes（这个值本来就不含保留部分）。之前保存的卡片槽位（「字段名@设备身份」）
 * 和浮窗来源（fieldName）读入时换成新名字，其他不动。原生壳的 FrontendPreferencesOwner 对它的缓存文件做同一份迁移。
 * 迁移在校验之前运行：只改形状对的值，其他原样交给后面的校验（例如 dashboardPreferences.ts 只拒绝坏的监视台段）。
 * 删除条件：下一个不再读旧版本缓存的发行。
 */
const retiredNvmlFbUsedField = "gpu_nvidia_fb_used_including_reserved_bytes";
const nvmlFbUsedField = "gpu_nvidia_fb_used_excluding_reserved_bytes";
export function migrateNvmlFbUsedField(value: FrontendPreferencesCache): FrontendPreferencesCache {
  const isObject = (item: unknown): item is Record<string, unknown> => typeof item === "object" && item !== null && !Array.isArray(item);
  const retiredSlot = (slot: unknown) => isObject(slot) && typeof slot.metricId === "string" && slot.metricId.startsWith(`${retiredNvmlFbUsedField}@`);
  const retiredSource = (source: unknown) => isObject(source) && source.fieldName === retiredNvmlFbUsedField;
  const result = { ...value };
  const cards: unknown = isObject(value.dashboard) ? value.dashboard.cards : undefined;
  if (value.dashboard && Array.isArray(cards) && cards.some(card => isObject(card) && Array.isArray(card.slots) && card.slots.some(retiredSlot))) {
    result.dashboard = { ...value.dashboard, cards: cards.map(card => !isObject(card) || !Array.isArray(card.slots) ? card : { ...card,
      slots: card.slots.map(slot => !retiredSlot(slot) ? slot
        : { ...slot, metricId: nvmlFbUsedField + slot.metricId.slice(retiredNvmlFbUsedField.length) }) }) };
  }
  if (isObject(value.overlays)) {
    result.overlays = Object.fromEntries(Object.entries(value.overlays).map(([id, overlay]) => [id,
      !isObject(overlay) || !isObject(overlay.metricSources) || !Object.values(overlay.metricSources).some(retiredSource) ? overlay
        : { ...overlay, metricSources: Object.fromEntries(Object.entries(overlay.metricSources).map(([metric, source]) => [metric,
          retiredSource(source) ? { ...source, fieldName: nvmlFbUsedField } : source])) }]));
  }
  return result;
}

export function pointOverlayPresentation(): OverlayPresentation {
  return {
    enabled: false, mode: "external", metrics: [...points.MetricOrder], metricSources: {}, fontSizePx: points.FontSizePx, sizeMode: points.SizeMode,
    regionWidthRatio: points.RegionWidthRatio, regionHeightRatio: points.RegionHeightRatio,
    anchor: points.Anchor, marginPx: points.MarginPx
  };
}

export function resolveFrontendRefresh(refresh: FrontendRefresh): FrontendRefreshIntervals {
  const presets = { monitor: points.MonitorPresets, resourceTable: points.ResourceTablePresets,
    localSystem: points.LocalSystemPresets };
  // 只按用户的手动设置：自定义取自定义值，档位取那一档，自动取自动前台档位（refresh-auto_foreground_preset）。
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
    // 与原生壳相同：旧缓存缺少的通知字段按本次构建的点值读取，保留已有选择；旧的动画两项按迁移换成动画风格。
    const appearance = value.appearance ? migrateAnimationAppearance(value.appearance) : undefined;
    // 2026-10-26 删除读数来源设置；旧缓存只丢弃这一项，后续保存不再写回。
    if (appearance) delete (appearance as AppAppearanceSettings & { readingSourceMode?: unknown }).readingSourceMode;
    // Migration frontend-preferences-self-scheduling-options-v1（与原生壳 FrontendPreferencesOwner 同名迁移相同）：
    // 2026-10-29 用户删除 RM 自调度和它触发的「按自调度自动关闭动画」，动画只按选中的风格；旧缓存的这一项读到时丢弃，
    // 后续保存不再写回。另一个删掉的选项（刷新自动档的后台档位）是构建点值，从未写进缓存。
    // 删除条件：所有受支持的升级都从 2026-10-29 之后的发行开始。
    if (appearance) delete (appearance as AppAppearanceSettings & { autoDisableAnimations?: unknown }).autoDisableAnimations;
    // Migration frontend-preferences-notified-version-v1（与原生壳 FrontendPreferencesOwner 同名迁移相同）：界面内更新提示
    // 2026-10-29 起跟随通知序号，旧缓存的已提示版本记录丢弃。删除条件：下一个不再读旧版本缓存的发行。
    const { updateInAppNotifiedVersion: _notified, ...notifications } =
      (value.notifications ?? {}) as Partial<AppNotificationSettings> & { updateInAppNotifiedVersion?: unknown };
    return { ...migrateNvmlFbUsedField(value), ...(appearance ? { appearance } : {}),
      notifications: { ...pointPreferences().notifications, ...notifications } };
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
    if (Object.keys(value.overlays).length) browserStorage.setItem(key, JSON.stringify(value));
    else browserStorage.removeItem(key);
    notifyReset();
    return value;
  } };
}

let client: FrontendPreferencesClient | undefined;
export function frontendPreferencesClient(): FrontendPreferencesClient {
  return client ??= createFrontendPreferencesClient();
}
