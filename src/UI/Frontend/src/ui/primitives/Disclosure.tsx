import { createComputed, createEffect, createUniqueId, on } from "solid-js";
import type { JSX } from "solid-js";
import { switchToggleDefinition } from "../../interactions/components/switchToggle.ts";
import type { Presentation } from "../../interactions/protocol/runtime.ts";
import { solidHost, useInteraction } from "../../interactions/solid/lodestone.ts";
import { enterSlot, exitInPlace } from "../../presentation/nodeAnimation.ts";

/**
 * 折叠标题：原生按钮加可以收起的内容。
 * - 展开与否由调用方持有（expanded / onToggle）；点击、Enter、空格由按钮自己处理。
 * - 交互和无动画完全一致（docs/animation_style_2026-10-26.md「总原则」）：展开、收起的那一刻内容就在终态布局上，
 *   展开即可点、可 Tab 进去；收起即让位、inert（不能 Tab 进去，辅助技术也读不到）。高度不做动画：它会挪动下面内容的命中区域。
 * - 画面交给呈现层（presentation/nodeAnimation.ts）：展开时内容播放进场槽位 disclosure（淡入）；
 *   收起时内容留一份幽灵（放在折叠块里，按内容原来的位置），播放退出槽位 disclosure（淡出）。
 * - 箭头的转角是同一个节点自身的连续运动，照旧由磁石带（协议 4.6：演示的折叠面板和开关是同一份定义，这里就用设置开关的定义）：
 *   宿主值变了，回点弹簧把开度带到 0 或 1，写在按钮的 --disclosure-openness。没有几何，不能拖。
 * - 无动画、极简档由运行时直接到终态，进场、退出槽位是空的。
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
  let root!: HTMLDivElement;
  let header!: HTMLButtonElement;
  let content!: HTMLDivElement;
  // 收起之前（DOM 还没改）：内容留幽灵。展开之后：内容进场。
  createComputed(on(() => props.expanded, (expanded) => {
    if (!expanded && content) exitInPlace(content, { slot: "disclosure", layer: root });
  }, { defer: true }));
  createEffect(on(() => props.expanded, (expanded) => {
    if (expanded) enterSlot(content, "disclosure");
  }, { defer: true }));
  const view = (): Presentation => ({
    element: header,
    bindingLabel: "箭头转角",
    geometry: () => null,
    render: ({ x }) => {
      header.style.setProperty("--disclosure-openness", x.toFixed(4));
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
  // 箭头的起始转角只在挂上时写一次，之后由运行时画（Solid 不再改它，免得终态先闪一帧）。
  const initialOpenness = props.expanded ? 1 : 0;
  return (
    <div ref={root} class={props.class ? `ui-disclosure ${props.class}` : "ui-disclosure"}>
      <button ref={header} type="button" class={props.headerClass ? `ui-disclosure-header ${props.headerClass}` : "ui-disclosure-header"}
        aria-expanded={props.expanded} aria-controls={bodyId} onClick={() => props.onToggle()}
        style={{ "--disclosure-openness": initialOpenness }}>
        <span class="ui-disclosure-chevron" aria-hidden="true" />
        <span class="ui-disclosure-label">{props.header}</span>
      </button>
      {props.peek}
      <div id={bodyId} class="ui-disclosure-body" inert={props.expanded ? undefined : true} hidden={!props.expanded}>
        <div ref={content} class="ui-disclosure-content">{props.children}</div>
      </div>
    </div>
  );
}
