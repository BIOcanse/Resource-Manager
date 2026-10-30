import assert from "node:assert/strict";
import {
  decodeMemoryCpuTierSettings,
  memoryCpuTierReadingDecoder
} from "../src/data/memoryCpuTier/memoryCpuTierDecoder.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";
import {
  buildMemoryCpuTierView,
  isMemoryCpuTierValueInRange,
  normalizeMemoryCpuTierSettings,
  withMemoryCpuTier,
  withMemoryCpuTierOverride,
  withoutMemoryCpuTierOverride
} from "../src/features/settings/memoryCpuTierModel.ts";
import { applySettingsPatch, createSettingsPatch } from "../src/features/settings/settingsPatch.ts";
import type { MemoryCpuTierReading } from "../src/data/memoryCpuTier/memoryCpuTierTypes.ts";

type Json = Record<string, unknown>;

function steps(values: unknown[], categories: string[] = ["aggressive", "proportional", "proportional", "proportional", "aggressive"]) {
  return values.map((value, index) => ({ value, category: categories[index] }));
}

function validReading(): Json {
  return {
    tier: 3,
    options: [
      {
        name: "gcGen0MaxBudget", value: 4194304, unit: "bytes",
        range: { kind: "integer", min: 262144, max: 268435456, unset: true },
        tiers: steps([262144, 1048576, 4194304, 16777216, null]),
        category: "proportional", source: "tier", apply: "restart",
        runningValue: 2097152, pendingRestart: true
      },
      {
        name: "browserRuntimeSnapshotSeconds", value: 30, unit: "seconds",
        range: { kind: "integer", min: 0, max: 600, unset: false },
        tiers: steps([0, 10, 30, 60, 300]),
        category: "proportional", source: "tier", apply: "immediate"
      },
      {
        name: "ledgerMemoryModeFloor", value: "auto", unit: null,
        range: { kind: "choice", choices: ["optimize", "autoLean", "auto", "autoRetain", "unrestricted"] },
        tiers: steps(["optimize", "autoLean", "auto", "autoRetain", "unrestricted"]),
        category: "proportional", source: "tier", apply: "immediate"
      }
    ]
  };
}

function mutated(edit: (reading: Json & { options: Json[] }) => void): Json {
  const reading = validReading() as Json & { options: Json[] };
  edit(reading);
  return reading;
}

// 合法回复：原样解出，restart 项带 runningValue 和 pendingRestart，immediate 项没有。
const decoded = memoryCpuTierReadingDecoder.decode(validReading());
assert.equal(decoded.tier, 3);
assert.equal(decoded.options.length, 3);
const restartOption = decoded.options[0]!;
assert.equal(restartOption.apply, "restart");
assert.equal(restartOption.apply === "restart" && restartOption.runningValue, 2097152);
assert.equal(restartOption.apply === "restart" && restartOption.pendingRestart, true);
assert.equal(Object.hasOwn(decoded.options[1]!, "runningValue"), false);
assert.deepEqual(decoded.options[2]!.range, { kind: "choice", choices: ["optimize", "autoLean", "auto", "autoRetain", "unrestricted"] });
// 未知名称照样解出：页面自己标“未知选项”。
assert.equal(memoryCpuTierReadingDecoder.decode(mutated(reading => { reading.options[1]!.name = "futureOption"; })).options[1]!.name, "futureOption");
// unset 允许时 value 可以是 null；source=user 时 value 不必等于档位值。
assert.equal(memoryCpuTierReadingDecoder.decode(mutated(reading => {
  reading.options[0]!.value = null; reading.options[0]!.source = "user";
})).options[0]!.value, null);
assert.equal(memoryCpuTierReadingDecoder.decode(mutated(reading => {
  reading.options[1]!.value = 120; reading.options[1]!.source = "user";
})).options[1]!.value, 120);
// 空选项表是合法回复。
assert.deepEqual(memoryCpuTierReadingDecoder.decode({ tier: 1, options: [] }).options, []);

