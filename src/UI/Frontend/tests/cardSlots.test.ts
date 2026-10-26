// 监视卡片的槽位规则（features/monitor/cardSlots.ts、stores/monitorConfig.ts）、旧布局迁移、趋势记录和九种语言的新文字。
import assert from "node:assert/strict";
import { cardUnitLimit, cardUnits, legacyCardSlots, remainingUnits, sizesThatFit, slotUnits, usedUnits } from "../src/features/monitor/cardSlots.ts";
import { appendCardTrends, cardTrendCapacity, cardTrendPaths, cardTrendRange } from "../src/features/monitor/cardTrends.ts";
import { addCardSlot, moveCardSlot, normalizeCards, removeCardSlot, replaceCardSlotMetric, resizeCardSlot } from "../src/stores/monitorConfig.ts";
import { readDashboardPreferences, saveDashboardPreferences } from "../src/preferences/dashboardPreferences.ts";
import { createFrontendPreferencesClient } from "../src/preferences/frontendPreferences.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import type { DashboardCardSettings, DashboardCardSlot, DashboardSlotSize, MetricValue } from "../src/types.ts";

const slot = (metricId: string, size: DashboardSlotSize): DashboardCardSlot => ({ metricId, size, binding: null });
const card = (id: string, ...slots: DashboardCardSlot[]): DashboardCardSettings => ({ id, slots });

// 单位：最小 1、中 2、大 3、超大占满（6）。
assert.equal(cardUnits, 6);
assert.deepEqual((["small", "medium", "large", "full"] as const).map(slotUnits), [1, 2, 3, 6]);

// 放得下的大小：新增只看剩余；改大小时这个槽位自己的单位也算可用。
assert.deepEqual(sizesThatFit([]), ["small", "medium", "large", "full"]);
const three = [slot("a", "medium"), slot("b", "small")];
assert.equal(usedUnits(three), 3);
assert.equal(remainingUnits(three), 3);
assert.deepEqual(sizesThatFit(three), ["small", "medium", "large"], "full does not fit next to other slots");
assert.deepEqual(sizesThatFit([slot("a", "large"), slot("b", "medium")]), ["small"]);
assert.deepEqual(sizesThatFit([slot("a", "large"), slot("b", "large")]), [], "a full card offers no size, so it shows no +");
assert.deepEqual(sizesThatFit([slot("a", "large"), slot("b", "medium")], 1), ["small", "medium", "large"], "a slot can grow into the remaining unit");
assert.deepEqual(sizesThatFit([slot("a", "full")], 0), ["small", "medium", "large", "full"]);
assert.deepEqual(sizesThatFit([slot("a", "large"), slot("b", "large")], 0), ["small", "medium", "large"]);

// 编辑：放不下时原样返回同一份列表。
const cards = [card("x", slot("a", "medium"), slot("b", "small")), card("y", slot("c", "large"), slot("d", "medium"))];
assert.deepEqual(addCardSlot(cards, "x", slot("n", "large"))[0].slots.map((item) => item.size), ["medium", "small", "large"]);
assert.equal(addCardSlot(cards, "x", slot("n", "full")), cards, "a size that does not fit is not added");
assert.equal(addCardSlot(cards, "y", slot("n", "medium")), cards);
assert.deepEqual(resizeCardSlot(cards, { cardId: "x", index: 1 }, "large")[0].slots[1], slot("b", "large"));
assert.equal(resizeCardSlot(cards, { cardId: "x", index: 1 }, "full"), cards, "resizing past the remaining units is refused");
assert.equal(resizeCardSlot(cards, { cardId: "x", index: 1 }, "small"), cards, "the same size is no change");
assert.deepEqual(resizeCardSlot(cards, { cardId: "y", index: 0 }, "small")[1].slots.map((item) => item.size), ["small", "medium"], "shrinking always fits");
assert.deepEqual(replaceCardSlotMetric(cards, { cardId: "y", index: 0 }, "z", { scopeKind: "gpu", scopeKey: "k" })[1].slots[0],
  { metricId: "z", size: "large", binding: { scopeKind: "gpu", scopeKey: "k" } }, "replacing keeps the size and takes the new binding");
