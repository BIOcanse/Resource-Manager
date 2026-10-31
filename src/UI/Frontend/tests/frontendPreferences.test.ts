import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { ResourceManager } from "../src/preferences/generated/frontendPoints.values.ts";
import { backendSettings, createFrontendPreferencesClient, pointPreferences, resolveFrontendRefresh } from "../src/preferences/frontendPreferences.ts";
import { readDashboardPreferences } from "../src/preferences/dashboardPreferences.ts";
import { defaultAppSettings } from "../src/stores/settingsStore.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { forceTerminateHotkeyActionId, normalizeEditableHotkeys,
  performanceOverlayHotkeyActionId } from "../src/features/settings/editableHotkeys.ts";

const points = ResourceManager.FrontendPoints;
function storage() {
  const values = new Map<string, string>();
  return { getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
    removeItem: (key: string) => { values.delete(key); } };
}

test("frontend changes survive restart; reset restores every preference", async () => {
  const disk = storage();
  const first = createFrontendPreferencesClient(disk);
  assert.deepEqual(await first.read(), pointPreferences());
  await first.reset();
  assert.equal(disk.getItem("resource-manager:frontend-preferences:v1"), null);
  const changed = await first.read();
  changed.appearance.theme = "dark";
  changed.refresh.monitor = { mode: "custom", preset: "quiet", customValue: 4321 };
  await first.save(changed);
  const restarted = createFrontendPreferencesClient(disk);
  assert.deepEqual(await restarted.read(), changed);
  await restarted.reset();
  assert.deepEqual(await createFrontendPreferencesClient(disk).read(), pointPreferences());
  assert.equal(disk.getItem("resource-manager:frontend-preferences:v1"), null);
  assert.equal((await restarted.read()).appearance.theme, points.Theme);
  // The per-software overlay settings are backend settings (performanceOverlay), not frontend preferences.
  assert.equal("overlays" in pointPreferences(), false);
});

test("every refresh preset and custom interval resolve locally from the manual setting only", () => {
  for (const key of ["monitor", "resourceTable", "localSystem"] as const) {
    for (const preset of ["responsive", "balanced", "lowPower", "quiet"] as const) {
      const defaults = pointPreferences();
      defaults.refresh[key] = { mode: "preset", preset, customValue: 1234 };
      const result = resolveFrontendRefresh(defaults.refresh);
      assert.equal(result.resolved[key], result.presets[key][preset]);
    }
    for (const customValue of [500, 4321, 300000]) {
      const defaults = pointPreferences();
      defaults.refresh[key] = { mode: "custom", preset: "balanced", customValue };
      const result = resolveFrontendRefresh(defaults.refresh);
      assert.equal(result.resolved[key], customValue);
    }
  }
  // The automatic mode resolves to the automatic foreground preset; there is no second (background) profile.
  const result = resolveFrontendRefresh(pointPreferences().refresh);
  for (const key of ["monitor", "resourceTable", "localSystem"] as const) {
    assert.equal(pointPreferences().refresh[key].mode, "aotu");
    assert.equal(result.resolved[key], result.presets[key][points.AutoForegroundPreset]);
  }
  assert.deepEqual(Object.keys(result).sort(), ["defaults", "presets", "resolved"]);
  assert.deepEqual(result.defaults.localSystem, points.LocalSystem);
});

test("concurrent scoped frontend writes preserve the other view's choices", async () => {
  const client = createFrontendPreferencesClient(storage());
  const old = await client.read();
  await Promise.all([
    client.save({ ...old, appearance: { ...old.appearance, theme: "dark" } }, "global"),
    client.save({ ...old, targetedReportViewTemplates: [] }, "targetedReportViewTemplates")
  ]);
  const current = await client.read();
  assert.equal(current.appearance.theme, "dark");
  assert.deepEqual(current.targetedReportViewTemplates, []);
});

test("frontend settings never enter the backend settings view", () => {
  const payload = backendSettings(defaultAppSettings());
  assert.equal("appearance" in payload, false);
  for (const field of ["monitorRefreshIntervalMs", "resourceTableRefreshIntervalMs", "localSystemRefreshIntervalMs"])
    assert.equal(field in (payload.performance as object), false, field);
});

