import { Show, createContext, createEffect, createMemo, on, onCleanup, useContext } from "solid-js";
import type { Accessor, JSX } from "solid-js";
import { ResourceManager } from "../../preferences/generated/frontendPoints.values.ts";
import type { PageId } from "../../types.ts";

/*
 * 一级页在 #pageStage 里各有一个位置（.page-host，常在，次序按 AppRoutes 里写的）；只有当前页的位置显示。
 *
 * 页面有两种挂法，由自由点 refresh-kept_pages 决定（preferences.points.json，默认只有监视页）：
 * - 常驻页：第一次打开时建好，之后离开只把它的位置标成不显示，回来时原样显示，不重建 DOM。
 *   页面里的界面状态（表格滚动到的位置、展开的行等）随 DOM 一起留着。
 * - 其余页：打开时建，离开时卸下（Solid 清理）。
 * 不显示的位置 aria-hidden，并且 content-visibility: hidden（styles/layout.css）：里面的内容不排版、不绘制、不可命中、
 * 不可聚焦、不进 Tab 顺序，浏览器留着已算好的样式和布局，回来时不用重算整页。不用 inert：浏览器把 inert 随样式传给
 * 每个子孙，切换它要把整页的样式重算一遍（实测监视页回来时约 30 ms），content-visibility: hidden 已经不可交互。
 * 里面的可见区域不和视口相交，按可见区域取得的订阅（frontendWork/useFrontendWorkController.ts）照常退掉，
 * 回来、重新相交后再取得。
 * 页面离开时要做的事（取消未保存的编辑等）用 onPageLeave 声明：两种挂法都在离开那一刻调用。
 * 常驻页隐藏后的第一帧，页里正在播的 CSS 过渡和动画暂停，再显示时接着播（见 pauseHiddenAnimations）。
 * 选项里只列已核对过隐藏时行为的页面（离开时要做的事都走 onPageLeave、不挂全局监听、订阅都随可见区域）。
 */

/** 常驻页（自由点 refresh-kept_pages）。 */
export const keptPages: ReadonlySet<PageId> = new Set<PageId>(ResourceManager.FrontendPoints.KeptPages);

const PageActiveContext = createContext<Accessor<boolean>>();

export function PageHost(props: { readonly page: PageId; readonly active: boolean; readonly children: JSX.Element }) {
  const kept = keptPages.has(props.page);
  // 常驻页一旦打开就一直建着；其余页只在当前时建着。
  const built = createMemo<boolean>((wasBuilt) => props.active || (kept && wasBuilt), false);
  const active = () => props.active;
  let host: HTMLDivElement | undefined;
  if (kept) {
    // 隐藏时暂停的动画，再显示时恢复播放；只有常驻页隐藏后 DOM 还在。
    // 暂停放到隐藏后的第一帧里做：取页里的动画要先把样式算好，放在换页的那个任务里，就把新页面的样式计算也拉进了这个任务；
    // 在帧里这一次样式计算本来就要做。
    let paused: readonly Animation[] = [];
    let pausing = 0;
    createEffect(on(active, (now) => {
      cancelAnimationFrame(pausing);
      pausing = 0;
      if (!host) return;
      const element = host;
      if (now) {
        for (const animation of paused) if (animation.playState === "paused") animation.play();
        paused = [];
      } else {
        pausing = requestAnimationFrame(() => {
          pausing = 0;
          paused = pauseHiddenAnimations(element);
        });
      }
    }, { defer: true }));
    onCleanup(() => cancelAnimationFrame(pausing));
  }
  return (
    <div ref={host} class="page-host" data-page-id={props.page} aria-hidden={props.active ? undefined : "true"}>
      <PageActiveContext.Provider value={active}>
        <Show when={built()}>{props.children}</Show>
      </PageActiveContext.Provider>
    </div>
  );
}

/**
 * 常驻页刚被隐藏（content-visibility: hidden）：浏览器不再给里面的元素算样式，隐藏时正在播的 CSS 过渡（例如刚换过配色、
 * 指针刚离开一个按钮）播完以后清不掉，一直留在动画时间线上，让主线程每个刷新周期都出一帧空帧
 * （实测 180 Hz 下每秒约 180 次 Commit，页面什么也没画；无头和有界面的 Edge 都会）。
 * 所以隐藏后的第一帧：正在播的过渡和动画暂停，返回它们，由再显示时接着播；已经播完、只等样式更新清掉的过渡直接取消
 * （过渡的终值就是元素当前的样式，取消前后画面一样）。只在隐藏时取一次页里的动画，显示时不再取。
 */
function pauseHiddenAnimations(host: Element): Animation[] {
  const paused: Animation[] = [];
  for (const animation of host.getAnimations({ subtree: true })) {
    if (animation.playState === "running") {
      animation.pause();
      paused.push(animation);
    } else if (animation.playState === "finished" && animation instanceof CSSTransition) {
      animation.cancel();
    }
  }
  return paused;
}

/**
 * 离开这一页时调用 leave：常驻页被隐藏时，或页面卸下时（卸下前它正显示着）。在页面组件的初始化里调用。
 * 不在一级页里（没有 PageHost）时就是卸下时调用。
 */
export function onPageLeave(leave: () => void): void {
  const active = useContext(PageActiveContext);
  // 页面只在显示着的时候建起来，所以初始化时它正显示着。
  let shown = true;
  const left = () => {
    if (!shown) return;
    shown = false;
    leave();
  };
  if (active) {
    createEffect(on(active, (now) => {
      if (now) shown = true;
      else left();
    }, { defer: true }));
  }
  onCleanup(left);
}
