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

// Validate five tiers, unique option names and known units, categories, sources and apply modes.
// Integer ranges require safe ordered bounds; choice ranges require unique nonempty strings.
// Editable values must fit their ranges; null requires integer range.unset.
// The current category and tier-sourced value must match the selected tier.
// Only restart options carry runningValue and pendingRestart.
// Runtime values retain the value shape but may fall outside the editable range.




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

/** Saved tier selection and per-option integer, string or null overrides. */
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

/** Enforce editable bounds only when bounded; runtime values require the same shape. */
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