test("cache explanation and reset are translated in every selectable locale", async () => {
  const explanations = new Set<string>();
  const labels = new Set<string>();
  for (const language of languageOptions) {
    if (language.id === "system") continue;
    const copy = await loadSettingsText(language.id);
    assert.ok(copy.appearance.cacheDescription.trim(), language.id);
    assert.ok(copy.appearance.resetCache.trim(), language.id);
    explanations.add(copy.appearance.cacheDescription);
    labels.add(copy.appearance.resetCache);
  }
  assert.equal(explanations.size, 9);
  assert.equal(labels.size, 9);
});

const notificationSymbols = ["UpdateNotify", "UpdateInApp", "UpdateSystem", "AccessRequestNotify", "AccessRequestInApp",
  "AccessRequestSystem", "MemoryProtectionNotify", "MemoryProtectionInApp", "MemoryProtectionSystem",
  "CrashReportNotify", "CrashReportInApp", "CrashReportSystem"] as const;
const allNotifications = (value: "on" | "off") => Object.fromEntries(notificationSymbols.map(symbol =>
  [symbol[0]!.toLowerCase() + symbol.slice(1), value])) as ReturnType<typeof pointPreferences>["notifications"];

test("the notification section has three switches per kind, defaults to its points and saves with the global scope only", async () => {
  // docs/notifications_2026-10-29.md「设置在前端」：每类是否弹、Windows 通知、软件内提示，默认全开。
  assert.deepEqual(pointPreferences().notifications, allNotifications("on"));
  for (const symbol of notificationSymbols) assert.equal(points[symbol], "on", symbol);
  const disk = storage();
  // A cache saved before the section existed reads the section's points and keeps its other choices.
  const { notifications: _absent, ...older } = { ...pointPreferences(), appearance: { ...pointPreferences().appearance, theme: "dark" as const } };
  disk.setItem("resource-manager:frontend-preferences:v1", JSON.stringify(older));
  const client = createFrontendPreferencesClient(disk);
  const loaded = await client.read();
  assert.equal(loaded.appearance.theme, "dark");
  assert.deepEqual(loaded.notifications, pointPreferences().notifications);
  const chosen = { ...allNotifications("off"), updateSystem: "on" as const, memoryProtectionInApp: "on" as const };
  await client.save({ ...loaded, notifications: chosen }, "global");
  // Dashboard and report-template saves from views holding the old section do not write it.
  await client.save({ ...loaded, dashboard: null }, "dashboard");
  await client.save({ ...loaded, targetedReportViewTemplates: [] }, "targetedReportViewTemplates");
  const current = await createFrontendPreferencesClient(disk).read();
  assert.deepEqual(current.notifications, chosen);
  assert.deepEqual(current.targetedReportViewTemplates, []);
  // The switches are frontend preferences and never enter an app-settings request.
  assert.equal("notifications" in backendSettings({ ...defaultAppSettings(), notifications: allNotifications("off") }), false);
  // Reset restores every switch; nothing else of the section is kept.
  assert.deepEqual((await client.reset()).notifications, allNotifications("on"));
});

test("an older notification section keeps its channel choices, gets the new switches from their points and drops the noticed version", async () => {
  // The old updateSystem / accessRequestSystem / updateInApp switches are the same points as the new Windows and in-app channels.
  const disk = storage();
  disk.setItem("resource-manager:frontend-preferences:v1", JSON.stringify({ ...pointPreferences(),
    notifications: { updateInApp: "off", updateSystem: "off", accessRequestSystem: "off", updateInAppNotifiedVersion: "1.2.0" } }));
  const client = createFrontendPreferencesClient(disk);
  const current = await client.read();
  assert.deepEqual(current.notifications, { ...allNotifications("on"), updateInApp: "off", updateSystem: "off", accessRequestSystem: "off" });
  assert.equal("updateInAppNotifiedVersion" in current.notifications, false, "migration frontend-preferences-notified-version-v1");
  await client.save(current, "global");
  assert.equal(disk.getItem("resource-manager:frontend-preferences:v1")!.includes("updateInAppNotifiedVersion"), false,
    "the next save does not write the record back");
});

