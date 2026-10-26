import { createUniqueId, onCleanup, onMount } from "solid-js";
import type { JSX } from "solid-js";
import { switchToggleDefinition } from "../../interactions/components/switchToggle.ts";
import type { Presentation } from "../../interactions/protocol/runtime.ts";
import { solidHost, useInteraction } from "../../interactions/solid/lodestone.ts";

/**
 * 折叠标题：原生按钮加可以收起的内容。
 * - 展开与否由调用方持有（expanded / onToggle）；点击、Enter、空格由按钮自己处理。
 * - 开度由磁石带（协议 4.6：演示的折叠面板和开关是同一份定义，这里就用设置开关的定义）：宿主值变了，回点弹簧把开度带到 0 或 1。
 *   开度映射到内容的高度（形态，下面的内容让位）和箭头的转角（--disclosure-openness）。没有几何，不能拖。
 * - 无动画、极简档由运行时直接到终态，这就是减弱版（docs/ui_interaction_kit.md 第 3 节）。
 * - 收起后内容 inert：不能 Tab 进去，辅助技术也读不到。
 * - peek 是收起时也显示的内容，放在标题和可收起的内容之间（例如版本系列的最新一版）。
 */
export function Disclosure(props: {
  /** 磁石实例名，日志里分得出是哪一个。 */
  name: string;
  expanded: boolean;
  onToggle: () => void;
  header: JSX.Element;
  class?: string;
  headerClass?: string;
  peek?: JSX.Element;
  children: JSX.Element;
}) {
  const bodyId = createUniqueId();
  let header!: HTMLButtonElement;
  let body!: HTMLDivElement;
  let content!: HTMLDivElement;
  // 内容高度由 ResizeObserver 缓存，画的时候不读布局。
  let contentHeight = 0;
  let openness = props.expanded ? 1 : 0;
  const draw = () => {
    // 完全展开时不限高、不裁剪：内容自己变高（换行、窗口变窄）不需要重新量，内容的焦点环也不被裁掉。
    const open = openness >= 1;
    body.style.height = open ? "" : `${(openness * contentHeight).toFixed(2)}px`;
    body.style.overflow = open ? "" : "hidden";
    header.style.setProperty("--disclosure-openness", openness.toFixed(4));
  };
  onMount(() => {
    contentHeight = content.offsetHeight;
    const observer = new ResizeObserver(() => {
      contentHeight = content.offsetHeight;
      if (openness > 0 && openness < 1) draw();
    });
    observer.observe(content);
    onCleanup(() => observer.disconnect());
  });
  const view = (): Presentation => ({
    element: header,
    bindingLabel: "高度",
    geometry: () => null,
    render: ({ x }) => {
      openness = x;
      draw();
    },
    // aria-expanded 由 Solid 按宿主值写。
    aria: (value) => `aria-expanded=${value >= 0.5}`
  });
  useInteraction(props.name, switchToggleDefinition, view, solidHost<number>({
    value: () => (props.expanded ? 1 : 0),
    enabled: () => true,
    // 不能拖，松手不会停在别处；展开与否只由按钮的点击改。
    call: () => {}
  }));
  return (
    <div class={props.class ? `ui-disclosure ${props.class}` : "ui-disclosure"}>
      <button ref={header} type="button" class={props.headerClass ? `ui-disclosure-header ${props.headerClass}` : "ui-disclosure-header"}
        aria-expanded={props.expanded} aria-controls={bodyId} onClick={() => props.onToggle()}
        style={{ "--disclosure-openness": openness }}>
        <span class="ui-disclosure-chevron" aria-hidden="true" />
        <span class="ui-disclosure-label">{props.header}</span>
      </button>
      {props.peek}
      <div ref={body} id={bodyId} class="ui-disclosure-body" inert={props.expanded ? undefined : true}
        style={openness >= 1 ? undefined : { height: "0px", overflow: "hidden" }}>
        <div ref={content} class="ui-disclosure-content">{props.children}</div>
      </div>
    </div>
  );
}
