import { onMount } from "solid-js";
import type { Presentation } from "../protocol/runtime.ts";
import { dialogEntranceDefinition } from "../components/dialogEntrance.ts";
import { useInteraction } from "./lodestone.ts";
import { createOrderHost } from "./orderHost.ts";

/*
 * 弹窗入场的呈现（定义在 ../components/dialogEntrance.ts）。
 * 挂上时先画开度 0（看不见，下一画之前不会露出终态），再把宿主值改成 1；每一画按开度 x 写：
 * 对话框不透明度 x、向上 (1 − x) × 6 px、缩放 0.97 → 1（原来的 CSS 入场 motion-dialog-in 的同一组量），遮罩不透明度 x。
 * 到 1 时清掉这些内联样式，不留 transform（免得改变对话框里固定定位的元素）。开度不越过 0–1：弹簧冲过 1 时按 1 画。
 */

/** 入场开始时对话框在终点下方几 px、缩小到多少。 */
const RISE_PX = 6;
const START_SCALE = 0.97;

export function useDialogEntrance(options: {
  readonly name: string;
  readonly dialog: () => HTMLElement | undefined;
  readonly backdrop: () => HTMLElement | undefined;
}): void {
  const host = createOrderHost(0);
  const draw = (x: number) => {
    const dialog = options.dialog();
    const backdrop = options.backdrop();
    if (!dialog || !backdrop) return;
    const p = Math.min(1, Math.max(0, x));
    if (p >= 1) {
      for (const element of [dialog, backdrop]) {
        element.style.removeProperty("opacity");
        element.style.removeProperty("will-change");
      }
      dialog.style.removeProperty("transform");
      return;
    }
    dialog.style.willChange = "transform, opacity";
    dialog.style.opacity = p.toFixed(4);
    dialog.style.transform = `translateY(${((1 - p) * RISE_PX).toFixed(2)}px) scale(${(START_SCALE + (1 - START_SCALE) * p).toFixed(4)})`;
    backdrop.style.opacity = p.toFixed(4);
  };
  onMount(() => draw(0));
  const view = (): Presentation => ({
    element: options.dialog(),
    bindingLabel: "对话框和遮罩的不透明度、位移、缩放",
    geometry: () => null,
    render: (d) => draw(d.x as number),
    aria: () => ""
  });
  useInteraction(options.name, dialogEntranceDefinition, view, host.host);
  onMount(() => host.set(1, "对话框打开"));
}
