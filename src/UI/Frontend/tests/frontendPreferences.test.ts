import assert from "node:assert/strict";
import { test } from "node:test";
import { ResourceManager } from "../src/preferences/generated/frontendPoints.values.ts";
import { backendSettings, cachedOverlay, createFrontendPreferencesClient, pointOverlayPresentation, pointPreferences, resolveFrontendRefresh } from "../src/preferences/frontendPreferences.ts";
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

test("a missing frontend cache uses generated point values; a change survives restart and reset removes it", async () => {
  const disk = storage();
  const first = createFrontendPreferencesClient(disk);
  assert.deepEqual(await first.read(), pointPreferences());
  const changed = await first.read();
  changed.appearance.theme = "dark";
  changed.refresh.monitor = { mode: "custom", preset: "quiet", customValue: 4321 };
  changed.overlays.game = { ...pointOverlayPresentation(), fontSizePx: 26, metrics: ["target.gpu", "target.cpu"] };
  await first.save(changed);
  const restarted = createFrontendPreferencesClient(disk);
  assert.deepEqual(await restarted.read(), changed);
  await restarted.reset();
  assert.deepEqual(await createFrontendPreferencesClient(disk).read(), pointPreferences());
  assert.equal((await restarted.read()).appearance.theme, points.Theme);
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