const invalidCases: Array<[string, unknown]> = [
  ["not a record", []],
  ["tier missing", mutated(reading => { delete reading.tier; })],
  ["tier zero", mutated(reading => { reading.tier = 0; })],
  ["tier six", mutated(reading => { reading.tier = 6; })],
  ["tier fraction", mutated(reading => { reading.tier = 2.5; })],
  ["options not array", mutated(reading => { reading.options = {} as unknown as Json[]; })],
  ["option not record", mutated(reading => { reading.options[0] = "x" as unknown as Json; })],
  ["empty name", mutated(reading => { reading.options[0]!.name = ""; })],
  ["duplicate name", mutated(reading => { reading.options[1]!.name = "gcGen0MaxBudget"; })],
  ["unknown unit", mutated(reading => { reading.options[1]!.unit = "minutes"; })],
  ["unit missing", mutated(reading => { delete reading.options[1]!.unit; })],
  ["range missing", mutated(reading => { delete reading.options[1]!.range; })],
  ["unknown range kind", mutated(reading => { reading.options[1]!.range = { kind: "float", min: 0, max: 1, unset: false }; })],
  ["min above max", mutated(reading => { reading.options[1]!.range = { kind: "integer", min: 10, max: 1, unset: false }; })],
  ["fractional min", mutated(reading => { reading.options[1]!.range = { kind: "integer", min: 0.5, max: 600, unset: false }; })],
  ["unset not boolean", mutated(reading => { reading.options[1]!.range = { kind: "integer", min: 0, max: 600, unset: "no" }; })],
  ["integer range with choices", mutated(reading => { reading.options[1]!.range = { kind: "integer", min: 0, max: 600, unset: false, choices: ["a"] }; })],
  ["empty choices", mutated(reading => { reading.options[2]!.range = { kind: "choice", choices: [] }; })],
  ["duplicate choices", mutated(reading => { reading.options[2]!.range = { kind: "choice", choices: ["auto", "auto"] }; })],
  ["choice range with min", mutated(reading => { reading.options[2]!.range = { kind: "choice", choices: ["auto"], min: 0 }; })],
  ["value above max", mutated(reading => { reading.options[1]!.value = 601; reading.options[1]!.source = "user"; })],
  ["value below min", mutated(reading => { reading.options[1]!.value = -1; reading.options[1]!.source = "user"; })],
  ["null without unset", mutated(reading => { reading.options[1]!.value = null; reading.options[1]!.source = "user"; })],
  ["string on integer range", mutated(reading => { reading.options[1]!.value = "30"; reading.options[1]!.source = "user"; })],
  ["choice not listed", mutated(reading => { reading.options[2]!.value = "never"; reading.options[2]!.source = "user"; })],
  ["null on choice range", mutated(reading => { reading.options[2]!.value = null; reading.options[2]!.source = "user"; })],
  ["four tiers", mutated(reading => { (reading.options[1]!.tiers as unknown[]).pop(); })],
  ["six tiers", mutated(reading => { (reading.options[1]!.tiers as unknown[]).push({ value: 1, category: "proportional" }); })],
  ["tier value out of range", mutated(reading => { (reading.options[1]!.tiers as Json[])[4]!.value = 9999; })],
  ["tier category unknown", mutated(reading => { (reading.options[1]!.tiers as Json[])[0]!.category = "extreme"; })],
  ["category unknown", mutated(reading => { reading.options[1]!.category = "extreme"; })],
  ["category differs from current tier", mutated(reading => { reading.options[1]!.category = "aggressive"; })],
  ["source unknown", mutated(reading => { reading.options[1]!.source = "default"; })],
  ["source tier with other value", mutated(reading => { reading.options[1]!.value = 60; })],
  ["apply unknown", mutated(reading => { reading.options[1]!.apply = "later"; })],
  ["immediate with runningValue", mutated(reading => { reading.options[1]!.runningValue = 30; })],
  ["immediate with pendingRestart", mutated(reading => { reading.options[1]!.pendingRestart = false; })],
  ["restart without runningValue", mutated(reading => { delete reading.options[0]!.runningValue; })],
  ["restart without pendingRestart", mutated(reading => { delete reading.options[0]!.pendingRestart; })],
  ["pendingRestart not boolean", mutated(reading => { reading.options[0]!.pendingRestart = "yes"; })],
  ["runningValue wrong shape", mutated(reading => { reading.options[0]!.runningValue = "big"; })]
];
for (const [label, value] of invalidCases) {
  assert.throws(() => memoryCpuTierReadingDecoder.decode(value), ResponseDecodeError, label);
}

// 设置里的 memoryCpuTier：tier 1..5，overrides 的值是整数、字符串或 null。
assert.deepEqual(decodeMemoryCpuTierSettings({ tier: 2, overrides: { a: 1, b: "auto", c: null } }, "settings.memoryCpuTier"),
  { tier: 2, overrides: { a: 1, b: "auto", c: null } });
