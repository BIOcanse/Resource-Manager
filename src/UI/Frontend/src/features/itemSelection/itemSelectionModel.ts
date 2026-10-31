/*
 * 录制项选择器的数据和规则（docs/targeted_recording_views_2026-10-30.md §3.1、§3.9）。
 * 录制配置和浮层设置共用：两边的项形状、校验都和后端录制项相同，只是上限、允许的种类和用途不同（配置 256，浮层 64）。
 * 目录里每项写明用途 uses（"recording"、"overlay"），选择器只列出含有当前用途的项。
 *
 * - 一项是 {kind, id, source, intervalMs, syncPrecision}。
 * - source："auto"（自动选择，报告里写实际用到的来源）或目录里这一项的某个具体来源。
 * - intervalMs：null = 有自然间隔用自然间隔，否则用配置的默认间隔；其余 100–60000 ms 的整数。应用提交帧逐帧记录，永远是 null。
 * - syncPrecision：low、mid、high，新加的项默认 mid（界面上写明，可改）。
 * - 同一种类下 id 不分大小写只能出现一次（与后端的重复判定一致）。
 */

export const itemKinds = ["frames", "software", "metric"] as const;
export type ItemKind = typeof itemKinds[number];
export const syncPrecisions = ["low", "mid", "high"] as const;
export type SyncPrecision = typeof syncPrecisions[number];

export const itemUses = ["recording", "overlay"] as const;
export type ItemUse = typeof itemUses[number];

export const automaticSource = "auto";
export const defaultSyncPrecision: SyncPrecision = "mid";
export const minimumIntervalMs = 100;
export const maximumIntervalMs = 60_000;

export interface SelectionItem {
  kind: ItemKind;
  id: string;
  source: string;
  intervalMs: number | null;
  syncPrecision: SyncPrecision;
}

/** 目录里的一项：可录的东西、它的单位、可以指定的具体来源、来源发布的自然间隔（没有为 null），以及能用在哪里。 */
export interface CatalogEntry {
  kind: ItemKind;
  id: string;
  unit: string;
  sources: string[];
  naturalIntervalMs: number | null;
  uses: ItemUse[];
  /** 这台机器上不适用或没有来源的原因码（not-applicable、no-source）；可用时为 null。 */
  unavailableReason?: string | null;
}

/** 只留下能用在 use 里的目录项（浮层独有的帧率、1% Low、0.1% Low 不进录制配置）。 */
export function catalogForUse(catalog: readonly CatalogEntry[], use: ItemUse): CatalogEntry[] {
  return catalog.filter((entry) => entry.uses.includes(use));
}

export function itemKey(kind: ItemKind, id: string) {
  return `${kind}:${id.toLowerCase()}`;
}

/** 从目录新加一项：来源自动、间隔留空、同步精度中。这些默认值都显示在选择器里，可改。 */
export function newItem(entry: CatalogEntry): SelectionItem {
  return { kind: entry.kind, id: entry.id, source: automaticSource, intervalMs: null, syncPrecision: defaultSyncPrecision };
}

export type AddRefusal = "limit" | "duplicate" | "kind";

/** 加一项。到上限、已经有这一项、种类不允许时不加，说明原因。 */
export function addItem(
  items: readonly SelectionItem[],
  entry: CatalogEntry,
  maximumItems: number,
  allowedKinds: readonly ItemKind[]
): { items: SelectionItem[] } | { refused: AddRefusal } {
  if (!allowedKinds.includes(entry.kind)) return { refused: "kind" };
  if (items.length >= maximumItems) return { refused: "limit" };
  const key = itemKey(entry.kind, entry.id);
  if (items.some((item) => itemKey(item.kind, item.id) === key)) return { refused: "duplicate" };
  return { items: [...items, newItem(entry)] };
}

export function removeItem(items: readonly SelectionItem[], kind: ItemKind, id: string): SelectionItem[] {
  const key = itemKey(kind, id);
  return items.filter((item) => itemKey(item.kind, item.id) !== key);
}

export function updateItem(
  items: readonly SelectionItem[],
  kind: ItemKind,
  id: string,
  change: Partial<Pick<SelectionItem, "source" | "intervalMs" | "syncPrecision">>
): SelectionItem[] {
  const key = itemKey(kind, id);
  return items.map((item) => {
    if (itemKey(item.kind, item.id) !== key) return item;
    const next = { ...item, ...change };
    // 帧逐帧记录，没有间隔。
    if (next.kind === "frames") next.intervalMs = null;
    return next;
  });
}

export type IntervalParse = { ok: true; value: number | null } | { ok: false };

