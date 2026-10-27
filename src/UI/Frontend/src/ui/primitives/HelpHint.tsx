import { createSignal, createUniqueId, onCleanup, onMount, Show, type JSX } from "solid-js";
import { Portal } from "solid-js/web";
import { CircleHelp } from "lucide-solid";
import { exitOnUnmount } from "../../presentation/nodeAnimation.ts";
import { uiText } from "../../text.ts";
import { HELP_HINT_SPACING, resolveAnchoredPlacement } from "./anchoredPlacement.ts";
import { graceArea, insideArea, type Point } from "./pointerGrace.ts";

/**
 * 问号说明：界面上一段说明不再藏在原生 title 里，而是旁边一个专门的问号按钮，说明画在我们自己的弹层上。
 *
 * 参与者：问号按钮、说明弹层（Portal 到 body，position: fixed，滚动容器裁不掉它）。开关只由这个组件持有，两个来源：
 * - 指针在里面：指针在问号上、说明上，或者正从其中一个移向另一个。离开一个时（落点不在另一个里），
 *   离开处和另一个的外框围成一块凸区域（pointerGrace.ts）；指针还在这块区域里就是在移过去的路上，走出去就算离开。
 *   斜着从问号侧边移向说明也算没离开。纯几何判断，不靠计时器去猜。触摸没有悬停，抬手就不算在里面。
 * - 焦点在里面：焦点在问号或说明上（Tab 到问号、点问号、在说明里按下去选字都会把焦点放进来）。焦点移到两者之外就不算。
 * 任一来源成立说明就开着，两个都不成立就关上。没有延迟，没有自动隐藏。
 * Esc：两个来源都清掉；焦点原来在问号或说明上时交还给问号（焦点在别处时只关说明，不抢焦点，也不吞掉这个键）。
 * 点问号：焦点来源成立（Esc 关掉之后，焦点还在问号上，再点一下重新打开）。
 * 在说明里按下去拖动选字：说明拿到焦点，指针拖出去也不关；松开后在别处按下（焦点离开）或按 Esc 才关。
 * 拖出说明时选区只停在说明里的字上（终点按指针收进正文框的位置算），不会选到后面页面的内容。
 * 说明里的字可以选中（user-select: text）。读屏：问号的 aria-describedby 指向说明。
 * 动画：说明和下拉框、右键菜单同属小弹层，进场只改不透明度（styles/motion.css 的小弹层规则），
 * 关上时由呈现层留一份只能看见的幽灵播放退出槽位（presentation/nodeAnimation.ts）；无动画、超高性能下当场出现、当场消失。
 */
export interface HelpHintProps {
  /** 说明正文。 */
  readonly children: JSX.Element;
  /** 问号按钮的无障碍名称；不给就是通用的「说明」（uiText.common.helpHint）。 */
  readonly label?: string;
  readonly class?: string;
}