for (const [label, value] of [
  ["settings not record", 3],
  ["settings tier out of range", { tier: 7, overrides: {} }],
  ["settings overrides missing", { tier: 3 }],
  ["settings override boolean", { tier: 3, overrides: { a: true } }],
  ["settings override fraction", { tier: 3, overrides: { a: 1.5 } }],
  ["settings empty override name", { tier: 3, overrides: { "": 1 } }]
] as Array<[string, unknown]>) {
  assert.throws(() => decodeMemoryCpuTierSettings(value, "settings.memoryCpuTier"), ResponseDecodeError, label);
}

// 归一化：键排序、坏档位回到默认 3。
assert.deepEqual(normalizeMemoryCpuTierSettings({ tier: 9, overrides: { b: 1, a: "x" } }), { tier: 3, overrides: { a: "x", b: 1 } });
assert.deepEqual(normalizeMemoryCpuTierSettings(undefined), { tier: 3, overrides: {} });

// 切档不清单独设置；清除只去掉一项。
const withOverride = withMemoryCpuTierOverride({ tier: 3, overrides: {} }, "browserRuntimeSnapshotSeconds", 120);
assert.deepEqual(withMemoryCpuTier(withOverride, 5), { tier: 5, overrides: { browserRuntimeSnapshotSeconds: 120 } });
assert.deepEqual(withoutMemoryCpuTierOverride(withOverride, "browserRuntimeSnapshotSeconds"), { tier: 3, overrides: {} });

// 范围判断：范围外、错类型、不允许的 null 都不是合法值。
const integerRange = { kind: "integer", min: 0, max: 600, unset: false } as const;
assert.equal(isMemoryCpuTierValueInRange(integerRange, 600), true);
assert.equal(isMemoryCpuTierValueInRange(integerRange, 601), false);
assert.equal(isMemoryCpuTierValueInRange(integerRange, 1.5), false);
assert.equal(isMemoryCpuTierValueInRange(integerRange, null), false);
assert.equal(isMemoryCpuTierValueInRange({ ...integerRange, unset: true }, null), true);
assert.equal(isMemoryCpuTierValueInRange({ kind: "choice", choices: ["auto"] }, "auto"), true);
assert.equal(isMemoryCpuTierValueInRange({ kind: "choice", choices: ["auto"] }, "never"), false);

// 视图：草稿等于已保存时显示后端值；不同时按 tiers[] 预览。
const reading: MemoryCpuTierReading = memoryCpuTierReadingDecoder.decode(validReading());
const saved = { tier: 3, overrides: {} };
const steady = buildMemoryCpuTierView(reading, saved, saved);
assert.equal(steady.preview, false);
assert.equal(steady.tier, 3);
assert.equal(steady.rows[0]!.value, 4194304);
assert.equal(steady.rows[0]!.showRunningValue, true, "pendingRestart shows both values");
assert.equal(steady.rows[1]!.showRunningValue, false);
const draftFive = buildMemoryCpuTierView(reading, { tier: 5, overrides: {} }, saved);
assert.equal(draftFive.preview, true);
assert.equal(draftFive.tier, 5);
assert.equal(draftFive.rows[0]!.value, null);
assert.equal(draftFive.rows[0]!.category, "aggressive");
assert.equal(draftFive.rows[1]!.value, 300);
assert.equal(draftFive.rows[2]!.value, "unrestricted");
const draftOverride = buildMemoryCpuTierView(reading, { tier: 1, overrides: { browserRuntimeSnapshotSeconds: 45 } }, saved);
assert.equal(draftOverride.rows[1]!.value, 45);
assert.equal(draftOverride.rows[1]!.source, "user");
assert.equal(draftOverride.rows[1]!.overridden, true);
assert.equal(draftOverride.rows[0]!.source, "tier");

// 补丁：memoryCpuTier 整节替换，去掉一项单独设置也能表达。
const baseline = { version: "1", memoryCpuTier: { tier: 3, overrides: { a: 1, b: 2 } }, debug: { x: 1, y: 2 } };
const draft = { version: "1", memoryCpuTier: { tier: 3, overrides: { b: 2 } }, debug: { x: 1, y: 3 } };
const patch = createSettingsPatch(baseline, draft);
assert.deepEqual(patch, { memoryCpuTier: { tier: 3, overrides: { b: 2 } }, debug: { y: 3 } });
assert.deepEqual(applySettingsPatch(baseline, patch), draft);
assert.deepEqual(createSettingsPatch(baseline, structuredClone(baseline)), {});

console.log("memoryCpuTier tests passed");
