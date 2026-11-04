import { Show } from "solid-js";
import type { ReadingSourceMark as Mark } from "../presentation/readingSources.ts";
import { uiText } from "../text.ts";

/** Shared source badge with a full-text tooltip; readingSources decides which sources need one. The badge must fit inline without nesting another button inside selector options. */
export function ReadingSourceMark(props: { mark: Mark | null | undefined }) {
  const text = () => uiText.dashboard.readingSource;
  return (
    <Show when={props.mark}>
      {(mark) => (
        <span
          class="reading-source-mark"
          classList={{ pending: mark() === "pending" }}
          data-reading-source-mark={mark()}
          data-full-content={mark() === "pending" ? text().pendingHint : text().preferredHint}
        >
          {mark() === "pending" ? text().pending : text().preferred}
        </span>
      )}
    </Show>
  );
}
