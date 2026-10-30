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

/**
 * 设置草稿里的 memoryCpuTier：档位取 1..5 的整数，否则用默认档；单独设置只保留整数、字符串和 null，
 * 键按名字排序，这样“同一组设置”只有一种 JSON 写法，比较草稿和已保存设置时不受插入顺序影响。
 */
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
  // 切档不清掉单独设置（§2）。
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

/** 值是否落在选项的可编辑范围里；范围外的值不进草稿，也就不会发出去。 */
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
  /** 只在重启后生效的选项上有：运行时实际在用的值。 */
  readonly runningValue: MemoryCpuTierValue | undefined;
  /** 同时显示“现在在用”和“重启后”两个值。 */
  readonly showRunningValue: boolean;
  /** 草稿里有这一项的单独设置（显示“清除”）。 */
  readonly overridden: boolean;
}

export interface MemoryCpuTierView {
  /** 列表对应的档位。 */
  readonly tier: number;
  /** true：草稿和已保存设置不同，列表是按 tiers[] 和草稿单独设置算出的预览；false：列表是后端回复原样。 */
  readonly preview: boolean;
  readonly rows: readonly MemoryCpuTierRow[];
}

/**
 * 草稿和已保存设置相同时，列表就是后端回复本身（生效值、类别、来源都按后端给的显示）。
 * 草稿不同（切了档或改了单独设置还没保存）时，每项的预览值 = 草稿单独设置 ?? 草稿档位在 tiers[] 里的值，
 * 类别取草稿档位的类别。保存后设置分区重读后端，草稿和已保存重新一致，列表又回到后端的值。
 */
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
    // 预览里重启项的新值和运行时实际值不同，就说明保存后要重启才生效。
    showRunningValue: option.apply === "restart" && value !== runningValue,
    overridden
  };
}

/** 编辑整数时的起点：当前值；“不设”时取范围的下限。 */
export function memoryCpuTierEditableInteger(row: MemoryCpuTierRow): number {
  if (row.range.kind !== "integer") return 0;
  return typeof row.value === "number" ? row.value : row.range.min;
}
