import { For, Show, createMemo, createSignal } from "solid-js";
import type { UpdatesCopy } from "../../../i18n/settingsTypes.ts";

export interface VersionChoice {
  choice: string;
  version?: string | null;
  series?: string | null;
  channel?: string | null;
  publishedAt?: string | null;
  available?: boolean;
  selectable?: boolean;
  unavailableReason?: string | null;
}

export function VersionSelector(props: {
  options: readonly VersionChoice[];
  choice: string | null;
  onSelect: (choice: string) => void;
  text: UpdatesCopy;
  installedVersionUnknown?: boolean;
}) {
  const [expanded, setExpanded] = createSignal<ReadonlySet<string>>(new Set());
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
    : option.unavailableReason ? text().unavailableReasons[option.unavailableReason] ?? option.unavailableReason : null;

  function toggle(series: string) {
    const next = new Set(expanded());
    if (next.has(series)) next.delete(series);
    else next.add(series);
    setExpanded(next);
  }

  function row(option: VersionChoice, label?: string) {
    return <label class="version-selector-item" aria-disabled={!enabled(option)}>
      <input type="radio" name="selectedVersion" value={option.choice}
        checked={props.choice === option.choice} disabled={!enabled(option)}
        onChange={() => props.onSelect(option.choice)} />
      <span class="version-selector-item-text">
        <strong>{label ?? option.version ?? option.choice}</strong>
        <Show when={label && option.version}><small>{option.version}</small></Show>
        <Show when={option.channel}><small>{option.channel === "stable"
          ? text().stable
          : text().preview}</small></Show>
        <Show when={!enabled(option) && reason(option)}><small>{reason(option)}</small></Show>
      </span>
    </label>;
  }

  return <div class="version-selector">
    <div class="version-selector-pinned">
      <Show when={latest()} fallback={<p>{text().noLatest}</p>}>
        {(option) => row(option(), text().latest)}
      </Show>
      <Show when={stable()} fallback={<p>{text().noStable}</p>}>
        {(option) => row(option(), text().latestStable)}
      </Show>
      <Show when={verified()}>{(option) => row(option(), text().verified)}</Show>
    </div>
    <Show when={groups().length > 0}>
      <div class="version-selector-groups">
        <For each={groups()}>{([series, versions]) => <section class="version-selector-group">
          <button type="button" class="version-selector-expand" aria-expanded={expanded().has(series)}
            onClick={() => toggle(series)}>
            <span>{series}</span><span>{expanded().has(series) ? "▾" : "▸"}</span>
          </button>
          {row(versions[0])}
          <Show when={expanded().has(series)}>
            <For each={versions.slice(1)}>{(option) => row(option)}</For>
          </Show>
        </section>}</For>
      </div>
    </Show>
  </div>;
}
