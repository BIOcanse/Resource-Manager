import { onCleanup, onMount, splitProps, type JSX } from "solid-js";
import { Portal } from "solid-js/web";
import { exitOnUnmount } from "../../presentation/nodeAnimation.ts";
import { resolveAnchoredPlacement, type AnchoredSpacing } from "./anchoredPlacement.ts";

export type AnchoredLayerProps = Omit<JSX.HTMLAttributes<HTMLDivElement>, "ref"> & {
  /** Element followed by the layer. */
  readonly anchor: () => HTMLElement | null | undefined;
  /** Named anchor gap and viewport inset. */
  readonly spacing: AnchoredSpacing;
  readonly ref?: (element: HTMLDivElement) => void;
  /** Called when the anchor is detached or hidden. Without a callback, positioning stops. */
  readonly onAnchorLost?: () => void;
};

/** Portal a fixed layer to body to escape clipping. Position immediately, then follow the anchor each frame; hide until positioned and skip unchanged styles. */
export function AnchoredLayer(props: AnchoredLayerProps) {
  const [local, rest] = splitProps(props, ["anchor", "spacing", "ref", "onAnchorLost", "children"]);
  let element: HTMLDivElement | undefined;
  let frame = 0;
  let placed = "";

  const place = () => {
    const anchor = local.anchor();
    if (!anchor?.isConnected || !anchor.checkVisibility()) {
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