/** 间隔输入框的文字：空 = null（自然间隔或配置默认）；其余必须是 100–60000 的整数。 */
export function parseIntervalText(text: string): IntervalParse {
  const trimmed = text.trim();
  if (trimmed === "") return { ok: true, value: null };
  if (!/^\d+$/.test(trimmed)) return { ok: false };
  const value = Number(trimmed);
  return isIntervalInRange(value) ? { ok: true, value } : { ok: false };
}

export function isIntervalInRange(value: number) {
  return Number.isSafeInteger(value) && value >= minimumIntervalMs && value <= maximumIntervalMs;
}

/** 一项留空间隔时实际会用的间隔和它的出处，作为输入框的占位文字。帧没有间隔。 */
export function intervalPlaceholder(
  entry: CatalogEntry | undefined,
  defaultIntervalMs: number
): { origin: "natural" | "profile-default"; intervalMs: number } | null {
  if (entry?.kind === "frames") return null;
  return entry?.naturalIntervalMs != null
    ? { origin: "natural", intervalMs: entry.naturalIntervalMs }
    : { origin: "profile-default", intervalMs: defaultIntervalMs };
}

export interface CatalogGroup {
  kind: ItemKind;
  entries: Array<{ entry: CatalogEntry; name: string; selected: boolean }>;
}

/**
 * 按种类分组的可选目录：只列允许的种类，按名称或 id 搜索（不分大小写，空格分开的每个词都要匹配），
 * 组内按名称排序。已选的项照样列出并标记，加不加由 addItem 判定。
 */
export function catalogGroups(
  catalog: readonly CatalogEntry[],
  items: readonly SelectionItem[],
  allowedKinds: readonly ItemKind[],
  query: string,
  nameOf: (kind: ItemKind, id: string) => string
): CatalogGroup[] {
  const selected = new Set(items.map((item) => itemKey(item.kind, item.id)));
  const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  const groups: CatalogGroup[] = [];
  for (const kind of itemKinds) {
    if (!allowedKinds.includes(kind)) continue;
    const entries = [];
    for (const entry of catalog) {
      if (entry.kind !== kind) continue;
      const name = nameOf(entry.kind, entry.id);
      const haystack = `${name} ${entry.id}`.toLowerCase();
      if (!words.every((word) => haystack.includes(word))) continue;
      entries.push({ entry, name, selected: selected.has(itemKey(entry.kind, entry.id)) });
    }
    entries.sort((left, right) => left.name.localeCompare(right.name) || left.entry.id.localeCompare(right.entry.id));
    if (entries.length > 0) groups.push({ kind, entries });
  }
  return groups;
}

export function findCatalogEntry(catalog: readonly CatalogEntry[], kind: ItemKind, id: string) {
  const key = itemKey(kind, id);
  return catalog.find((entry) => itemKey(entry.kind, entry.id) === key);
}

/** 一项可选的来源：自动选择加目录里的每个具体来源；已存的来源不在今天的目录里时也列出，不悄悄换掉。 */
export function sourceOptions(entry: CatalogEntry | undefined, current: string): string[] {
  const sources = [automaticSource, ...(entry?.sources ?? [])];
  if (!sources.some((source) => source.toLowerCase() === current.toLowerCase())) sources.push(current);
  return sources;
}

export type ItemListIssue = "too-many" | "duplicate" | "kind" | "precision" | "interval" | "frames-interval" | "id" | "source";

/**
 * 一组项是否合规（与后端 TargetedRecordingItemReader 的形状校验一致，目录归属由后端判定）：
 * 不超过上限，(kind, id) 不重复，种类和同步精度在各自的集合里，间隔为 null 或 100–60000，帧没有间隔。
 * 合规返回 null，否则返回第一个问题。
 */
export function itemListIssue(items: readonly SelectionItem[], maximumItems: number): ItemListIssue | null {
  if (items.length > maximumItems) return "too-many";
  const seen = new Set<string>();
  for (const item of items) {
    if (!(itemKinds as readonly string[]).includes(item.kind)) return "kind";
    if (typeof item.id !== "string" || item.id.trim() === "") return "id";
    if (typeof item.source !== "string" || item.source.trim() === "") return "source";
    if (!(syncPrecisions as readonly string[]).includes(item.syncPrecision)) return "precision";
    if (item.kind === "frames" && item.intervalMs !== null) return "frames-interval";
    if (item.intervalMs !== null && !isIntervalInRange(item.intervalMs)) return "interval";
    const key = itemKey(item.kind, item.id);
    if (seen.has(key)) return "duplicate";
    seen.add(key);
  }
  return null;
}

export function sameItems(left: readonly SelectionItem[], right: readonly SelectionItem[]) {
  return left.length === right.length && left.every((item, index) => {
    const other = right[index];
    return item.kind === other.kind && item.id === other.id && item.source === other.source
      && item.intervalMs === other.intervalMs && item.syncPrecision === other.syncPrecision;
  });
}