test("the notification points use the notification group and enabled on/off choices", () => {
  const declaration = JSON.parse(readFileSync(new URL("../preferences.points.json", import.meta.url), "utf8"));
  const group = declaration.points.find((point: { symbol: string }) => point.symbol === "UpdateSystem").group;
  const names: Record<(typeof notificationSymbols)[number], string> = {
    UpdateNotify: "notification-update_notify", UpdateInApp: "notification-update_in_app", UpdateSystem: "notification-update_system",
    AccessRequestNotify: "notification-access_request_notify", AccessRequestInApp: "notification-access_request_in_app",
    AccessRequestSystem: "notification-access_request_system", MemoryProtectionNotify: "notification-memory_protection_notify",
    MemoryProtectionInApp: "notification-memory_protection_in_app", MemoryProtectionSystem: "notification-memory_protection_system",
    CrashReportNotify: "notification-crash_report_notify", CrashReportInApp: "notification-crash_report_in_app",
    CrashReportSystem: "notification-crash_report_system"
  };
  for (const symbol of notificationSymbols) {
    assert.deepEqual(declaration.points.find((point: { symbol: string }) => point.symbol === symbol),
      { group, name: names[symbol], symbol, type: "choice", options: ["on", "off"], value: "on" }, symbol);
  }
  assert.equal(declaration.points.filter((point: { group: number }) => point.group === group).length, notificationSymbols.length);
});

test("the notification section and the access-request notice are translated in all selectable languages", async () => {
  // Every value is present; a language's copy is its own (not the English or simplified Chinese base it inherits from).
  const values = new Map<string, Map<string, string>>();
  for (const language of languageOptions) {
    if (language.id === "system") continue;
    const copy = await loadSettingsText(language.id);
    const text = copy.notifications;
    const own = new Map<string, string>();
    const add = (key: string, value: string) => { assert.ok(value.trim(), `${language.id}: ${key}`); own.set(key, value); };
    add("section", copy.sections.notifications);
    add("intro", text.intro);
    for (const kind of ["memoryProtection", "update", "accessRequest"] as const) {
      add(`${kind}.title`, text.kinds[kind].title);
      add(`${kind}.description`, text.kinds[kind].description);
    }
    for (const key of ["notifyTitle", "notifyDescription", "systemTitle", "systemDescription", "inAppTitle", "inAppDescription",
      "accessRequestNoticeTitle", "accessRequestNoticeOpen"] as const) add(key, text[key]);
    const label = text.switchLabel(text.kinds.update.title, text.systemTitle);
    assert.ok(label.includes(text.kinds.update.title) && label.includes(text.systemTitle), `${language.id}: switch label`);
    const message = text.accessRequestNoticeMessage("Example agent", "agent.exe");
    assert.ok(message.includes("Example agent") && message.includes("agent.exe"), `${language.id}: access request message`);
    add("accessRequestNoticeMessage", message);
    values.set(language.id, own);
  }
  assert.equal(values.size, 9);
  for (const [language, own] of values) {
    if (language === "en-US" || language === "zh-CN") continue;
    const base = values.get(language === "zh-TW" ? "zh-CN" : "en-US")!;
    const same = [...own].filter(([key, value]) => base.get(key) === value).map(([key]) => key);
    assert.ok(same.length <= 4, `${language} repeats its base copy: ${same.join(", ")}`);
  }
});

test("animation choices saved before the animation style migrate once (docs/animation_style_2026-10-26.md)", async () => {
  // auto、normal 的风格按原手感（smooth → smooth；standard、crisp、没有手感 → desktop）；none、ultra 照搬。不再写自动关闭动画。
  const cases: Array<[string, string | undefined, string]> = [
    ["auto", "standard", "desktop"], ["auto", "crisp", "desktop"], ["auto", "smooth", "smooth"], ["auto", undefined, "desktop"],
    ["normal", "standard", "desktop"], ["normal", "smooth", "smooth"], ["normal", undefined, "desktop"],
    ["none", "smooth", "none"], ["ultra", "crisp", "ultra"]
  ];
  for (const [animations, motionFeel, style] of cases) {
    const disk = storage();
    const { animationStyle: _style, ...appearance } = { ...pointPreferences().appearance, theme: "dark" as const };
    disk.setItem("resource-manager:frontend-preferences:v1",
      JSON.stringify({ ...pointPreferences(), appearance: { ...appearance, animations, ...(motionFeel ? { motionFeel } : {}) } }));
    const loaded = (await createFrontendPreferencesClient(disk).read()).appearance;
    assert.equal(loaded.animationStyle, style, `${animations}/${motionFeel}`);
    assert.equal("autoDisableAnimations" in loaded, false, `${animations}/${motionFeel}`);
    assert.equal(loaded.theme, "dark");
    assert.equal("animations" in loaded || "motionFeel" in loaded, false, "old fields are dropped");
  }
  // A cache that already has the style only loses the old fields.
  const disk = storage();
  disk.setItem("resource-manager:frontend-preferences:v1", JSON.stringify({ ...pointPreferences(),
    appearance: { ...pointPreferences().appearance, animationStyle: "terminal", animations: "auto", motionFeel: "smooth" } }));
  const kept = (await createFrontendPreferencesClient(disk).read()).appearance;
  assert.deepEqual([kept.animationStyle, "animations" in kept], ["terminal", false]);
});

