import { For, Show, createMemo, createSignal, createUniqueId } from "solid-js";
import type { UpdatesCopy } from "../../../i18n/settingsTypes.ts";
import { Disclosure } from "../../../ui/primitives/Disclosure.tsx";

export interface VersionChoice {
  choice: string;
  version?: string | null;
  series?: string | null;
  channel?: string | null;
  publishedAt?: string | null;
  available?: boolean;
  selectable?: boolean;
  eligibility?: "eligible" | "installedVersionUnknown" | "notNewer";
  unavailableReason?: string | null;
}

/*
 * 版本列表：顶上是固定的几项（最新、最新稳定、已验证；没有的那一项显示空状态），下面按系列分组，每组默认只显示最新一版，
 * 系列标题是折叠标题（ui/primitives/Disclosure.tsx）。不能选的原因在列表顶上各说一次，不能选的行用 aria-describedby 指向它。
 */
export function VersionSelector(props: {
  options: readonly VersionChoice[];
  choice: string | null;
  onSelect: (choice: string) => void;
  text: UpdatesCopy;
  installedVersionUnknown?: boolean;
}) {
  const [expanded, setExpanded] = createSignal<ReadonlySet<string>>(new Set());
  const noteIdPrefix = createUniqueId();
  const text = () => props.text;
  const history = createMemo(() => props.options.filter((option) =>
    option.choice.startsWith("version:") || option.choice.startsWith("tag:")));
  const latest = createMemo(() => props.options.find((option) => option.choice === "latest") ?? history()[0]);
  const stable = createMemo(() => props.options.find((option) => option.choice === "latestStable")
    ?? history().find((option) => option.channel === "stable"));
  const verified = createMemo(() => props.options.find((option) => option.choice === "verified"));
  const groups = createMemo(() => {
    const result = new Map<string, VersionChoice[]>();
    for (const option of history()) {
      const series = option.series || (text().otherVersions);
      result.set(series, [...(result.get(series) ?? []), option]);
    }
    return [...result.entries()];
  });
  const enabled = (option: VersionChoice) =>
    option.available !== false && option.selectable !== false && !props.installedVersionUnknown;
  const reason = (option: VersionChoice) => props.installedVersionUnknown
    ? text().installedUnknown
    : option.eligibility && option.eligibility !== "eligible"
      ? text().unavailableReasons[option.eligibility]
    : option.unavailableReason ? text().unavailableReasons[option.unavailableReason] ?? option.unavailableReason : null;
  // 列表里出现的每种原因一条，按第一次出现的次序。
  const notes = createMemo(() => {
    const seen: string[] = [];
    for (const option of props.options) {
      const why = enabled(option) ? null : reason(option);
      if (why && !seen.includes(why)) seen.push(why);
    }
    return seen;
  });
  const noteId = (why: string) => `${noteIdPrefix}-${notes().indexOf(why)}`;

  function toggle(series: string) {
    const next = new Set(expanded());
    if (next.has(series)) next.delete(series);
    else next.add(series);
    setExpanded(next);
  }

  function row(option: VersionChoice, label?: string) {
    const why = () => (enabled(option) ? null : reason(option));
    return <label class="version-selector-item" aria-disabled={!enabled(option)}>
      <input type="radio" name="selectedVersion" value={option.choice}
        checked={props.choice === option.choice} disabled={!enabled(option)}
        aria-describedby={why() ? noteId(why()!) : undefined}
        onChange={() => props.onSelect(option.choice)} />
      <span class="version-selector-item-text">
        <strong>{label ?? option.version ?? option.choice}</strong>
        <Show when={label && option.version}><small>{option.version}</small></Show>
        <Show when={option.channel}><small>{option.channel === "stable"
          ? text().stable
          : text().preview}</small></Show>
      </span>
    </label>;
  }

  // 固定项没有对应版本时，在它的位置上显示空状态。
  function emptySlot(label: string, message: string) {
    return <div class="version-selector-item version-selector-empty">
      <span class="version-selector-radio-space" aria-hidden="true" />
      <span class="version-selector-item-text">
        <strong>{label}</strong>
        <small>{message}</small>
      </span>
    </div>;
  }

  return <div class="version-selector">
    <Show when={notes().length > 0}>
      <ul class="version-selector-notes">
        <For each={notes()}>{(why) => <li id={noteId(why)}>{why}</li>}</For>
      </ul>
    </Show>
    <div class="version-selector-pinned">
      <Show when={latest()} fallback={emptySlot(text().latest, text().noLatest)}>
        {(option) => row(option(), text().latest)}
      </Show>
      <Show when={stable()} fallback={emptySlot(text().latestStable, text().noStable)}>
        {(option) => row(option(), text().latestStable)}
      </Show>
      <Show when={verified()}>{(option) => row(option(), text().verified)}</Show>
    </div>
    <Show when={groups().length > 0}>
      <div class="version-selector-groups">
        <For each={groups()}>{([series, versions]) => <Show when={versions.length > 1} fallback={
          <section class="version-selector-group">
            <span class="version-selector-series">{series}</span>
            {row(versions[0])}
          </section>
        }>
          <Disclosure name={`版本系列「${series}」`} class="version-selector-group" headerClass="version-selector-expand"
            expanded={expanded().has(series)} onToggle={() => toggle(series)}
            header={series} peek={row(versions[0])}>
            <div class="version-selector-rest">
              <For each={versions.slice(1)}>{(option) => row(option)}</For>
            </div>
          </Disclosure>
        </Show>}</For>
      </div>
    </Show>
  </div>;
}
