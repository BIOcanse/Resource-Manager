import type { JSX } from "solid-js";
import { AnchoredLayer } from "../../ui/primitives/AnchoredLayer.tsx";
import { RESOURCE_TOOLTIP_SPACING } from "../../ui/primitives/anchoredPlacement.ts";

/** Portal the tooltip above scroll containers and follow the moving anchor each frame. Reuse it between segments; accessible names already contain its text. */
export function ResourceTooltipOverlay(props: { anchor: () => HTMLElement | undefined; children: JSX.Element }) {
  return (
    <AnchoredLayer anchor={props.anchor} spacing={RESOURCE_TOOLTIP_SPACING} class="resource-tooltip" aria-hidden="true">
      {props.children}
    </AnchoredLayer>
  );
}
