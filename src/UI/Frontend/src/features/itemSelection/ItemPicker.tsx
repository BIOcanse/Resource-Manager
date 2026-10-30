import { createEffect, createMemo, createSignal, For, Index, Show } from "solid-js";
import { createStore, produce } from "solid-js/store";
import { StandardSelect } from "../../components/StandardSelect.tsx";
import { uiText } from "../../text.ts";
import { itemName, sourceName } from "./itemNames.ts";
import {
  addItem, catalogGroups, findCatalogEntry, intervalPlaceholder, itemKey, parseIntervalText, removeItem,
  sourceOptions, syncPrecisions, updateItem,
  type CatalogEntry, type ItemKind, type SelectionItem, type SyncPrecision
} from "./itemSelectionModel.ts";

/*
 * 录制项选择器（docs/targeted_recording_views_2026-10-30.md §3.1、§3.9）。录制配置用它，浮层设置之后也用它。
 *
 * - 输入：目录、当前的项、最多几项（配置 256，浮层 32）、允许的种类、留空间隔时的默认间隔（作为占位文字显示）。
 * - 输出：onChange 给出新的项列表；间隔输入框里有不合规的文字时 onIssuesChange(true)，调用方据此不让保存。
 * - 左边按种类分组列出目录，可搜索，逐项添加；右边是已选的项，逐项改来源、间隔、同步精度，或移除。
 * - 新加的项的默认值（来源自动、间隔留空、同步精度中）写在右边的说明里，每一项都能改。
 * - 到上限时添加按钮不可用，并说明原因。
 * 正在编辑的间隔文字是本组件的状态；调用方换了一组项（放弃修改、换了配置）时重新挂载本组件。
 */
