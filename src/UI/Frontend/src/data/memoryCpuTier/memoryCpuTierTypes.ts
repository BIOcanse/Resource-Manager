// Wire types for the five memory/CPU tiers.
// The backend supplies option names, units, ranges and tier values.

/** Tier 1 minimizes memory at higher CPU cost; tier 5 minimizes CPU at higher memory cost. */
export const memoryCpuTierCount = 5;
/** Tier used when restoring defaults. */
export const memoryCpuTierDefault = 3;

export const memoryCpuTierUnits = ["bytes", "milliseconds", "seconds", "count", "level"] as const;
export const memoryCpuTierCategories = ["proportional", "aggressive"] as const;
export const memoryCpuTierSources = ["tier", "user"] as const;
export const memoryCpuTierApplyModes = ["immediate", "restart"] as const;
export const memoryCpuTierRangeKinds = ["integer", "choice"] as const;

export type MemoryCpuTierUnit = typeof memoryCpuTierUnits[number];
export type MemoryCpuTierCategory = typeof memoryCpuTierCategories[number];
export type MemoryCpuTierSource = typeof memoryCpuTierSources[number];
export type MemoryCpuTierApply = typeof memoryCpuTierApplyModes[number];

/** Integer, declared choice string, or null when range.unset permits it. */
export type MemoryCpuTierValue = number | string | null;

export type MemoryCpuTierRange =
  | { readonly kind: "integer"; readonly min: number; readonly max: number; readonly unset: boolean }
  | { readonly kind: "choice"; readonly choices: readonly string[] };

export interface MemoryCpuTierStep {
  readonly value: MemoryCpuTierValue;
  readonly category: MemoryCpuTierCategory;
}

interface MemoryCpuTierOptionBase {
  readonly name: string;
  /** Effective value: override ?? selected-tier value. */
  readonly value: MemoryCpuTierValue;
  /** Formatting unit only; the frontend performs no conversion. */
  readonly unit: MemoryCpuTierUnit | null;
  readonly range: MemoryCpuTierRange;
  /** Exactly five entries; index zero is tier 1. */
  readonly tiers: readonly MemoryCpuTierStep[];
  /** Category of the selected tier. */
  readonly category: MemoryCpuTierCategory;
  readonly source: MemoryCpuTierSource;
}

export type MemoryCpuTierOption =
  | (MemoryCpuTierOptionBase & { readonly apply: "immediate" })
  | (MemoryCpuTierOptionBase & {
    readonly apply: "restart";
    /** Actual runtime value read at startup. */
    readonly runningValue: MemoryCpuTierValue;
    /** True when a restart is required to apply the value. */
    readonly pendingRestart: boolean;
  });

/** Memory/CPU tier settings response. */
export interface MemoryCpuTierReading {
  readonly tier: number;
  readonly options: readonly MemoryCpuTierOption[];
}

/** memoryCpuTier section of the app settings patch. */
export interface MemoryCpuTierSettings {
  tier: number;
  overrides: Record<string, MemoryCpuTierValue>;
}

export function defaultMemoryCpuTierSettings(): MemoryCpuTierSettings {
  return { tier: memoryCpuTierDefault, overrides: {} };
}
