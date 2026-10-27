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
  return (
    <div class="page-host" data-page-id={props.page} aria-hidden={props.active ? undefined : "true"}>
      <PageActiveContext.Provider value={active}>
        <Show when={built()}>{props.children}</Show>
      </PageActiveContext.Provider>
    </div>
  );
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
