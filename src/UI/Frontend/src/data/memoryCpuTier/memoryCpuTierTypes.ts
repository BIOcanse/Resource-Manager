// 内存与 CPU 取舍五档的线格式（docs/memory_cpu_tiers_2026-10-30.md §5）。
// 选项名、单位、范围和五档的值都由后端在读回复里给出，前端不写死选项表。

/** 档位数：第 1 档内存最省、CPU 压力最高；第 5 档内存压力最高、CPU 最省。 */
export const memoryCpuTierCount = 5;
/** 默认档位（恢复默认值时用）。 */
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

/** 一个选项只有一个值：整数、choice 里的一个字符串，或 range.unset 时的“不设”(null)。 */
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
  /** 生效值：单独设置 ?? 当前档的值。 */
  readonly value: MemoryCpuTierValue;
  /** 只用于格式化，前端不换算。 */
  readonly unit: MemoryCpuTierUnit | null;
  readonly range: MemoryCpuTierRange;
  /** 长度恰好 5，下标 0 是第 1 档。 */
  readonly tiers: readonly MemoryCpuTierStep[];
  /** 当前档的类别。 */
  readonly category: MemoryCpuTierCategory;
  readonly source: MemoryCpuTierSource;
}

export type MemoryCpuTierOption =
  | (MemoryCpuTierOptionBase & { readonly apply: "immediate" })
  | (MemoryCpuTierOptionBase & {
    readonly apply: "restart";
    /** 启动时从运行时读出的实际值。 */
    readonly runningValue: MemoryCpuTierValue;
    /** 重启后才生效时为 true。 */
    readonly pendingRestart: boolean;
  });

/** GET /api/settings/memory-cpu-tier 的回复。 */
export interface MemoryCpuTierReading {
  readonly tier: number;
  readonly options: readonly MemoryCpuTierOption[];
}

/** 用户设置里的一节：PATCH /api/settings/app 的 memoryCpuTier 字段。 */
export interface MemoryCpuTierSettings {
  tier: number;
  overrides: Record<string, MemoryCpuTierValue>;
}

export function defaultMemoryCpuTierSettings(): MemoryCpuTierSettings {
  return { tier: memoryCpuTierDefault, overrides: {} };
}
