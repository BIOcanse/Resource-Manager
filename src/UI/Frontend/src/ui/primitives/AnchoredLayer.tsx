import { onCleanup, onMount, splitProps, type JSX } from "solid-js";
import { Portal } from "solid-js/web";
import { exitOnUnmount } from "../../presentation/nodeAnimation.ts";
import { resolveAnchoredPlacement, type AnchoredSpacing } from "./anchoredPlacement.ts";

export type AnchoredLayerProps = Omit<JSX.HTMLAttributes<HTMLDivElement>, "ref"> & {
  /** 弹层跟着的元素。 */
  readonly anchor: () => HTMLElement | null | undefined;
  /** 离锚点、离视口四边的距离（anchoredPlacement.ts 的具名间距）。 */
  readonly spacing: AnchoredSpacing;
  readonly ref?: (element: HTMLDivElement) => void;
  /** 锚点不在页面上了（被卸下）。不给就只是不再挪位置。 */
  readonly onAnchorLost?: () => void;
};

/**
 * 挂在页面最顶层、跟着一个锚点的小弹层：资源条提示、问号说明、名称提示和完整内容提示都用它，外观各由自己的 class 给。
 * Portal 到 body，position: fixed，所以下一行、卡片和滚动容器都盖不住、裁不掉它。
 * 挂上的这一刻就放好（不等下一帧），之后每一帧对着锚点重算位置（anchoredPlacement.ts）：锚点在动、页面滚动、窗口变化时都跟着走，
 * 位置没变时不写样式。放好之前样式表按缺 data-placement 不显示它，不会在左上角闪一下。
 * 卸下时由呈现层留一份只能看见的幽灵播放退出槽位（presentation/nodeAnimation.ts）。
 */
export function AnchoredLayer(props: AnchoredLayerProps) {
  const [local, rest] = splitProps(props, ["anchor", "spacing", "ref", "onAnchorLost", "children"]);
  let element: HTMLDivElement | undefined;
  let frame = 0;
  let placed = "";

  const place = () => {
    const anchor = local.anchor();
    if (!anchor?.isConnected) {
      local.onAnchorLost?.();
      return;
    }
    if (!element) return;
    const size = element.getBoundingClientRect();
    const viewport = document.documentElement;
    const placement = resolveAnchoredPlacement(
      anchor.getBoundingClientRect(),
      { left: 0, top: 0, right: viewport.clientWidth, bottom: viewport.clientHeight },
      size.width,
      size.height,
      local.spacing);
    const key = `${placement.left}|${placement.top}|${placement.vertical}`;
    if (key !== placed) {
      placed = key;
      element.style.left = `${placement.left}px`;
      element.style.top = `${placement.top}px`;
      element.dataset.placement = placement.vertical;
    }
  };
  const follow = () => {
    place();
    frame = requestAnimationFrame(follow);
  };

  onMount(follow);
  onCleanup(() => cancelAnimationFrame(frame));

  return (
    <Portal>
      <div {...rest} ref={(node) => { element = node; local.ref?.(node); exitOnUnmount(node); }}>
        {local.children}
      </div>
    </Portal>
  );
}
