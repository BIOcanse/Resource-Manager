import type { JSX } from "solid-js";
import { AnchoredLayer } from "../../ui/primitives/AnchoredLayer.tsx";
import { RESOURCE_TOOLTIP_SPACING } from "../../ui/primitives/anchoredPlacement.ts";

/**
 * 资源条的悬停提示。挂在页面最顶层（共用的 AnchoredLayer：Portal 到 body，position: fixed，和下拉框的弹出层同一层），
 * 不在资源条那一行里，所以下一行资源条、卡片和滚动容器都盖不住、裁不掉它。
 * 出现期间每一帧对着悬停的那一段（anchor）重算位置：数据过渡中这一段在动、换到另一段、页面滚动或窗口变化时，
 * 提示都跟着走。在分段之间移动时不重挂，只换内容。
 * 读屏不读它：这一段的无障碍名称里已经是同样的内容。
 */
export function ResourceTooltipOverlay(props: { anchor: () => HTMLElement | undefined; children: JSX.Element }) {
  return (
    <AnchoredLayer anchor={props.anchor} spacing={RESOURCE_TOOLTIP_SPACING} class="resource-tooltip" aria-hidden="true">
      {props.children}
    </AnchoredLayer>
  );
}
