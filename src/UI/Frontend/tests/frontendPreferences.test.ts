import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { ResourceManager } from "../src/preferences/generated/frontendPoints.values.ts";
import { backendSettings, cachedOverlay, createFrontendPreferencesClient, pointOverlayPresentation, pointPreferences, resolveFrontendRefresh } from "../src/preferences/frontendPreferences.ts";
import { readDashboardPreferences } from "../src/preferences/dashboardPreferences.ts";
import { defaultAppSettings } from "../src/stores/settingsStore.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";

const points = ResourceManager.FrontendPoints;
function storage() {
  const values = new Map<string, string>();
  return { getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
    removeItem: (key: string) => { values.delete(key); } };
}

test("frontend changes survive restart; reset restores presentation while retaining module choices", async () => {
  const disk = storage();
  const first = createFrontendPreferencesClient(disk);
  assert.deepEqual(await first.read(), pointPreferences());
  await first.reset();
  assert.equal(disk.getItem("resource-manager:frontend-preferences:v1"), null);
  const changed = await first.read();
  changed.appearance.theme = "dark";
  changed.refresh.monitor = { mode: "custom", preset: "quiet", customValue: 4321 };
  changed.overlays.game = { ...pointOverlayPresentation(), enabled: true, mode: "injected", fontSizePx: 26, metrics: ["target.gpu", "target.cpu"] };
  await first.save(changed);
  const restarted = createFrontendPreferencesClient(disk);
  assert.deepEqual(await restarted.read(), changed);
  await restarted.reset();
  assert.deepEqual(await createFrontendPreferencesClient(disk).read(), {
    ...pointPreferences(), overlays: { game: { ...pointOverlayPresentation(), enabled: true, mode: "injected" } }
  });
  assert.equal((await restarted.read()).appearance.theme, points.Theme);
  assert.equal(pointOverlayPresentation().enabled, false);
  assert.equal(pointOverlayPresentation().mode, "external");
});

test("every refresh preset and custom interval resolve locally in both foreground/background profiles", () => {
  for (const key of ["monitor", "resourceTable", "localSystem"] as const) {
    for (const preset of ["responsive", "balanced", "lowPower", "quiet"] as const) {
      const defaults = pointPreferences();
      defaults.refresh[key] = { mode: "preset", preset, customValue: 1234 };
      const result = resolveFrontendRefresh(defaults.refresh);
      assert.equal(result.foreground[key], result.presets[key][preset]);
      assert.equal(result.backgroundOptimized[key], result.presets[key][preset]);
    }
    for (const customValue of [500, 4321, 300000]) {
      const defaults = pointPreferences();
      defaults.refresh[key] = { mode: "custom", preset: "balanced", customValue };
      const result = resolveFrontendRefresh(defaults.refresh);
      assert.equal(result.foreground[key], customValue);
      assert.equal(result.backgroundOptimized[key], customValue);
    }
  }
  const result = resolveFrontendRefresh(pointPreferences().refresh);
  assert.equal(result.foreground.localSystem, points.LocalSystemPresets[points.AutoForegroundPreset]);
  assert.equal(result.backgroundOptimized.localSystem, points.LocalSystemPresets[points.AutoBackgroundPreset]);
  assert.deepEqual(result.defaults.localSystem, points.LocalSystem);
});

