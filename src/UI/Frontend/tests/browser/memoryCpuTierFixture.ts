// 模拟后端的内存与 CPU 取舍（docs/memory_cpu_tiers_2026-10-30.md §5）：一张小表覆盖字节（可不设）、毫秒、秒、条数、等级、选项六种取值，
// 其中两项重启后生效；GET 的每项值 = 单独设置 ?? 当前档位在 tiers[] 里的值。重启项的 runningValue 是模拟服务启动时用的值，固定不变，
// 生效值和它不同时 pendingRestart=true（页面显示“现在在用 / 重启后”）。
import type { MemoryCpuTierSettings, MemoryCpuTierRange, MemoryCpuTierUnit, MemoryCpuTierValue,
  MemoryCpuTierCategory, MemoryCpuTierSource, MemoryCpuTierApply, MemoryCpuTierStep } from "../../src/data/memoryCpuTier/memoryCpuTierTypes.ts";

export interface MemoryCpuTierFixtureDefinition {
  name: string; unit: MemoryCpuTierUnit | null; apply: MemoryCpuTierApply; runningValue?: MemoryCpuTierValue;
  range: MemoryCpuTierRange; tiers: [value: MemoryCpuTierValue, category: MemoryCpuTierCategory][];
}
export interface MemoryCpuTierFixtureOption {
  name: string; value: MemoryCpuTierValue; unit: MemoryCpuTierUnit | null; range: MemoryCpuTierRange;
  tiers: MemoryCpuTierStep[]; category: MemoryCpuTierCategory; source: MemoryCpuTierSource; apply: MemoryCpuTierApply;
  runningValue?: MemoryCpuTierValue; pendingRestart?: boolean;
}
const proportional = "proportional";
const aggressive = "aggressive";

export const memoryCpuTierOptionTable: MemoryCpuTierFixtureDefinition[] = [
  {
    name: "gcGen0MaxBudget", unit: "bytes", apply: "restart", runningValue: 2097152,
    range: { kind: "integer", min: 262144, max: 268435456, unset: true },
    tiers: [[262144, aggressive], [1048576, proportional], [4194304, proportional], [16777216, proportional], [null, aggressive]]
  },
  {
    name: "gcConserveMemory", unit: "level", apply: "restart", runningValue: 5,
    range: { kind: "integer", min: 0, max: 9, unset: false },
    tiers: [[9, aggressive], [7, proportional], [5, proportional], [3, proportional], [0, aggressive]]
  },
  {
    name: "ledgerMemoryModeFloor", unit: null, apply: "immediate",
    range: { kind: "choice", choices: ["optimize", "autoLean", "auto", "autoRetain", "unrestricted"] },
    tiers: [["optimize", proportional], ["autoLean", proportional], ["auto", proportional], ["autoRetain", proportional], ["unrestricted", proportional]]
  },
  {
    name: "browserRuntimeSnapshotSeconds", unit: "seconds", apply: "immediate",
    range: { kind: "integer", min: 0, max: 600, unset: false },
    tiers: [[0, proportional], [10, proportional], [30, proportional], [60, proportional], [300, proportional]]
  },
  {
    name: "singleRequestStopBufferMs", unit: "milliseconds", apply: "immediate",
    range: { kind: "integer", min: 0, max: 3600000, unset: false },
    tiers: [[0, aggressive], [5000, proportional], [10000, proportional], [30000, proportional], [60000, aggressive]]
  },
  {
    name: "rendererAbsenceEntries", unit: "count", apply: "immediate",
    range: { kind: "integer", min: 0, max: 4096, unset: false },
    tiers: [[0, proportional], [64, proportional], [256, proportional], [1024, proportional], [4096, proportional]]
  }
];

// 前端不认识的名称（后端新加的选项）：页面显示原名和“未知选项”。
const unknownOption: MemoryCpuTierFixtureDefinition = {
  name: "futureTradeOff", unit: "count", apply: "immediate",
  range: { kind: "integer", min: 1, max: 8, unset: false },
  tiers: [[1, proportional], [2, proportional], [4, proportional], [6, proportional], [8, proportional]]
};

export const memoryCpuTierScenarios = ["ready", "unknown-option", "malformed", "error"];

export function defaultMemoryCpuTierSettings(): MemoryCpuTierSettings {
  return { tier: 3, overrides: {} };
}

/** 按当前设置拼出 GET /api/settings/memory-cpu-tier 的回复；scenario 选表的变体。 */
export function memoryCpuTierReading(settings: MemoryCpuTierSettings, scenario: string) {
  const table = scenario === "unknown-option" ? [...memoryCpuTierOptionTable, unknownOption] : memoryCpuTierOptionTable;
  const tier = settings.tier;
  const options = table.map(definition => {
    const step = definition.tiers[tier - 1];
    const overridden = Object.hasOwn(settings.overrides, definition.name);
    const value = overridden ? settings.overrides[definition.name] : step[0];
    const option: MemoryCpuTierFixtureOption = {
      name: definition.name, value, unit: definition.unit, range: structuredClone(definition.range),
      tiers: definition.tiers.map(([stepValue, category]) => ({ value: stepValue, category })),
      category: step[1], source: overridden ? "user" : "tier", apply: definition.apply
    };
    if (definition.apply === "restart") {
      option.runningValue = definition.runningValue;
      option.pendingRestart = value !== definition.runningValue;
    }
    return option;
  });
  // 坏回复：第一项只有四档，前端解码必须失败并显示读取失败。
  if (scenario === "malformed") options[0].tiers.pop();
  return { tier, options };
}

/** 模拟后端对 PATCH 里 memoryCpuTier 的校验：返回出问题的路径，合法时返回 null。 */
export function invalidMemoryCpuTierSettings(value: unknown) {
  const path = "$.memoryCpuTier";
  if (value === null || typeof value !== "object" || Array.isArray(value)) return path;
  const settings = value as { tier?: unknown; overrides?: unknown };
  if (typeof settings.tier !== "number" || !Number.isInteger(settings.tier) || settings.tier < 1 || settings.tier > 5) return `${path}.tier`;
  const overrides = settings.overrides;
  if (overrides === null || typeof overrides !== "object" || Array.isArray(overrides)) return `${path}.overrides`;
  for (const [name, overrideValue] of Object.entries(overrides)) {
    const definition = memoryCpuTierOptionTable.find(item => item.name === name);
    if (!definition || !inRange(definition.range, overrideValue)) return `${path}.overrides.${name}`;
  }
  return null;
}

function inRange(range: MemoryCpuTierRange, value: unknown) {
  if (range.kind === "choice") return typeof value === "string" && range.choices.includes(value);
  if (value === null) return range.unset;
  return typeof value === "number" && Number.isSafeInteger(value) && value >= range.min && value <= range.max;
}
