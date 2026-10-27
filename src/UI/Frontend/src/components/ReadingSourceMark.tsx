import { Show } from "solid-js";
import type { ReadingSourceMark as Mark } from "../presentation/readingSources.ts";
import { uiText } from "../text.ts";

/**
 * 读数来源的小标记：「首选」或「待确认」，悬停看说明（共用的完整内容提示，ui/primitives/TooltipLayer.tsx）。没有标记时什么都不画。
 * 选择器选项、监视卡片和设备详情共用；哪些来源带标记由 presentation/readingSources.ts 决定。
 * 不另放问号：选择器里的标记在选项按钮里面（按钮里不能再放按钮），监视卡片的标签只有一行、放不下，
 * 这个标记本身就是一个只有两个字的说明标签，悬停它看全文。
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
          data-full-content={mark() === "pending" ? text().pendingHint : text().preferredHint}
        >
          {mark() === "pending" ? text().pending : text().preferred}
        </span>
      )}
    </Show>
  );
}