test("the removed self-scheduling animation switch is dropped on cache read and not written back", async () => {
  // Migration frontend-preferences-self-scheduling-options-v1: an old cache still holds autoDisableAnimations; it reads
  // without error, the value is ignored, the style stays the user's choice and a later save does not write it back.
  for (const autoDisableAnimations of [true, false]) {
    const disk = storage();
    const expected = { ...pointPreferences(), appearance: { ...pointPreferences().appearance, animationStyle: "terminal" as const } };
    disk.setItem("resource-manager:frontend-preferences:v1", JSON.stringify({ ...expected,
      appearance: { ...expected.appearance, autoDisableAnimations } }));
    const client = createFrontendPreferencesClient(disk);
    const loaded = await client.read();
    assert.deepEqual(loaded, expected);
    await client.save(loaded, "global");
    assert.equal(disk.getItem("resource-manager:frontend-preferences:v1")!.includes("autoDisableAnimations"), false);
    assert.deepEqual(await createFrontendPreferencesClient(disk).read(), expected);
  }
  assert.equal("autoDisableAnimations" in pointPreferences().appearance, false);
});

test("card slot names are migrated while pending overlay entries stay opaque until backend commit", async () => {
  // Migration frontend-preferences-nvml-fb-used-v1: only the retired name changes; the v1 field, metric ids and everything else stay.
  const v1 = "gpu_nvidia_fb_used_v1_including_reserved_bytes";
  // Migration frontend-preferences-overlays-to-backend-v1 (browser half): the standalone browser frontend has no window process
  // to write old overlay entries to the backend. Keep the old input on disk without exposing it as a current preference.
  const legacyOverlay = (field: string) => ({ enabled: true, mode: "external", metrics: ["gpu.vram", "gpu.vramV1"], metricSources: {
    "gpu.vram": { fieldName: field, deviceIdentity: "gpu:a" }, "gpu.vramV1": { fieldName: v1, deviceIdentity: "gpu:a" } } });
  const preferences = (field: string) => ({ ...pointPreferences(),
    dashboard: { version: 17, usesFactoryDefaults: false, cards: [{ id: "gpu-card", slots: [
      { metricId: `${field}@gpu:a`, size: "medium" as const, binding: { scopeKind: "device-field", scopeKey: "gpu:a" } },
      { metricId: `${v1}@gpu:a`, size: "small" as const, binding: { scopeKind: "device-field", scopeKey: "gpu:a" } },
      { metricId: "gpu.0.nvmlFbUsedBytes", size: "small" as const, binding: { scopeKind: "gpu", scopeKey: "gpu:a" } }] }],
      resourceBars: [{ id: "gpu-bar", metricId: "gpu.0.vram", scaleMode: "capacity" as const }],
      resourceTableColumns: [], resourceTableProcessColumns: [] } });
  const disk = storage();
  const retired = "gpu_nvidia_fb_used_including_reserved_bytes";
  disk.setItem("resource-manager:frontend-preferences:v1", JSON.stringify({ ...preferences(retired), overlays: { game: legacyOverlay(retired) } }));
  const client = createFrontendPreferencesClient(disk);
  const renamed = "gpu_nvidia_fb_used_excluding_reserved_bytes";
  const expected = preferences(renamed);
  const loaded = await client.read();
  assert.deepEqual(loaded, expected);
  await client.save(loaded, "dashboard");
  const written = disk.getItem("resource-manager:frontend-preferences:v1")!;
  assert.deepEqual(JSON.parse(written).dashboard, expected.dashboard);
  assert.deepEqual(JSON.parse(written).overlays, { game: legacyOverlay(retired) });
  assert.deepEqual(await createFrontendPreferencesClient(disk).read(), expected);
  await client.reset();
  assert.deepEqual(JSON.parse(disk.getItem("resource-manager:frontend-preferences:v1")!).overlays, { game: legacyOverlay(retired) });
  assert.deepEqual(await createFrontendPreferencesClient(disk).read(), pointPreferences());
});