assert.deepEqual(removeCardSlot(cards, { cardId: "x", index: 0 })[0].slots, [slot("b", "small")]);
assert.equal(cards[0].slots.length, 2, "edits do not mutate the input");
// 拖动：同一张卡片里移到目标的位置；到别的卡片放到目标前面，要放得下。
assert.deepEqual(moveCardSlot(cards, { cardId: "x", index: 0 }, { cardId: "x", index: 1 })[0].slots.map((item) => item.metricId), ["b", "a"]);
assert.deepEqual(moveCardSlot(cards, { cardId: "x", index: 1 }, { cardId: "x", index: 0 })[0].slots.map((item) => item.metricId), ["b", "a"]);
const intoX = moveCardSlot(cards, { cardId: "y", index: 1 }, { cardId: "x", index: 1 });
assert.deepEqual(intoX.map((item) => item.slots.map((entry) => entry.metricId)), [["a", "d", "b"], ["c"]]);
assert.equal(moveCardSlot(cards, { cardId: "x", index: 0 }, { cardId: "y", index: 0 }), cards, "a slot that does not fit the other card stays");

// 旧布局迁移：主读数是“中”放最前，每行是“最小”依次往后，绑定跟着走，空的项不要；单位可能超过 6，原样保留。
const binding = { scopeKind: "gpu", scopeKey: "luid-a" };
const legacy = { id: "gpu0", main: "gpu.0.usage", mainBinding: binding, small: ["gpu.0.power", " ", "gpu.0.temperature"],
  smallBindings: [binding, null, null] };
assert.deepEqual(legacyCardSlots(legacy), [
  { metricId: "gpu.0.usage", binding, size: "medium" },
  { metricId: "gpu.0.power", binding, size: "small" },
  { metricId: "gpu.0.temperature", binding: null, size: "small" }
]);
assert.deepEqual(legacyCardSlots({ id: "empty", main: null, small: ["cpu.usage"] }), [{ metricId: "cpu.usage", binding: null, size: "small" }]);
const crowded = legacyCardSlots({ id: "gpu", main: "m", small: ["1", "2", "3", "4", "5", "6"] });
assert.equal(usedUnits(crowded), 8);
assert.equal(cardUnitLimit, 8, "the cache accepts a migrated card with a main reading and the most rows");
assert.deepEqual(sizesThatFit(crowded), [], "a migrated card over six units has no + ...");
assert.deepEqual(sizesThatFit(crowded, 0), ["small", "medium"], "... and can only shrink or keep its slots");
assert.equal(normalizeCards([card("keep", ...crowded)])[0].slots.length, 7, "saving keeps a migrated card as it is");
assert.deepEqual(normalizeCards([card("gone"), card("x", slot(" ", "small"))]), [], "cards without slots are not saved");

// 缓存：旧格式读入后是迁移后的槽位；槽位格式原样往返；超过 cardUnitLimit 的卡片不写。
const contents = new Map<string, string>();
const client = createFrontendPreferencesClient({ getItem: (key: string) => contents.get(key) ?? null,
  setItem: (key: string, value: string) => { contents.set(key, value); }, removeItem: (key: string) => { contents.delete(key); } });