export function HelpHint(props: HelpHintProps) {
  const popoverId = `help-hint-${createUniqueId()}`;
  let button: HTMLButtonElement | undefined;
  let popover: HTMLDivElement | undefined;
  const [pointerInside, setPointerInside] = createSignal(false);
  const [focusInside, setFocusInside] = createSignal(false);
  const open = () => pointerInside() || focusInside();

  const withinHint = (target: EventTarget | null) =>
    target instanceof Node && Boolean(button?.contains(target) || popover?.contains(target));

  // 正在从一个移向另一个时可以经过的区域；不在路上时是 null。
  let grace: Point[] | null = null;
  const onGraceMove = (event: PointerEvent) => {
    if (grace && !insideArea(grace, { x: event.clientX, y: event.clientY })) leave();
  };
  const endGrace = () => {
    if (!grace) return;
    grace = null;
    document.removeEventListener("pointermove", onGraceMove, true);
  };
  const leave = () => {
    endGrace();
    setPointerInside(false);
  };
  const onPointerEnter = () => {
    endGrace();
    setPointerInside(true);
  };
  const pointerLeft = (other: () => HTMLElement | undefined) => (event: PointerEvent) => {
    if (withinHint(event.relatedTarget)) return;
    const target = other();
    // 离开了窗口（relatedTarget 为 null）、触摸抬手、另一个不在：直接算离开。
    if (event.pointerType === "touch" || event.relatedTarget === null || !target?.isConnected) {
      leave();
      return;
    }
    endGrace();
    grace = graceArea({ x: event.clientX, y: event.clientY }, target.getBoundingClientRect());
    document.addEventListener("pointermove", onGraceMove, true);
  };
  const onFocusOut = (event: FocusEvent) => {
    if (!withinHint(event.relatedTarget)) setFocusInside(false);
  };
  const dismiss = (event: KeyboardEvent) => {
    if (withinHint(document.activeElement)) {
      event.preventDefault();
      event.stopPropagation();
      // 先交还焦点再清来源：焦点从说明回到问号时的 focusin 不会把说明重新打开。
      button?.focus({ preventScroll: true });
    }
    leave();
    setFocusInside(false);
  };
  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === "Escape" && open()) dismiss(event);
  };

  // 拖动选字只选说明里的字：说明挂在 body 末尾，原生选区拖到说明外面会一路选到页面里的内容。
  // 按下以后每一帧（画出来之前）看一次：选区的终点跑到说明外面，就把它挪到指针收进说明正文框里那一点的字符位置上，和在输入框里拖选一样。
  let dragFrame = 0;
  let dragPoint: Point | null = null;
  const confineSelection = () => {
    dragFrame = 0;
    const selection = document.getSelection();
    if (!dragPoint || !popover || !selection?.anchorNode || !selection.focusNode) return;
    if (!popover.contains(selection.anchorNode) || popover.contains(selection.focusNode)) return;
    const box = popover.getBoundingClientRect();
    const style = getComputedStyle(popover);
    const left = box.left + popover.clientLeft + parseFloat(style.paddingLeft);
    const top = box.top + popover.clientTop + parseFloat(style.paddingTop);
    const right = box.right - popover.clientLeft - parseFloat(style.paddingRight) - 1;
    const bottom = box.bottom - popover.clientTop - parseFloat(style.paddingBottom) - 1;
    const caret = document.caretPositionFromPoint(Math.min(right, Math.max(left, dragPoint.x)), Math.min(bottom, Math.max(top, dragPoint.y)));
    if (caret && popover.contains(caret.offsetNode)) {
      selection.setBaseAndExtent(selection.anchorNode, selection.anchorOffset, caret.offsetNode, caret.offset);
    }
  };
  const onDragMove = (event: PointerEvent) => {
    dragPoint = { x: event.clientX, y: event.clientY };
    if (dragFrame === 0) dragFrame = requestAnimationFrame(confineSelection);
  };
  const endDrag = () => {
    document.removeEventListener("pointermove", onDragMove, true);
    document.removeEventListener("pointerup", onDragEnd, true);
    document.removeEventListener("pointercancel", onDragEnd, true);
    if (dragFrame !== 0) cancelAnimationFrame(dragFrame);
    dragFrame = 0;
    dragPoint = null;
  };
  const onDragEnd = (event: PointerEvent) => {
    dragPoint = { x: event.clientX, y: event.clientY };
    confineSelection();
    endDrag();
  };
  const onPopoverPointerDown = (event: PointerEvent) => {
    if (event.button !== 0 || event.pointerType === "touch") return;
    endDrag();
    document.addEventListener("pointermove", onDragMove, true);
    document.addEventListener("pointerup", onDragEnd, true);
    document.addEventListener("pointercancel", onDragEnd, true);
  };

  // 只靠指针打开、焦点在别处时，Esc 也能关掉说明；这时不吞键，也不动焦点（dismiss 里判断）。
  const onDocumentKeyDown = (event: KeyboardEvent) => {
    if (event.key === "Escape" && pointerInside() && !focusInside()) dismiss(event);
  };
  document.addEventListener("keydown", onDocumentKeyDown);
  onCleanup(() => {
    document.removeEventListener("keydown", onDocumentKeyDown);
    endGrace();
    endDrag();
  });

  return (
    <>
      <button
        ref={button}
        type="button"
        class={["help-hint", open() ? "open" : "", props.class ?? ""].filter(Boolean).join(" ")}
        aria-label={props.label ?? uiText.common.helpHint}
        aria-describedby={popoverId}
        onPointerEnter={onPointerEnter}
        onPointerLeave={pointerLeft(() => popover)}
        onFocusIn={() => setFocusInside(true)}
        onFocusOut={onFocusOut}
        onClick={() => setFocusInside(true)}
        onKeyDown={onKeyDown}
      >
        <CircleHelp size={14} aria-hidden="true" />
      </button>
      <Show when={open()}>
        <HelpHintPopover
          id={popoverId}
          anchor={() => button}
          ref={(element) => { popover = element; }}
          onPointerEnter={onPointerEnter}
          onPointerLeave={pointerLeft(() => button)}
          onPointerDown={onPopoverPointerDown}
          onFocusIn={() => setFocusInside(true)}
          onFocusOut={onFocusOut}
          onKeyDown={(event) => {
            if (event.key === "Tab") {
              // 说明不在 Tab 顺序里（它在 body 末尾）：从说明里按 Tab 回到问号，接着按就是问号后面的控件。
              event.preventDefault();
              button?.focus({ preventScroll: true });
              return;
            }
            onKeyDown(event);
          }}
        >
          {props.children}
        </HelpHintPopover>
      </Show>
    </>
  );
}