test("a stale preference caller cannot replace pending overlays and reset is serialized with saves", async () => {
  const disk = storage();
  const key = "resource-manager:frontend-preferences:v1";
  const pending = { game: { enabled: true, mode: "injected", fontSizePx: 27 } };
  disk.setItem(key, JSON.stringify({ ...pointPreferences(), overlays: pending }));
  const client = createFrontendPreferencesClient(disk);
  const stale = { ...await client.read(), overlays: { other: { enabled: false } } };
  const saved = await client.save(stale);
  assert.equal("overlays" in saved, false);
  assert.deepEqual(JSON.parse(disk.getItem(key)!).overlays, pending);
  await Promise.all([
    client.reset(),
    client.save({ ...saved, appearance: { ...saved.appearance, language: "de-DE" } }, "global"),
    client.save({ ...saved, targetedReportViewTemplates: [] }, "targetedReportViewTemplates")
  ]);
  const current = await client.read();
  assert.equal(current.appearance.language, "de-DE");
  assert.deepEqual(current.targetedReportViewTemplates, []);
  assert.deepEqual(JSON.parse(disk.getItem(key)!).overlays, pending);
});

test("hotkey normalization never supplies missing backend actions or their defaults", () => {
  assert.deepEqual(normalizeEditableHotkeys(), []);
  assert.deepEqual(normalizeEditableHotkeys([]), []);
  const overlay = { actionId: performanceOverlayHotkeyActionId, enabled: true, encoding: [65, 0, 0, 0, 0, 0, 0] };
  assert.deepEqual(normalizeEditableHotkeys([overlay]), [overlay]);
  assert.equal(normalizeEditableHotkeys([overlay]).some(hotkey => hotkey.actionId === forceTerminateHotkeyActionId), false);
});

test("malformed card slots pass the NVML rename untouched and are left to the validators", async () => {
  // The rename runs before any validation: a null slot or a numeric metricId must not fail the whole read.
  const malformed = { ...pointPreferences(), appearance: { ...pointPreferences().appearance, theme: "dark" as const },
    dashboard: { version: 17, usesFactoryDefaults: false, cards: [{ id: "broken", slots: [null, { metricId: 42, size: "small" }] }] } };
  const disk = storage();
  disk.setItem("resource-manager:frontend-preferences:v1", JSON.stringify(malformed));
  const client = createFrontendPreferencesClient(disk);
  assert.deepEqual(await client.read(), malformed, "the read keeps the malformed card slots exactly as before the migration existed");
  await assert.rejects(readDashboardPreferences(undefined, client), /Invalid frontend dashboard cache/, "the dashboard validator still rejects only that section");
  // A well-formed retired slot next to malformed ones is still renamed; the malformed ones stay as they are.
  const mixedSlot = (field: string) => ({ metricId: `${field}@gpu:a`, size: "small" });
  const mixed = (field: string) => ({ ...malformed,
    dashboard: { ...malformed.dashboard, cards: [{ id: "broken", slots: [...malformed.dashboard.cards[0].slots, mixedSlot(field)] }] } });
  const mixedDisk = storage();
  mixedDisk.setItem("resource-manager:frontend-preferences:v1", JSON.stringify(mixed("gpu_nvidia_fb_used_including_reserved_bytes")));
  const renamed = "gpu_nvidia_fb_used_excluding_reserved_bytes";
  assert.deepEqual(await createFrontendPreferencesClient(mixedDisk).read(), mixed(renamed));
});

test("retired reading-source choices are dropped on cache read and subsequent save", async () => {
  for (const readingSourceMode of ["preferred", "all", "unknown", null, 42, {}]) {
    for (const oldAnimation of [false, true]) {
      const disk = storage();
      const expected = { ...pointPreferences(), appearance: { ...pointPreferences().appearance, theme: "dark" as const } };
      const appearance = oldAnimation
        ? { ...expected.appearance, animationStyle: undefined, animations: "normal", motionFeel: "smooth" }
        : expected.appearance;
      disk.setItem("resource-manager:frontend-preferences:v1", JSON.stringify({ ...expected,
        appearance: { ...appearance, readingSourceMode } }));
      if (oldAnimation) {
        expected.appearance.animationStyle = "smooth";
      }
      const client = createFrontendPreferencesClient(disk);
      const loaded = await client.read();
      assert.equal("readingSourceMode" in loaded.appearance, false);
      assert.deepEqual(loaded, expected);
      await client.save(loaded, "global");
      assert.equal(disk.getItem("resource-manager:frontend-preferences:v1")!.includes("readingSourceMode"), false);
      assert.deepEqual(await createFrontendPreferencesClient(disk).read(), expected);
    }
  }
});
