import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireNonEmptyString,
  requireOneOf,
  requireRecord,
  requireSafeInteger,
  requireString,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import {
  memoryCpuTierApplyModes,
  memoryCpuTierCategories,
  memoryCpuTierCount,
  memoryCpuTierRangeKinds,
  memoryCpuTierSources,
  memoryCpuTierUnits,
  type MemoryCpuTierOption,
  type MemoryCpuTierRange,
  type MemoryCpuTierReading,
  type MemoryCpuTierSettings,
  type MemoryCpuTierStep,
  type MemoryCpuTierValue
} from "./memoryCpuTierTypes.ts";

// 读回复的校验规则（§5）：
// - tier 是 1..5 的整数；options 是数组，name 非空且不重复。
// - unit 是 bytes/milliseconds/seconds/count/level 之一或 null。
// - range 只有两种形状：integer {min ≤ max 的安全整数, unset 布尔} 或 choice {非空、不重复的字符串数组}。
// - value 和 tiers[].value 都在 range 里：integer 是 [min, max] 内的整数，choice 是 choices 之一；
//   null 只在 integer 且 unset 为 true 时允许。
// - tiers 恰好 5 项；category 等于当前档 tiers[tier-1].category；source 为 tier 时 value 等于当前档的值。
// - category/source/apply 是各自的枚举值之一。
// - runningValue 和 pendingRestart 恰好在 apply 为 restart 时出现：runningValue 是运行时实际在用的值，
//   形状和 value 相同（整数或 choices 之一，null 只在 unset 时），但不要求落在可编辑范围里。
export const memoryCpuTierReadingDecoder = defineResponseDecoder<MemoryCpuTierReading>(
  "settings.memory-cpu-tier.v1", value => {
    const record = requireRecord(value);
    const tier = requireTierNumber(record.tier, "$.tier");
    const seen = new Set<string>();
    const options = requireArray(record.options, "$.options").map((item, index) => {
      const option = decodeOption(item, `$.options[${index}]`, tier);
      if (seen.has(option.name)) throw new ResponseDecodeError(`$.options[${index}].name`, "unique option name");
      seen.add(option.name);
      return option;
    });
    return Object.freeze({ tier, options: Object.freeze(options) });
  });

/** 用户设置里的 memoryCpuTier 一节：{ tier: 1..5, overrides: { 名字: 整数 | 字符串 | null } }。 */
export function decodeMemoryCpuTierSettings(value: unknown, path: string): MemoryCpuTierSettings {
  const record = requireRecord(value, path);
  const overridesRecord = requireRecord(record.overrides, `${path}.overrides`);
  const overrides: Record<string, MemoryCpuTierValue> = {};
  for (const [name, overrideValue] of Object.entries(overridesRecord)) {
    const valuePath = `${path}.overrides.${name}`;
    if (name.trim().length === 0) throw new ResponseDecodeError(valuePath, "non-empty option name");
    if (overrideValue === null || typeof overrideValue === "string") {
      overrides[name] = overrideValue;
    } else {
      overrides[name] = requireSafeInteger(overrideValue, valuePath);
    }
  }
  return { tier: requireTierNumber(record.tier, `${path}.tier`), overrides };
}

