import {
  defaultMemoryCpuTierSettings,
  memoryCpuTierCount,
  type MemoryCpuTierApply,
  type MemoryCpuTierCategory,
  type MemoryCpuTierOption,
  type MemoryCpuTierRange,
  type MemoryCpuTierReading,
  type MemoryCpuTierSettings,
  type MemoryCpuTierSource,
  type MemoryCpuTierUnit,
  type MemoryCpuTierValue
} from "../../data/memoryCpuTier/memoryCpuTierTypes.ts";

/** Normalize valid tier integers and supported override values; sort override keys so draft equality is independent of insertion order. */
export function normalizeMemoryCpuTierSettings(value?: MemoryCpuTierSettings | null): MemoryCpuTierSettings {
  const defaults = defaultMemoryCpuTierSettings();
  const tier = Number.isSafeInteger(value?.tier) && value!.tier >= 1 && value!.tier <= memoryCpuTierCount
    ? value!.tier : defaults.tier;
  const overrides: Record<string, MemoryCpuTierValue> = {};
  const source = value?.overrides && typeof value.overrides === "object" ? value.overrides : {};
  for (const name of Object.keys(source).sort()) {
    const item = source[name];
    if (item === null || typeof item === "string" || Number.isSafeInteger(item)) overrides[name] = item as MemoryCpuTierValue;
  }
  return { tier, overrides };
}

export function withMemoryCpuTier(settings: MemoryCpuTierSettings, tier: number): MemoryCpuTierSettings {
  // Changing tiers preserves individual overrides.
  return normalizeMemoryCpuTierSettings({ tier, overrides: { ...settings.overrides } });
}

export function withMemoryCpuTierOverride(
  settings: MemoryCpuTierSettings, name: string, value: MemoryCpuTierValue
): MemoryCpuTierSettings {
  return normalizeMemoryCpuTierSettings({ tier: settings.tier, overrides: { ...settings.overrides, [name]: value } });
}

export function withoutMemoryCpuTierOverride(settings: MemoryCpuTierSettings, name: string): MemoryCpuTierSettings {
  const overrides = { ...settings.overrides };
  delete overrides[name];
  return normalizeMemoryCpuTierSettings({ tier: settings.tier, overrides });
}

/** Reject values outside the editable range before they enter the draft. */
export function isMemoryCpuTierValueInRange(range: MemoryCpuTierRange, value: MemoryCpuTierValue): boolean {
  if (range.kind === "choice") return typeof value === "string" && range.choices.includes(value);
  if (value === null) return range.unset;
  return typeof value === "number" && Number.isSafeInteger(value) && value >= range.min && value <= range.max;
}

export interface MemoryCpuTierRow {
  readonly name: string;
  readonly value: MemoryCpuTierValue;
  readonly unit: MemoryCpuTierUnit | null;
  readonly range: MemoryCpuTierRange;
  readonly category: MemoryCpuTierCategory;
  readonly source: MemoryCpuTierSource;
  readonly apply: MemoryCpuTierApply;
  /** Effective runtime value for restart-only options. */
  readonly runningValue: MemoryCpuTierValue | undefined;
  /** Show both current and post-restart values. */
  readonly showRunningValue: boolean;
  /** Whether the draft has an override that can be cleared. */
  readonly overridden: boolean;
}

export interface MemoryCpuTierView {
  /** Tier represented by the list. */
  readonly tier: number;
  /** True marks a draft preview; false preserves the backend list unchanged. */
  readonly preview: boolean;
  readonly rows: readonly MemoryCpuTierRow[];
}

/** Use backend values unchanged when the draft matches saved settings. Otherwise preview overrides over tier values and reread effective values after save. */
export function buildMemoryCpuTierView(
  reading: MemoryCpuTierReading, draft: MemoryCpuTierSettings, committed: MemoryCpuTierSettings
): MemoryCpuTierView {
  const preview = JSON.stringify(normalizeMemoryCpuTierSettings(draft)) !== JSON.stringify(normalizeMemoryCpuTierSettings(committed));
  const tier = preview ? draft.tier : reading.tier;
  return {
    tier,
    preview,
    rows: reading.options.map(option => preview ? previewRow(option, draft) : readingRow(option, draft))
  };
}

function readingRow(option: MemoryCpuTierOption, draft: MemoryCpuTierSettings): MemoryCpuTierRow {
  return {
    name: option.name, value: option.value, unit: option.unit, range: option.range,
    category: option.category, source: option.source, apply: option.apply,
    runningValue: option.apply === "restart" ? option.runningValue : undefined,
    showRunningValue: option.apply === "restart" && option.pendingRestart,
    overridden: Object.hasOwn(draft.overrides, option.name)
  };
}

function previewRow(option: MemoryCpuTierOption, draft: MemoryCpuTierSettings): MemoryCpuTierRow {
  const step = option.tiers[draft.tier - 1]!;
  const overridden = Object.hasOwn(draft.overrides, option.name);
  const value = overridden ? draft.overrides[option.name]! : step.value;
  const runningValue = option.apply === "restart" ? option.runningValue : undefined;
  return {
    name: option.name, value, unit: option.unit, range: option.range,
    category: step.category, source: overridden ? "user" : "tier", apply: option.apply,
    runningValue,
    // Mark restart-only preview values that differ from runtime values.
    showRunningValue: option.apply === "restart" && value !== runningValue,
    overridden
  };
}

/** Start integer editing at the current value, or the lower bound when unset. */
export function memoryCpuTierEditableInteger(row: MemoryCpuTierRow): number {
  if (row.range.kind !== "integer") return 0;
  return typeof row.value === "number" ? row.value : row.range.min;
}