export function ItemPicker(props: {
  catalog: readonly CatalogEntry[];
  items: readonly SelectionItem[];
  maximumItems: number;
  allowedKinds: readonly ItemKind[];
  defaultIntervalMs: number;
  onChange: (items: SelectionItem[]) => void;
  onIssuesChange?: (hasIssues: boolean) => void;
}) {
  const copy = () => uiText.targetedReport.itemPicker;
  const [query, setQuery] = createSignal("");
  // 间隔输入框里还不合规的文字，按项的键存；合规的输入直接写进项，不留在这里。
  const [intervalDrafts, setIntervalDrafts] = createStore<Record<string, string>>({});
  const groups = createMemo(() => catalogGroups(props.catalog, props.items, props.allowedKinds, query(), itemName));
  const full = () => props.items.length >= props.maximumItems;
  const hasIssues = createMemo(() => Object.keys(intervalDrafts).length > 0);
  createEffect(() => props.onIssuesChange?.(hasIssues()));

  const add = (entry: CatalogEntry) => {
    const result = addItem(props.items, entry, props.maximumItems, props.allowedKinds);
    if ("items" in result) props.onChange(result.items);
  };
  const remove = (item: SelectionItem) => {
    const key = itemKey(item.kind, item.id);
    setIntervalDrafts(produce((drafts) => { delete drafts[key]; }));
    props.onChange(removeItem(props.items, item.kind, item.id));
  };
  const editInterval = (item: SelectionItem, text: string) => {
    const key = itemKey(item.kind, item.id);
    const parsed = parseIntervalText(text);
    if (!parsed.ok) {
      setIntervalDrafts(key, text);
      return;
    }
    setIntervalDrafts(produce((drafts) => { delete drafts[key]; }));
    if (parsed.value !== item.intervalMs) props.onChange(updateItem(props.items, item.kind, item.id, { intervalMs: parsed.value }));
  };
  const placeholder = (item: SelectionItem) => {
    const value = intervalPlaceholder(findCatalogEntry(props.catalog, item.kind, item.id), props.defaultIntervalMs);
    if (value === null) return "";
    return value.origin === "natural" ? copy().intervalNatural(value.intervalMs) : copy().intervalDefault(value.intervalMs);
  };

  return (
    <div class="item-picker">
      <section class="item-picker-catalog" aria-label={copy().available}>
        <h4>{copy().available}</h4>
        <input class="item-picker-search" type="search" value={query()} placeholder={copy().search}
          aria-label={copy().search} onInput={(event) => setQuery(event.currentTarget.value)} />
        <Show when={full()}>
          <p class="item-picker-note warning" role="status">{copy().limitReached}</p>
        </Show>
        <div class="item-picker-groups">
          <Show when={groups().length > 0} fallback={<p class="item-picker-empty">{copy().noMatch}</p>}>
            <For each={groups()}>
              {(group) => (
                <div class="item-picker-group" role="group" aria-label={copy().groups[group.kind]}>
                  <h5>{copy().groups[group.kind]}</h5>
                  <ul>
                    <For each={group.entries}>
                      {(row) => (
                        <li class="item-picker-entry">
                          <span class="item-picker-entry-name">
                            <strong>{row.name}</strong>
                            <span>{row.entry.id}{row.entry.unit ? ` · ${row.entry.unit}` : ""}</span>
                          </span>
                          <Show when={!row.selected} fallback={<span class="item-picker-added">{copy().added}</span>}>
                            <button class="secondary" type="button" disabled={full()}
                              aria-label={copy().addItem(row.name)} onClick={() => add(row.entry)}>{copy().add}</button>
                          </Show>
                        </li>
                      )}
                    </For>
                  </ul>
                </div>
              )}
            </For>
          </Show>
        </div>
      </section>
      <section class="item-picker-selected" aria-label={copy().selected}>
        <div class="item-picker-selected-header">
          <h4>{copy().selected}</h4>
          <span class="item-picker-count" role="status">{copy().count(props.items.length, props.maximumItems)}</span>
        </div>
        <p class="item-picker-hint">{copy().defaultsHint}</p>
        <p class="item-picker-hint">{copy().precisionHint}</p>
        <Show when={props.items.length > 0} fallback={<p class="item-picker-empty">{copy().none}</p>}>
          <ul class="item-picker-rows">
            {/* 按位置列出：改一项时这一行不重建，正在输入的间隔框不丢焦点。 */}
            <Index each={props.items}>
              {(item) => {
                const key = () => itemKey(item().kind, item().id);
                const entry = () => findCatalogEntry(props.catalog, item().kind, item().id);
                const name = () => itemName(item().kind, item().id);
                const draft = () => intervalDrafts[key()];
                const change = (value: Parameters<typeof updateItem>[3]) =>
                  props.onChange(updateItem(props.items, item().kind, item().id, value));
                return (
                  <li class="item-picker-row">
                    <div class="item-picker-row-title">
                      <span class="item-picker-entry-name">
                        <strong>{name()}</strong>
                        <span>{copy().groups[item().kind]} · {item().id}{entry()?.unit ? ` · ${entry()!.unit}` : ""}</span>
                      </span>
                      <button class="secondary" type="button" aria-label={copy().removeItem(name())}
                        onClick={() => remove(item())}>{copy().remove}</button>
                    </div>
                    <Show when={props.catalog.length > 0 && !entry()}>
                      <p class="item-picker-note warning">{uiText.targetedReport.errors.itemUnknown}</p>
                    </Show>
                    <div class="item-picker-row-fields">
                      <div class="item-picker-field">
                        <span>{copy().source}</span>
                        <StandardSelect value={item().source} ariaLabel={`${copy().source} · ${name()}`}
                          options={sourceOptions(entry(), item().source).map((source) => ({ value: source, label: sourceName(source) }))}
                          onChange={(source) => change({ source })} />
                      </div>
                      <label class="item-picker-field">
                        <span>{copy().interval}</span>
                        <Show when={item().kind !== "frames"} fallback={<span class="item-picker-fixed">{copy().perPresent}</span>}>
                          <input type="text" inputMode="numeric" value={draft() ?? (item().intervalMs?.toString() ?? "")}
                            placeholder={placeholder(item())} aria-invalid={draft() !== undefined}
                            onInput={(event) => editInterval(item(), event.currentTarget.value)} />
                        </Show>
                      </label>
                      <div class="item-picker-field">
                        <span>{copy().syncPrecision}</span>
                        <StandardSelect<SyncPrecision> value={item().syncPrecision}
                          ariaLabel={`${copy().syncPrecision} · ${name()}`}
                          options={syncPrecisions.map((value) => ({ value, label: copy().precisions[value] }))}
                          onChange={(syncPrecision) => change({ syncPrecision })} />
                      </div>
                    </div>
                    <Show when={draft() !== undefined}>
                      <p class="item-picker-note error" role="alert">{copy().intervalInvalid}</p>
                    </Show>
                  </li>
                );
              }}
            </Index>
          </ul>
        </Show>
      </section>
    </div>
  );
}