const base = (await readDashboardPreferences([], client)).settings;
const key = [...contents.keys()][0] ?? "resource-manager:frontend-preferences:v1";
const stored = await client.read();
// 缓存校验照旧拒绝空白的行，所以缓存里的旧卡片不带空白项。
const cachedLegacy = { ...legacy, small: ["gpu.0.power", "gpu.0.temperature"], smallBindings: [binding, null] };
contents.set(key, JSON.stringify({ ...stored, dashboard: { ...base, version: 16, cards: [cachedLegacy] } }));
const migrated = (await readDashboardPreferences([], client)).settings.cards;
assert.deepEqual(migrated, [{ id: "gpu0", slots: legacyCardSlots(cachedLegacy) }]);
assert.deepEqual(migrated[0].slots, legacyCardSlots(legacy), "the same card with or without the blank row migrates the same");
await saveDashboardPreferences({ ...base, cards: migrated }, client);
assert.deepEqual((await readDashboardPreferences([], client)).settings.cards, migrated, "slots round-trip the cache");
await assert.rejects(saveDashboardPreferences({ ...base, cards: [card("over", ...crowded, slot("x", "small"))] }, client));

// 趋势：只记 ids 里的监控项，只在送它的那一路来帧时记一点，最多 cardTrendCapacity 点；没有读数记空点。
const reading = (numericValue: number | null, status = 1) => ({ numericValue, status, displayValue: numericValue === null ? null : `${numericValue}%` } as unknown as MetricValue);
let trends = appendCardTrends(new Map(), new Set(["a", "b"]), (id) => id === "a", { a: reading(10) });
assert.deepEqual(trends.get("a")?.map((point) => point.value), [10]);
assert.deepEqual(trends.get("b"), []);
trends = appendCardTrends(trends, new Set(["a"]), () => true, { a: reading(null) });
assert.deepEqual(trends.get("a")?.map((point) => point.value), [10, null]);
assert.equal(trends.has("b"), false, "a metric no longer in a large or full slot is dropped");
for (let index = 0; index < cardTrendCapacity + 5; index += 1) trends = appendCardTrends(trends, new Set(["a"]), () => true, { a: reading(index) });
assert.equal(trends.get("a")?.length, cardTrendCapacity);
const points = [{ value: 20, display: "20%" }, { value: null, display: "" }, { value: 80, display: "80%" }, { value: 50, display: "50%" }];
assert.deepEqual(cardTrendRange(points), { low: points[0], high: points[2] });
const paths = cardTrendPaths(points, true);
assert.equal((paths.line.match(/M/g) ?? []).length, 2, "a missing reading breaks the line");
assert.match(paths.line, /M94\.32 80\.00H94\.92/, "a single point between gaps is drawn as a short dash on the percent axis");
assert.deepEqual(cardTrendPaths([], false), { line: "", area: "" });

// 九种语言的新文字：都有、带上参数、非英文的不照抄英文。
const languages = ["zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES", "ru-RU"] as const;
const english = (await loadAppCopy("en-US")).dashboard;
const sample = (text: typeof english) => [
  text.slotLabel(3, 2), text.addSlot(3), text.resizeSlot("<slot>", "<metric>"), text.trendLabel("<label>", 42),
  text.currentSize, text.trendLow, text.trendHigh,
  ...(["small", "medium", "large", "full"] as const).flatMap((size) => [text.slotSize[size], text.slotSizeHint[size]])
];
for (const language of languages) {
  const text = (await loadAppCopy(language)).dashboard;
  const values = sample(text);
  assert.ok(values.every((value) => typeof value === "string" && value.trim().length > 0), `${language}: every new string is present`);
  assert.match(text.slotLabel(3, 2), /3[\s\S]*2|2[\s\S]*3/, `${language}: slot label names the card and the item`);
  assert.match(text.addSlot(3), /3/);
  assert.ok(text.resizeSlot("<slot>", "<metric>").includes("<slot>") && text.resizeSlot("<slot>", "<metric>").includes("<metric>"));
  assert.ok(text.trendLabel("<label>", 42).includes("<label>") && text.trendLabel("<label>", 42).includes("42"));
  assert.equal(new Set(["small", "medium", "large", "full"].map((size) => text.slotSize[size as DashboardSlotSize])).size, 4, `${language}: four distinct size names`);
  if (language !== "en-US") {
    const same = values.filter((value, index) => value === sample(english)[index]);
    assert.deepEqual(same, [], `${language}: translated, not copied from English`);
  }
}

console.log("cardSlots: ok");
