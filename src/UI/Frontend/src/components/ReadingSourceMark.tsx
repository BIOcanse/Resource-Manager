import { Show } from "solid-js";
import type { ReadingSourceMark as Mark } from "../presentation/readingSources.ts";
import { uiText } from "../text.ts";

/**
 * 读数来源的小标记：「首选」或「待确认」，悬停看说明。没有标记时什么都不画。
 * 选择器选项、监视卡片和设备详情共用；哪些来源带标记由 presentation/readingSources.ts 决定。
 */
export function ReadingSourceMark(props: { mark: Mark | null | undefined }) {
  const text = () => uiText.dashboard.readingSource;
  return (
    <Show when={props.mark}>
      {(mark) => (
        <span
          class="reading-source-mark"
          classList={{ pending: mark() === "pending" }}
          data-reading-source-mark={mark()}
          title={mark() === "pending" ? text().pendingHint : text().preferredHint}
        >
          {mark() === "pending" ? text().pending : text().preferred}
        </span>
      )}
    </Show>
  );
}