test("concurrent scoped frontend writes preserve the other view's choices", async () => {
  const client = createFrontendPreferencesClient(storage());
  const old = await client.read();
  await Promise.all([
    client.save({ ...old, appearance: { ...old.appearance, theme: "dark" } }, "global"),
    client.save({ ...old, overlays: { GAME: { ...pointOverlayPresentation(), fontSizePx: 28 } } }, { overlay: "game" })
  ]);
  const current = await client.read();
  assert.equal(current.appearance.theme, "dark");
  assert.equal(current.overlays.game.fontSizePx, 28);
  assert.equal(cachedOverlay(current, "GAME")?.fontSizePx, 28);
  await client.save({ ...current, overlays: {} }, { overlay: "GAME" });
  assert.equal(cachedOverlay(await client.read(), "game"), undefined);
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

test("the notification section defaults to its points, saves with the global scope and survives the other scopes", async () => {
  assert.deepEqual(pointPreferences().notifications,
    { updateInApp: points.UpdateInApp, updateSystem: points.UpdateSystem, accessRequestSystem: points.AccessRequestSystem, updateInAppNotifiedVersion: null });
  assert.equal(points.UpdateInApp, "on");
  assert.equal(points.UpdateSystem, "on");
  assert.equal(points.AccessRequestSystem, "on");
  const disk = storage();
  // A cache saved before the section existed reads the section's points and keeps its other choices.
  const { notifications: _absent, ...older } = { ...pointPreferences(), appearance: { ...pointPreferences().appearance, theme: "dark" as const } };
  disk.setItem("resource-manager:frontend-preferences:v1", JSON.stringify(older));
  const client = createFrontendPreferencesClient(disk);
  const loaded = await client.read();
  assert.equal(loaded.appearance.theme, "dark");
  assert.deepEqual(loaded.notifications, pointPreferences().notifications);
  await client.save({ ...loaded, notifications: { updateInApp: "off", updateSystem: "on", accessRequestSystem: "off", updateInAppNotifiedVersion: "1.2.0" } }, "global");
  // Dashboard and overlay saves from views holding the old section do not write it.
  await client.save({ ...loaded, dashboard: null }, "dashboard");
  await client.save({ ...loaded, overlays: { game: pointOverlayPresentation() } }, { overlay: "game" });
  const current = await createFrontendPreferencesClient(disk).read();
  assert.deepEqual(current.notifications, { updateInApp: "off", updateSystem: "on", accessRequestSystem: "off", updateInAppNotifiedVersion: "1.2.0" });
  assert.ok(current.overlays.game);
  // The switches are frontend preferences and never enter an app-settings request.
  const settings = { ...defaultAppSettings(), notifications: { updateInApp: "off" as const, updateSystem: "off" as const, accessRequestSystem: "off" as const } };
  assert.equal("notifications" in backendSettings(settings), false);
});

test("an older notification section gets the access-request point without replacing existing choices or records", async () => {
  const disk = storage();
  disk.setItem("resource-manager:frontend-preferences:v1", JSON.stringify({ ...pointPreferences(),
    notifications: { updateInApp: "off", updateSystem: "off", updateInAppNotifiedVersion: "1.2.0" } }));
  const current = await createFrontendPreferencesClient(disk).read();
  assert.deepEqual(current.notifications, { updateInApp: "off", updateSystem: "off",
    accessRequestSystem: points.AccessRequestSystem, updateInAppNotifiedVersion: "1.2.0" });
});

test("the access-request point uses the notification group and an enabled on/off choice", () => {
  const declaration = JSON.parse(readFileSync(new URL("../preferences.points.json", import.meta.url), "utf8"));
  const update = declaration.points.find((point: { symbol: string }) => point.symbol === "UpdateSystem");
  const access = declaration.points.find((point: { symbol: string }) => point.symbol === "AccessRequestSystem");
  assert.deepEqual(access, { group: update.group, name: "notification-access_request_system", symbol: "AccessRequestSystem",
    type: "choice", options: ["on", "off"], value: "on" });
});

test("access-request notification titles and descriptions are translated in all selectable languages", async () => {
  const titles = new Set<string>();
  const descriptions = new Set<string>();
  for (const language of languageOptions) {
    if (language.id === "system") continue;
    const { updates } = await loadSettingsText(language.id);
    assert.ok(updates.accessRequestNoticeTitle.trim(), `${language.id}: title`);
    assert.ok(updates.accessRequestNoticeDescription.trim(), `${language.id}: description`);
    titles.add(updates.accessRequestNoticeTitle);
    descriptions.add(updates.accessRequestNoticeDescription);
  }
  assert.equal(titles.size, 9);
  assert.equal(descriptions.size, 9);
});

test("animation choices saved before the animation style migrate once (docs/animation_style_2026-10-26.md)", async () => {
  // auto → 自动关闭开，normal → 关，风格按原手感（smooth → smooth；standard、crisp、没有手感 → desktop）；none、ultra 照搬。
  const cases: Array<[string, string | undefined, string, boolean]> = [
    ["auto", "standard", "desktop", true], ["auto", "crisp", "desktop", true], ["auto", "smooth", "smooth", true], ["auto", undefined, "desktop", true],
    ["normal", "standard", "desktop", false], ["normal", "smooth", "smooth", false], ["normal", undefined, "desktop", false],
    ["none", "smooth", "none", points.AutoDisableAnimations], ["ultra", "crisp", "ultra", points.AutoDisableAnimations]
  ];
  for (const [animations, motionFeel, style, autoDisable] of cases) {
    const disk = storage();
    const { animationStyle: _style, autoDisableAnimations: _auto, ...appearance } = { ...pointPreferences().appearance, theme: "dark" as const };
    disk.setItem("resource-manager:frontend-preferences:v1",
      JSON.stringify({ ...pointPreferences(), appearance: { ...appearance, animations, ...(motionFeel ? { motionFeel } : {}) } }));
    const loaded = (await createFrontendPreferencesClient(disk).read()).appearance;
    assert.equal(loaded.animationStyle, style, `${animations}/${motionFeel}`);
    assert.equal(loaded.autoDisableAnimations, autoDisable, `${animations}/${motionFeel}`);
    assert.equal(loaded.theme, "dark");
    assert.equal("animations" in loaded || "motionFeel" in loaded, false, "old fields are dropped");
  }
  // A cache that already has the style only loses the old fields.
  const disk = storage();
  disk.setItem("resource-manager:frontend-preferences:v1", JSON.stringify({ ...pointPreferences(),
    appearance: { ...pointPreferences().appearance, animationStyle: "terminal", autoDisableAnimations: false, animations: "auto", motionFeel: "smooth" } }));
  const kept = (await createFrontendPreferencesClient(disk).read()).appearance;
  assert.deepEqual([kept.animationStyle, kept.autoDisableAnimations, "animations" in kept], ["terminal", false, false]);
});

test("the retired NVML v2 used field name is renamed in card slots and overlay sources on cache read", async () => {
  // Migration frontend-preferences-nvml-fb-used-v1: only the retired name changes; the v1 field, metric ids and everything else stay.
  const v1 = "gpu_nvidia_fb_used_v1_including_reserved_bytes";
  const preferences = (field: string) => ({ ...pointPreferences(),
    dashboard: { version: 17, usesFactoryDefaults: false, cards: [{ id: "gpu-card", slots: [
      { metricId: `${field}@gpu:a`, size: "medium" as const, binding: { scopeKind: "device-field", scopeKey: "gpu:a" } },
      { metricId: `${v1}@gpu:a`, size: "small" as const, binding: { scopeKind: "device-field", scopeKey: "gpu:a" } },
      { metricId: "gpu.0.nvmlFbUsedBytes", size: "small" as const, binding: { scopeKind: "gpu", scopeKey: "gpu:a" } }] }],
      resourceBars: [{ id: "gpu-bar", metricId: "gpu.0.vram", scaleMode: "capacity" as const }],
      resourceTableColumns: [], resourceTableProcessColumns: [] },
    overlays: { game: { ...pointOverlayPresentation(), enabled: true, metrics: ["gpu.vram", "gpu.vramV1"], metricSources: {
      "gpu.vram": { fieldName: field, deviceIdentity: "gpu:a" }, "gpu.vramV1": { fieldName: v1, deviceIdentity: "gpu:a" } } } } });
  const disk = storage();
  disk.setItem("resource-manager:frontend-preferences:v1", JSON.stringify(preferences("gpu_nvidia_fb_used_including_reserved_bytes")));
  const client = createFrontendPreferencesClient(disk);
  const expected = preferences("gpu_nvidia_fb_used_excluding_reserved_bytes");
  const loaded = await client.read();
  assert.deepEqual(loaded, expected);
  await client.save(loaded, "dashboard");
  await client.save(loaded, { overlay: "game" });
  assert.equal(disk.getItem("resource-manager:frontend-preferences:v1")!.includes("gpu_nvidia_fb_used_including_reserved_bytes"), false);
  assert.deepEqual(await createFrontendPreferencesClient(disk).read(), expected);
});

test("malformed card slots and overlay sources pass the NVML rename untouched and are left to the validators", async () => {
  // The rename runs before any validation: a null slot, a numeric metricId or a null overlay source must not fail the whole read.
  const malformed = { ...pointPreferences(), appearance: { ...pointPreferences().appearance, theme: "dark" as const },
    dashboard: { version: 17, usesFactoryDefaults: false, cards: [{ id: "broken", slots: [null, { metricId: 42, size: "small" }] }] },
    overlays: { game: { ...pointOverlayPresentation(), metrics: ["gpu.vram"], metricSources: { "gpu.vram": null } } } };
  const disk = storage();
  disk.setItem("resource-manager:frontend-preferences:v1", JSON.stringify(malformed));
  const client = createFrontendPreferencesClient(disk);
  assert.deepEqual(await client.read(), malformed, "the read returns the cache exactly as before the migration existed");
  await assert.rejects(readDashboardPreferences(undefined, client), /Invalid frontend dashboard cache/, "the dashboard validator still rejects only that section");
  // A well-formed retired slot or source next to malformed ones is still renamed; the malformed ones stay as they are.
  const mixedSlot = (field: string) => ({ metricId: `${field}@gpu:a`, size: "small" });
  const mixedSource = (field: string) => ({ fieldName: field, deviceIdentity: "gpu:a" });
  const mixed = (field: string) => ({ ...malformed,
    dashboard: { ...malformed.dashboard, cards: [{ id: "broken", slots: [...malformed.dashboard.cards[0].slots, mixedSlot(field)] }] },
    overlays: { game: { ...malformed.overlays.game, metrics: ["gpu.vram", "gpu.vramV2"],
      metricSources: { "gpu.vram": null, "gpu.vramV2": mixedSource(field) } } } });
  const mixedDisk = storage();
  mixedDisk.setItem("resource-manager:frontend-preferences:v1", JSON.stringify(mixed("gpu_nvidia_fb_used_including_reserved_bytes")));
  assert.deepEqual(await createFrontendPreferencesClient(mixedDisk).read(), mixed("gpu_nvidia_fb_used_excluding_reserved_bytes"));
});

test("retired reading-source choices are dropped on cache read and subsequent save", async () => {
  for (const readingSourceMode of ["preferred", "all", "unknown", null, 42, {}]) {
    for (const oldAnimation of [false, true]) {
      const disk = storage();
      const expected = { ...pointPreferences(), appearance: { ...pointPreferences().appearance, theme: "dark" as const } };
      const appearance = oldAnimation
        ? { ...expected.appearance, animationStyle: undefined, autoDisableAnimations: undefined, animations: "normal", motionFeel: "smooth" }
        : expected.appearance;
      disk.setItem("resource-manager:frontend-preferences:v1", JSON.stringify({ ...expected,
        appearance: { ...appearance, readingSourceMode } }));
      if (oldAnimation) {
        expected.appearance.animationStyle = "smooth";
        expected.appearance.autoDisableAnimations = false;
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