function decodeOption(value: unknown, path: string, tier: number): MemoryCpuTierOption {
  const record = requireRecord(value, path);
  const name = requireNonEmptyString(record.name, `${path}.name`);
  const unit = record.unit === null ? null : requireOneOf(record.unit, `${path}.unit`, memoryCpuTierUnits);
  const range = decodeRange(record.range, `${path}.range`);
  const optionValue = decodeRangeValue(record.value, `${path}.value`, range, true);
  const tierItems = requireArray(record.tiers, `${path}.tiers`);
  if (tierItems.length !== memoryCpuTierCount) {
    throw new ResponseDecodeError(`${path}.tiers`, `exactly ${memoryCpuTierCount} tiers`);
  }
  const tiers = Object.freeze(tierItems.map((item, index): MemoryCpuTierStep => {
    const step = requireRecord(item, `${path}.tiers[${index}]`);
    return Object.freeze({
      value: decodeRangeValue(step.value, `${path}.tiers[${index}].value`, range, true),
      category: requireOneOf(step.category, `${path}.tiers[${index}].category`, memoryCpuTierCategories)
    });
  }));
  const category = requireOneOf(record.category, `${path}.category`, memoryCpuTierCategories);
  const current = tiers[tier - 1]!;
  if (category !== current.category) {
    throw new ResponseDecodeError(`${path}.category`, `the current tier's category '${current.category}'`);
  }
  const source = requireOneOf(record.source, `${path}.source`, memoryCpuTierSources);
  if (source === "tier" && optionValue !== current.value) {
    throw new ResponseDecodeError(`${path}.value`, "the current tier's value when source is 'tier'");
  }
  const apply = requireOneOf(record.apply, `${path}.apply`, memoryCpuTierApplyModes);
  const base = { name, value: optionValue, unit, range, tiers, category, source };
  if (apply === "immediate") {
    for (const key of ["runningValue", "pendingRestart"]) {
      if (key in record) throw new ResponseDecodeError(`${path}.${key}`, "absent when apply is 'immediate'");
    }
    return Object.freeze({ ...base, apply });
  }
  for (const key of ["runningValue", "pendingRestart"]) {
    if (!(key in record)) throw new ResponseDecodeError(`${path}.${key}`, "present when apply is 'restart'");
  }
  return Object.freeze({
    ...base,
    apply,
    runningValue: decodeRangeValue(record.runningValue, `${path}.runningValue`, range, false),
    pendingRestart: requireBoolean(record.pendingRestart, `${path}.pendingRestart`)
  });
}

function decodeRange(value: unknown, path: string): MemoryCpuTierRange {
  const record = requireRecord(value, path);
  const kind = requireOneOf(record.kind, `${path}.kind`, memoryCpuTierRangeKinds);
  if (kind === "integer") {
    if ("choices" in record) throw new ResponseDecodeError(`${path}.choices`, "absent on an integer range");
    const min = requireSafeInteger(record.min, `${path}.min`);
    const max = requireSafeInteger(record.max, `${path}.max`);
    if (min > max) throw new ResponseDecodeError(`${path}.max`, "max ≥ min");
    return Object.freeze({ kind, min, max, unset: requireBoolean(record.unset, `${path}.unset`) });
  }
  for (const key of ["min", "max", "unset"]) {
    if (key in record) throw new ResponseDecodeError(`${path}.${key}`, "absent on a choice range");
  }
  const choiceItems = requireArray(record.choices, `${path}.choices`);
  if (choiceItems.length === 0) throw new ResponseDecodeError(`${path}.choices`, "at least one choice");
  const choices = choiceItems.map((item, index) => requireNonEmptyString(item, `${path}.choices[${index}]`));
  if (new Set(choices).size !== choices.length) throw new ResponseDecodeError(`${path}.choices`, "unique choices");
  return Object.freeze({ kind, choices: Object.freeze(choices) });
}

/** bounded 为 true 时值必须落在可编辑范围里；运行时实际值只要求形状一致。 */
function decodeRangeValue(value: unknown, path: string, range: MemoryCpuTierRange, bounded: boolean): MemoryCpuTierValue {
  if (value === undefined) throw new ResponseDecodeError(path, "a value");
  if (range.kind === "choice") {
    const choice = requireString(value, path);
    if (!range.choices.includes(choice)) {
      throw new ResponseDecodeError(path, range.choices.map(item => `'${item}'`).join(" or "));
    }
    return choice;
  }
  if (value === null) {
    if (!range.unset) throw new ResponseDecodeError(path, "an integer (null only when range.unset is true)");
    return null;
  }
  const integer = requireSafeInteger(value, path);
  if (bounded && (integer < range.min || integer > range.max)) {
    throw new ResponseDecodeError(path, `an integer in ${range.min}..${range.max}`);
  }
  return integer;
}

function requireTierNumber(value: unknown, path: string): number {
  const tier = requireSafeInteger(value, path);
  if (tier < 1 || tier > memoryCpuTierCount) throw new ResponseDecodeError(path, `a tier in 1..${memoryCpuTierCount}`);
  return tier;
}
