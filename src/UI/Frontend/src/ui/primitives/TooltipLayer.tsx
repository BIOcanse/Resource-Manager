import { createEffect, createMemo, createSignal, onCleanup, Show } from "solid-js";
import { AnchoredLayer } from "./AnchoredLayer.tsx";
import { HOVER_TOOLTIP_SPACING } from "./anchoredPlacement.ts";

/** 名称提示：元素带这个属性（值为空），提示显示它的无障碍名称（aria-label）。图标按钮用。 */
export const NAME_TOOLTIP_ATTRIBUTE = "data-name-tooltip";
/** 完整内容提示：提示显示这个属性的值。截断了的文字、一格或一段的完整信息用。 */
export const FULL_CONTENT_ATTRIBUTE = "data-full-content";

const ANCHOR_SELECTOR = `[${NAME_TOOLTIP_ATTRIBUTE}], [${FULL_CONTENT_ATTRIBUTE}]`;
const SOURCE_ATTRIBUTES = [NAME_TOOLTIP_ATTRIBUTE, FULL_CONTENT_ATTRIBUTE, "aria-label"];

function anchorOf(target: EventTarget | null): HTMLElement | null {
  return target instanceof Element ? target.closest<HTMLElement>(ANCHOR_SELECTOR) : null;
}

function tooltipText(anchor: HTMLElement): string {
  const text = anchor.hasAttribute(FULL_CONTENT_ATTRIBUTE)
    ? anchor.getAttribute(FULL_CONTENT_ATTRIBUTE)
    : anchor.getAttribute("aria-label");
  return text?.trim() ?? "";
}

/**
 * 名称提示和完整内容提示：替代浏览器原生 title 的悬停提示，整个应用只挂一个（App.tsx）。
 *
 * 页面上的元素用属性声明自己要哪一种（NAME_TOOLTIP_ATTRIBUTE、FULL_CONTENT_ATTRIBUTE），文字取自元素当前的属性，属性变了提示跟着变。
 * 「现在给哪个元素显示提示」只由这里持有，两个来源：
 * - 指针在元素上（含它的子元素）。离开窗口算离开。触摸没有悬停，不显示。
 * - 键盘焦点在元素上（:focus-visible）。点一下得到的焦点不算，点完指针移开提示就收。
 * 指针来源优先。Esc 或按下指针：收起当前这个，直到指针或焦点换到别的元素（离开再回来会重新显示）。
 * 元素被卸下或去掉了声明就收起。没有延迟，没有自动隐藏：和资源条提示一样当场出现、当场收起，进场和退场的淡入淡出由呈现层按动画风格给。
 * 弹层不接收指针（不挡住后面的控件），读屏不读它：名称已经在 aria-label 里，完整内容由各处自己让读屏读得到（可见文字或 aria-description）。
 * 外观和问号说明、资源条提示同一套弹层令牌（styles/primitives.css .hover-tooltip），位置规则同一个（anchoredPlacement.ts）。
 */
export function TooltipLayer() {
  const [hovered, setHovered] = createSignal<HTMLElement | null>(null);
  const [focused, setFocused] = createSignal<HTMLElement | null>(null);
  const [dismissed, setDismissed] = createSignal<HTMLElement | null>(null);
  const [revision, setRevision] = createSignal(0);
  const anchor = createMemo(() => {
    const current = hovered() ?? focused();
    return current && current !== dismissed() ? current : null;
  });
  const text = createMemo(() => {
    revision();
    const current = anchor();
    return current ? tooltipText(current) : "";
  });

  // 来源换到别的元素（或者没有了）时，Esc／按下收起的那一个不再压着。
  const hover = (next: HTMLElement | null) => {
    if (next === hovered()) return;
    if (next !== dismissed()) setDismissed(null);
    setHovered(next);
  };
  const focus = (next: HTMLElement | null) => {
    if (next === focused()) return;
    if (next !== dismissed()) setDismissed(null);
    setFocused(next);
  };
  const drop = (element: HTMLElement | null) => {
    if (!element) return;
    if (hovered() === element) hover(null);
    if (focused() === element) focus(null);
  };
  const dismiss = () => {
    const current = anchor();
    if (current) setDismissed(current);
  };

  const onPointerOver = (event: PointerEvent) => {
    if (event.pointerType !== "touch") hover(anchorOf(event.target));
  };
  const onPointerOut = (event: PointerEvent) => {
    if (event.pointerType !== "touch" && event.relatedTarget === null) hover(null);
  };
  const onFocusIn = (event: FocusEvent) => {
    const target = event.target;
    focus(target instanceof Element && target.matches(":focus-visible") ? anchorOf(target) : null);
  };
  const onFocusOut = () => focus(null);
  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === "Escape") dismiss();
  };
  document.addEventListener("pointerover", onPointerOver, true);
  document.addEventListener("pointerout", onPointerOut, true);
  document.addEventListener("pointerdown", dismiss, true);
  document.addEventListener("focusin", onFocusIn, true);
  document.addEventListener("focusout", onFocusOut, true);
  document.addEventListener("keydown", onKeyDown, true);
  onCleanup(() => {
    document.removeEventListener("pointerover", onPointerOver, true);
    document.removeEventListener("pointerout", onPointerOut, true);
    document.removeEventListener("pointerdown", dismiss, true);
    document.removeEventListener("focusin", onFocusIn, true);
    document.removeEventListener("focusout", onFocusOut, true);
    document.removeEventListener("keydown", onKeyDown, true);
  });

  // 只看正在显示的这一个元素：文字属性变了重读，声明去掉了就收起。
  createEffect(() => {
    const current = anchor();
    if (!current) return;
    const observer = new MutationObserver(() => {
      if (current.matches(ANCHOR_SELECTOR)) setRevision((value) => value + 1);
      else drop(current);
    });
    observer.observe(current, { attributes: true, attributeFilter: SOURCE_ATTRIBUTES });
    onCleanup(() => observer.disconnect());
  });

  return (
    <Show when={text() !== ""}>
      <AnchoredLayer
        anchor={anchor}
        spacing={HOVER_TOOLTIP_SPACING}
        class="hover-tooltip"
        aria-hidden="true"
        onAnchorLost={() => drop(anchor())}
      >
        {text()}
      </AnchoredLayer>
    </Show>
  );
}
