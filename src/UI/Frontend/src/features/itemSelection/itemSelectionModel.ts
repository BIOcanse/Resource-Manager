/* Shared recording/overlay rules use backend item shapes, purpose filters, and case-insensitive identity uniqueness. Null intervals use natural or configured timing; frame items always use null. */

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

/** Catalog item with units, concrete sources, natural interval, and supported uses. */
export interface CatalogEntry {
  kind: ItemKind;
  id: string;
  unit: string;
  sources: string[];
  naturalIntervalMs: number | null;
  uses: ItemUse[];
  /** Unavailable reason code, or null when available. */
  unavailableReason?: string | null;
}

/** Filter by catalog use so overlay-only metrics stay out of recording configurations. */
export function catalogForUse(catalog: readonly CatalogEntry[], use: ItemUse): CatalogEntry[] {
  return catalog.filter((entry) => entry.uses.includes(use));
}

export function itemKey(kind: ItemKind, id: string) {
  return `${kind}:${id.toLowerCase()}`;
}

/** Initialize with automatic source, inherited interval, and medium precision; all remain editable. */
export function newItem(entry: CatalogEntry): SelectionItem {
  return { kind: entry.kind, id: entry.id, source: automaticSource, intervalMs: null, syncPrecision: defaultSyncPrecision };
}

export type AddRefusal = "limit" | "duplicate" | "kind";

/** Add only within limits and allowed kinds, rejecting duplicates with a reason. */
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
    // Frame items record every frame and have no interval.
    if (next.kind === "frames") next.intervalMs = null;
    return next;
  });
}

export type IntervalParse = { ok: true; value: number | null } | { ok: false };

/** Empty interval text means null; other values must be integer milliseconds in the allowed range. */
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

/** Resolve an inherited interval and its source for placeholder text; frame items have no interval. */
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

/** Filter allowed kinds by every case-insensitive search term, group and sort by name, and retain selected items. */
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

/** Offer automatic and catalog sources while retaining a saved source absent from the catalog. */
export function sourceOptions(entry: CatalogEntry | undefined, current: string): string[] {
  const sources = [automaticSource, ...(entry?.sources ?? [])];
  if (!sources.some((source) => source.toLowerCase() === current.toLowerCase())) sources.push(current);
  return sources;
}

export type ItemListIssue = "too-many" | "duplicate" | "kind" | "precision" | "interval" | "frames-interval" | "id" | "source";

/** Validate shape, limits, unique identities, kind, precision, and interval; return the first issue or null. Catalog membership remains backend-owned. */
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
