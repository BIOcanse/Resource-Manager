import { For } from "solid-js";

/** Stack candidate labels to reserve the widest localized width. Only the current label contributes visible text and the accessible name. */
export function StableLabel<K extends string>(props: {
  readonly labels: Readonly<Record<K, string>>;
  readonly current: K;
}) {
  return (
    <span class="stable-label">
      <For each={Object.keys(props.labels) as K[]}>
        {(key) => (
          <span classList={{ "stable-label-inactive": key !== props.current }}>
            {props.labels[key]}
          </span>
        )}
      </For>
    </span>
  );
}