function HelpHintPopover(props: {
  readonly id: string;
  readonly anchor: () => HTMLElement | undefined;
  readonly ref: (element: HTMLDivElement) => void;
  readonly onPointerEnter: () => void;
  readonly onPointerLeave: (event: PointerEvent) => void;
  readonly onPointerDown: (event: PointerEvent) => void;
  readonly onFocusIn: () => void;
  readonly onFocusOut: (event: FocusEvent) => void;
  readonly onKeyDown: (event: KeyboardEvent) => void;
  readonly children: JSX.Element;
}) {
  let element: HTMLDivElement | undefined;
  let frame = 0;
  let placed = "";

  // 和资源条提示一样：出现期间每一帧对着问号重算位置（页面滚动、窗口变化、问号随旁边的计数挪动时都跟着走），位置没变不写样式。
  const place = () => {
    const anchor = props.anchor();
    if (element && anchor?.isConnected) {
      const size = element.getBoundingClientRect();
      const viewport = document.documentElement;
      const placement = resolveAnchoredPlacement(
        anchor.getBoundingClientRect(),
        { left: 0, top: 0, right: viewport.clientWidth, bottom: viewport.clientHeight },
        size.width,
        size.height,
        HELP_HINT_SPACING);
      const key = `${placement.left}|${placement.top}|${placement.vertical}`;
      if (key !== placed) {
        placed = key;
        element.style.left = `${placement.left}px`;
        element.style.top = `${placement.top}px`;
        element.dataset.placement = placement.vertical;
      }
    }
  };
  const follow = () => {
    place();
    frame = requestAnimationFrame(follow);
  };

  // 挂上的这一刻就放好（不等下一帧），第一帧画出来就在问号旁边。
  onMount(follow);
  onCleanup(() => cancelAnimationFrame(frame));

  return (
    <Portal>
      <div
        ref={(node) => { element = node; props.ref(node); exitOnUnmount(node); }}
        id={props.id}
        class="help-hint-popover"
        role="tooltip"
        tabIndex={-1}
        data-modal-focus-portal="true"
        onPointerEnter={props.onPointerEnter}
        onPointerLeave={props.onPointerLeave}
        onPointerDown={props.onPointerDown}
        onFocusIn={props.onFocusIn}
        onFocusOut={props.onFocusOut}
        onKeyDown={props.onKeyDown}
      >
        {props.children}
      </div>
    </Portal>
  );
}
