import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dashboardMetricAvailable, dashboardPointValues, dashboardSettingsVersion, readDashboardPreferences, resetDashboardPreferences, resolveDashboardBindings, saveDashboardPreferences } from "../src/preferences/dashboardPreferences.ts";
import { createFrontendPreferencesClient, pointPreferences } from "../src/preferences/frontendPreferences.ts";
import { legacyCardSlots } from "../src/features/monitor/cardSlots.ts";
import type { DashboardSettings, LegacyDashboardCardSettings, MetricDefinition } from "../src/types.ts";

type Case = { name: string; ids: string[] | null; identities?: Record<string, string> | null; settings: DashboardSettings };
const cases = JSON.parse(readFileSync(new URL("../../../../tests/Fixtures/FrontendPreferencesLegacy/dashboard-default-cases.json", import.meta.url), "utf8")) as Case[];
const catalog = (item: Case): MetricDefinition[] | undefined => item.ids?.map(id => {
  const index = /^gpu\.(\d+)\./.exec(id)?.[1];
  return { id, label: id, group: "", unit: "", selectable: true,
    ...(index && item.identities?.[index] ? { scopeKind: "gpu", scopeKey: item.identities[index] } : {}) };
}) ?? undefined;
// 固定样例记的是有槽位之前的出厂卡片（一个主读数加若干行）：展开成槽位后要和现在的出厂值一致，主读数是“中”在最前，每行是“最小”。
for (const item of cases) {
  const actual = dashboardPointValues(catalog(item));
  const legacyCards = item.settings.cards as unknown as LegacyDashboardCardSettings[];
  assert.deepEqual({ ...actual, usesFactoryDefaults: false },
    { ...item.settings, version: dashboardSettingsVersion, cards: legacyCards.map(card => ({ id: card.id, slots: legacyCardSlots(card) })) }, item.name);
}

const contents = new Map<string, string>();
const storage = { getItem: (key: string) => contents.get(key) ?? null,
  setItem: (key: string, value: string) => { contents.set(key, value); }, removeItem: (key: string) => { contents.delete(key); } };
let client = createFrontendPreferencesClient(storage);
const initial = await readDashboardPreferences(undefined, client);
assert.deepEqual(initial.settings, dashboardPointValues());
assert.equal(initial.source.kind, "pointValues");
assert.equal(contents.size, 0, "Reading factory values must not commit a layout cache");
const custom = { ...initial.settings, cards: [], resourceBars: [], resourceTableColumns: [{ id: "name", visible: true, width: 311 }], resourceTableProcessColumns: [] };
await saveDashboardPreferences(custom, client);
client = createFrontendPreferencesClient(storage);
assert.deepEqual((await readDashboardPreferences([], client)).settings.cards, [], "Explicit empty cards survive restart");
assert.deepEqual((await readDashboardPreferences([], client)).settings.resourceBars, []);
const saved = await client.read();
await client.save({ ...saved, appearance: { ...saved.appearance, language: "ja-JP" }, dashboard: null }, "global");
assert.equal((await client.read()).dashboard?.resourceTableColumns[0].width, 311, "Global saves cannot overwrite the dashboard scope");
await resetDashboardPreferences(client);
assert.equal((await client.read()).appearance.language, "ja-JP");
assert.deepEqual((await readDashboardPreferences(undefined, client)).settings, dashboardPointValues());

// A cached column without a width takes its own column set's template width; GPU columns resolve through the {index} template.
{
  const unsized = (ids: string[]) => ids.map(id => ({ id, visible: true }));
  await saveDashboardPreferences({ ...initial.settings, resourceTableColumns: unsized(["memory", "gpu.3.vram", "pid", "custom.column"]),
    resourceTableProcessColumns: unsized(["pid", "gpu.1.usage"]) } as DashboardSettings, client);
  const widths = (await readDashboardPreferences([], client)).settings;
  const { ResourceManager } = await import("../src/preferences/generated/frontendPoints.values.ts");
  const fallback = ResourceManager.FrontendPoints.DashboardLayoutLimits.defaultColumnWidth;
  assert.deepEqual(widths.resourceTableColumns.map(column => column.width), [110, 132, fallback, fallback],
    "software columns use the software templates; a process-only column is not a software template");
  assert.deepEqual(widths.resourceTableProcessColumns.map(column => column.width), [76, 108]);
  await resetDashboardPreferences(client);
}

// Existing Phase A browser caches have no dashboard entry.
const phaseA = pointPreferences(); delete phaseA.dashboard;
await client.save(phaseA);
assert.deepEqual((await readDashboardPreferences(undefined, client)).settings, dashboardPointValues());
const gpu = cases.find(item => item.identities)!;
const bound = dashboardPointValues(catalog(gpu));
const moved = catalog(gpu)!.map(metric => ({ ...metric, id: metric.id.replace(/^gpu\.0\./, "gpu.4.") }));
const resolved = resolveDashboardBindings(bound, moved);
assert.equal(resolved.cards.find(card => card.id === "gpu0")?.slots[0].metricId, "gpu.4.usage");
assert.equal(bound.cards.find(card => card.id === "gpu0")?.slots[0].metricId, "gpu.0.usage", "Resolution cannot mutate cached choices");
assert.equal(dashboardMetricAvailable("gpu.0.usage", { scopeKind: "gpu", scopeKey: "gpu-a" }, moved), false);
assert.equal(dashboardMetricAvailable("gpu.4.usage", { scopeKind: "gpu", scopeKey: "gpu-a" }, moved), true);
console.log("Dashboard HEAD defaults, cache loss/restart/reset/scopes and stable bindings passed");

// Existing widths/order/visibility/source survive adding arbitrary process columns.
const oldColumns = [{ id: "memory", visible: false, source: "process_all_private_commit_actual" },
  { id: "process_all_handle_count_actual", visible: true, width: 180, source: null }];
await saveDashboardPreferences({ ...dashboardPointValues(), resourceTableColumns: oldColumns,
  resourceTableProcessColumns: [] }, client);
const migrated = (await readDashboardPreferences([], client)).settings;
assert.deepEqual(migrated.resourceTableColumns.map(({ id, visible, source }) => ({ id, visible, source })),
  oldColumns.map(({ id, visible, source }) => ({ id, visible, source })));
assert.equal(migrated.resourceTableColumns[0].width, 110);
assert.equal(migrated.resourceTableColumns[1].width, 180);
assert.deepEqual(migrated.resourceTableProcessColumns, [], "An explicitly empty view is not reset");
await resetDashboardPreferences(client);
