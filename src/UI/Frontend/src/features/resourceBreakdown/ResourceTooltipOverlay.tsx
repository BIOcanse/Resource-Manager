import { onCleanup, onMount, type JSX } from "solid-js";
import { Portal } from "solid-js/web";
import { resolveResourceTooltipPlacement } from "./resourceTooltipPlacement.ts";

/**
 * 资源条的悬停提示。挂在页面最顶层（和下拉框的弹出层一样，Portal 到 body，position: fixed），不在资源条那一行里，
 * 所以下一行资源条、卡片和滚动容器都盖不住、裁不掉它。
 * 出现期间每一帧对着悬停的那一段（anchor）重算位置：数据过渡中这一段在动、换到另一段、页面滚动或窗口变化时，
 * 提示都跟着走；位置没变时不写样式。放好之前不显示（CSS 按 data-placement 判断），不会在左上角闪一下。
 * 读屏不读它：这一段的无障碍名称里已经是同样的内容。
 */
export function ResourceTooltipOverlay(props: { anchor: () => HTMLElement | undefined; children: JSX.Element }) {
  let tooltip: HTMLDivElement | undefined;
  let frame = 0;
  let placed = "";

  const place = () => {
    const anchor = props.anchor();
    if (tooltip && anchor?.isConnected) {
      const size = tooltip.getBoundingClientRect();
      const viewport = document.documentElement;
      const placement = resolveResourceTooltipPlacement(
        anchor.getBoundingClientRect(),
        { left: 0, top: 0, right: viewport.clientWidth, bottom: viewport.clientHeight },
        size.width,
        size.height);
      const key = `${placement.left}|${placement.top}|${placement.vertical}`;
      if (key !== placed) {
        placed = key;
        tooltip.style.left = `${placement.left}px`;
        tooltip.style.top = `${placement.top}px`;
        tooltip.dataset.placement = placement.vertical;
      }
    }
    frame = requestAnimationFrame(place);
  };

  onMount(() => {
    frame = requestAnimationFrame(place);
  });
  onCleanup(() => cancelAnimationFrame(frame));

  return (
    <Portal>
      <div ref={(element) => { tooltip = element; }} class="resource-tooltip" aria-hidden="true">
        {props.children}
      </div>
    </Portal>
  );
}
