import { onCleanup } from "solid-js";
import type { Accessor } from "solid-js";
import { REORDER_BOUND, reorderItemDefinition } from "../components/reorderItem.ts";
import type { Host, Presentation } from "../protocol/runtime.ts";
import { ZERO } from "../protocol/vec.ts";
import type { Vec } from "../protocol/vec.ts";
import { useInteraction } from "./lodestone.ts";

/*
 * 拖动排序的一组项（协议第 4.2 节第 4 项“宿主映射”、第 4.4 节第 3 处）。
 *
 * 每一项是一个拖动排序项组件（interactions/components/reorderItem.ts），同一组的项共用这一个宿主映射：
 * 它持有这一组的元素和 id。项调用 over(指针处)、commit(指针处) 时，按指针处的元素找同组的另一项（指针下是源自己或不在同组项上就没有目标），
 * 松开时有目标才交给业务 commit(源, 目标)；取消不交给业务。
 * 画面：源半透明由每一项的呈现层按模式画（拖动时加 drag-source）；目标虚线框由这里按当前目标画（drag-over）；
 * 拖动中整页的光标是抓取（body.reordering）。
 * 项里可用的原生控件（换指标、删除、添加、左右移、上下移这些按钮等）由呈现层声明成原生控件：落在上面的输入整个交给浏览器，
 * 不开始拖，按钮照常拿到点击和焦点（协议第 4.2 节第 2 项）。停用的控件算项的一部分（编辑时表头的排序按钮停用，整个表头格是把手）。项拖动中被卸载时，手势由运行时直接结束、不调用宿主，所以这里在卸载时收掉画面。
 */

/** 项里交给浏览器的原生控件：可用的按钮、输入框、下拉框、链接、可编辑区。 */
export const NATIVE_CONTROL = "button:not(:disabled),input:not(:disabled),select:not(:disabled),textarea:not(:disabled),a[href],[contenteditable='true']";

export interface ReorderGroup {
  /**
   * 一项：id 和能不能拖（不能拖时不参与路由，按下交给外层）。返回给项元素的 ref。
   * 在 Solid 组件的初始化里调用（项卸载时自动移出这一组）。
   */
  item(options: { id: Accessor<string>; enabled: Accessor<boolean> }): (element: HTMLElement) => void;
}

/** name 写进运行时日志；commit 只在松开时指针在同组另一项上时调用一次。 */
export function createReorderGroup(name: string, commit: (sourceId: string, targetId: string) => void): ReorderGroup {
  const ids = new Map<HTMLElement, Accessor<string>>();
  let source: HTMLElement | null = null;
  let target: HTMLElement | null = null;

  const setTarget = (next: HTMLElement | null) => {
    if (next === target) return;
    target?.classList.remove("drag-over");
    next?.classList.add("drag-over");
    target = next;
  };
  const end = () => {
    setTarget(null);
    source = null;
    document.body.classList.remove("reordering");
  };
  /** 指针处下面的同组另一项；在源自己上或不在同组项上为 null。 */
  const itemAt = (point: Vec, from: HTMLElement): HTMLElement | null => {
    const hit = document.elementFromPoint(point.x, point.y);
    if (!hit) return null;
    for (const element of ids.keys()) {
      if (element.contains(hit)) return element === from ? null : element;
    }
    return null;
  };

  return {
    item(options) {
      let element!: HTMLElement;
      const view = (): Presentation<Vec> => ({
        element,
        bindingLabel: "源半透明（按模式）",
        // 几何 1 个单位 = 1 个屏幕 px：两角就是通道边界的两角。
        geometry: () => ({
          center: "指针（排序项不跟手）",
          atMin: { x: -REORDER_BOUND, y: -REORDER_BOUND },
          atMax: { x: REORDER_BOUND, y: REORDER_BOUND }
        }),
        nativeControl: (target) => {
          const control = target instanceof Element ? target.closest(NATIVE_CONTROL) : null;
          return control !== null && control !== element && element.contains(control);
        },
        render: ({ mode }) => { element.classList.toggle("drag-source", mode === "拖动"); },
        aria: () => ""
      });
      const host: Host<Vec> = {
        value: () => ZERO,
        enabled: options.enabled,
        call: (port, args) => {
          if (port === "over") {
            if (source !== element) {
              source = element;
              document.body.classList.add("reordering");
            }
            setTarget(itemAt(args[0] as Vec, element));
            return;
          }
          if (port === "commit") {
            const to = itemAt(args[0] as Vec, element);
            end();
            if (to) commit(options.id(), ids.get(to)!());
            return;
          }
          if (port === "cancel") end();
        },
        // 宿主值不变（物体不跟手），没有变化可通知。
        subscribe: () => () => {}
      };
      useInteraction(`${name}「${options.id()}」`, reorderItemDefinition, view, host);
      onCleanup(() => {
        ids.delete(element);
        if (source === element) end();
        else if (target === element) setTarget(null);
      });
      return (el) => {
        element = el;
        // 按下时取得焦点（拖动中的 Esc 按焦点路由到这一项）；-1 不进 Tab 次序。
        if (!el.hasAttribute("tabindex")) el.tabIndex = -1;
        ids.set(el, options.id);
      };
    }
  };
}
