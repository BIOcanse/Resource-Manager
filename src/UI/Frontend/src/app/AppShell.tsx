import { For, Show, createEffect, createMemo, on, onCleanup, onMount } from "solid-js";
import type { Accessor, JSX } from "solid-js";
import { ListTodo } from "lucide-solid";
import { ObservationStateNotice } from "../components/ObservationStateNotice";
import type { RuntimeCapabilitiesStore } from "../stores/runtimeCapabilitiesStore";
import type { PageId } from "../types";
import { postShellMessage } from "../utils";
import { PageBoundary } from "../ui/patterns/PageBoundary.tsx";
import { TabsList, TabsRoot, TabsTrigger } from "../ui/primitives/Tabs.tsx";
import type {
  TaskCenterOperationState
} from "../frontendRuntime/taskCenter/TaskCenterProjection.ts";
import { uiText } from "../text.ts";
import type { NavigationTree, SecondaryNavigation, SecondaryTab } from "./navigation/navigationTree.ts";
import { useSelectionIndicator } from "../interactions/solid/selectionIndicator.ts";
import { useContentTransition } from "../interactions/solid/contentTransition.ts";
import { CurrentPageNameProvider } from "./navigation/currentPageName.tsx";

interface AppShellProps {
  activePage: Accessor<PageId>;
  pageTitle: Accessor<string>;
  systemUptime: Accessor<string>;
  /** 分页树（docs/navigation_tree_2026-10-25.md）：两条分页条都按它画。 */
  navigation: NavigationTree;
  runtimeCapabilities: RuntimeCapabilitiesStore;
  taskCenterActiveCount: Accessor<number>;
  taskCenterOperationState: Accessor<TaskCenterOperationState>;
  /** 深层导航目标由已挂载视图提供；常规导航仍聚焦页面标题。 */
  pageFocusTarget?: Accessor<HTMLElement | undefined>;
  onPageTargetFocused?: (target: HTMLElement) => void;
  onPageSelect: (page: PageId) => void;
  onTaskCenterOpen: () => void;
  /** 页面上方、不随换页过渡的提示（可信访问待处理、语言载入失败）。 */
  notices: JSX.Element;
  /** 当前页的内容。 */
  children: JSX.Element;
}

export function AppShell(props: AppShellProps) {
  let mainViewport: HTMLElement | undefined;
  let pageHeading: HTMLHeadingElement | undefined;
  let pageNav: HTMLElement | undefined;
  let primaryIndicator: HTMLElement | undefined;
  let subnav: HTMLElement | undefined;
  let subnavSlot: HTMLElement | undefined;
  let pageStage: HTMLElement | undefined;
  let pageLayer: HTMLElement | undefined;

  const tree = props.navigation;
  const visiblePages = createMemo(() => tree.pages.filter((page) => page.visible()));
  /** 有二级分页的一级页：每页一组分页，和外壳一起建好，之后只显示、隐藏，不重建。 */
  const pagesWithSecondary = tree.pages.filter((page) => page.secondary);
  const secondary = createMemo(() => tree.page(props.activePage()).secondary);
  const secondarySelected = () => secondary()?.selected() ?? null;
  const routeIdentity = () => `${props.activePage()}:${secondarySelected() ?? ""}`;
  const routeOrder = () => tree.order(props.activePage(), secondarySelected());
  const primaryOrder = () => tree.pages.findIndex((page) => page.id === props.activePage());

  createEffect(() => {
    document.title = uiText.shell.documentTitle(props.pageTitle(), uiText.shell.productName);
  });

  // 换一级页：正文回到顶部，焦点交给页标题。只换二级分页：正文回到顶部，焦点留在分页条上（←/→ 接着切）。
  // 深层导航（可信访问待处理横幅）由已挂载的视图交出焦点目标，焦点交给它；消费请求或卸载目标不会再次抢回焦点。
  let displayedPage = props.activePage();
  createEffect(on([routeIdentity, () => props.pageFocusTarget?.()], ([route, target], previous) => {
    const routeChanged = route !== previous?.[0];
    if (!routeChanged && !target) return;
    const pageChanged = props.activePage() !== displayedPage;
    displayedPage = props.activePage();
    let cancelled = false;
    onCleanup(() => { cancelled = true; });
    queueMicrotask(() => {
      if (cancelled) return;
      if (routeChanged) mainViewport?.scrollTo({ top: 0, left: 0, behavior: "auto" });
      if (target) {
        if (target.isConnected) {
          target.focus({ preventScroll: true });
          props.onPageTargetFocused?.(target);
        }
      } else if (pageChanged) {
        pageHeading?.focus({ preventScroll: true });
      }
    });
  }, { defer: true }));

  // 页签放不下时分页条横向滚动：切页后、分页条变宽变窄后，把选中项滚回可见范围。
  createEffect(on(routeIdentity, () => queueMicrotask(revealSelected), { defer: true }));
  onMount(() => {
    const observer = new ResizeObserver(revealSelected);
    if (pageNav) observer.observe(pageNav);
    if (subnav) observer.observe(subnav);
    onCleanup(() => observer.disconnect());
  });

  function revealSelected() {
    reveal(pageNav, pageNav?.querySelector<HTMLElement>(".shell-page.active") ?? null);
    reveal(subnav, subnavSlot?.querySelector<HTMLElement>(".page-subnav-group:not([hidden]) [role='tab'][aria-selected='true']") ?? null);
  }

  // 一级页签的指示条：序号是一级次序。
  useSelectionIndicator({
    name: "一级页签",
    container: () => pageNav,
    indicator: () => primaryIndicator,
    selected: () => pageNav?.querySelector<HTMLElement>(".shell-page.active") ?? null,
    items: () => [...(pageNav?.querySelectorAll<HTMLElement>(".shell-page") ?? [])],
    order: primaryOrder
  });
  // 二级分页条常驻，每个一级页的那组分页也常驻：换一级页时这一组直接显示、别的组隐藏，不进场；
  // 每组自带选中指示条（页签组），组里换分页时指示条滑过去，回到这一页时它还停在原来的分页上。
  // 页面内容：换一级页时旧页的节点被 Solid 换掉，挪进退出层；只换二级分页时页面就地更新，先存一份静态副本。
  useContentTransition({
    name: "页面内容",
    key: routeIdentity,
    order: routeOrder,
    stage: () => pageStage,
    layer: () => pageLayer,
    snapshot: () => (props.activePage() !== displayedPage ? "move" : "clone")
  });

  return (
    <>
      <header class="window-shellbar">
        <nav ref={(element) => { pageNav = element; }} class="shell-pages has-selection-indicator" aria-label={uiText.shell.pageNav}>
          <span ref={(element) => { primaryIndicator = element; }} class="selection-indicator page-tab-indicator" aria-hidden="true" />
          <For each={visiblePages()}>
            {(page) => (
              <button
                class="shell-page page-tab"
                classList={{ active: props.activePage() === page.id }}
                type="button"
                data-page-id={page.id}
                aria-current={props.activePage() === page.id ? "page" : undefined}
                onClick={() => props.onPageSelect(page.id)}
              >
                {page.label()}
              </button>
            )}
          </For>
        </nav>
        <div class="window-drag-region" data-window-drag onPointerDown={(event) => {
          if (event.button === 0) {
            event.preventDefault();
            postShellMessage("window.drag");
          }
        }}>
          <strong>{uiText.shell.productName}</strong>
          <span class="shell-uptime" title={uiText.shell.uptimeTitle}>
            <span class="shell-uptime-label">{uiText.shell.uptime}</span>
            <span id="captureTime">{props.systemUptime()}</span>
          </span>
        </div>
        <div class="shell-utility-actions">
          <button
            id="taskCenterButton"
            class="shell-task-button"
            type="button"
            aria-label={props.taskCenterActiveCount() > 0
              ? uiText.shell.taskCenterActive(props.taskCenterActiveCount())
              : uiText.shell.taskCenter}
            aria-haspopup="dialog"
            title={taskCenterTitle(props.taskCenterOperationState())}
            onClick={props.onTaskCenterOpen}
          >
            <ListTodo aria-hidden="true" size={18} />
            <Show when={props.taskCenterActiveCount() > 0}>
              <span class="shell-task-count" aria-hidden="true">
                {props.taskCenterActiveCount() > 99 ? "99+" : props.taskCenterActiveCount()}
              </span>
            </Show>
          </button>
        </div>
        <div class="window-actions">
          <button class="window-button" type="button" aria-label={uiText.shell.minimize} onClick={() => postShellMessage("window.minimize")}>−</button>
          <button class="window-button" type="button" aria-label={uiText.shell.maximize} onClick={() => postShellMessage("window.maximize")}>□</button>
          <button id="windowCloseButton" class="window-button" type="button" aria-label={uiText.shell.closeWindow} onClick={() => postShellMessage("window.close")}>×</button>
        </div>
      </header>

      <div ref={(element) => { subnav = element; }} id="pageSubnav" class="page-subnav">
        <div ref={(element) => { subnavSlot = element; }} class="page-subnav-slot">
          <For each={pagesWithSecondary}>
            {(page) => (
              <div class="page-subnav-group" data-page-id={page.id} hidden={props.activePage() !== page.id}>
                <SecondaryTabs page={page.id} navigation={page.secondary!} />
              </div>
            )}
          </For>
        </div>
      </div>

      <div ref={(element) => { pageLayer = element; }} class="page-leaving-layer" aria-hidden="true" />
      <main
        ref={(element) => { mainViewport = element; }}
        class="app-shell"
        aria-labelledby="activePageTitle"
      >
        <h1
          id="activePageTitle"
          ref={(element) => { pageHeading = element; }}
          class="app-page-title"
          tabIndex={-1}
        >
          {props.pageTitle()}
        </h1>
        <ObservationStateNotice
          state={props.runtimeCapabilities.observation()}
          label={uiText.shell.runtimeCapability}
          onRetry={() => void props.runtimeCapabilities.refresh()}
        />
        {props.notices}
        <div
          ref={(element) => { pageStage = element; }}
          id="pageStage"
          class="page-stage"
          role={secondary() ? "tabpanel" : undefined}
          aria-labelledby={secondary() ? secondaryTabElementId(props.activePage(), secondary()!) : undefined}
        >
          <CurrentPageNameProvider name={() => tree.name(props.activePage())}>
            <PageBoundary name={uiText.shell.currentPage} resetKey={props.activePage()}>
              {props.children}
            </PageBoundary>
          </CurrentPageNameProvider>
        </div>
      </main>
    </>
  );
}

/** 二级分页条里一个一级页的那组分页：页签组（←/→、Home/End，自带指示条），选中项和选择都是这一页清单的。 */
function SecondaryTabs(props: { page: PageId; navigation: SecondaryNavigation }) {
  const tabsById = createMemo(() => new Map(props.navigation.tabs().map((tab) => [tab.id, tab])));
  const ids = createMemo(() => props.navigation.tabs().map((tab) => tab.id), undefined, {
    equals: (a, b) => a.length === b.length && a.every((id, index) => id === b[index])
  });
  return (
    <TabsRoot value={props.navigation.selected()} onChange={props.navigation.select} id={secondaryTabsId(props.page)} class="page-subnav-root">
      <TabsList class="page-subnav-tabs" ariaLabel={props.navigation.label()}>
        <For each={ids()}>
          {(id) => (
            <TabsTrigger value={id} id={tabsById().get(id)?.elementId} controls="pageStage" class="page-subnav-tab page-tab">
              {tabLabel(tabsById().get(id))}
            </TabsTrigger>
          )}
        </For>
      </TabsList>
    </TabsRoot>
  );
}

/** 页签上的文字：名字，有数量时后面跟数量。 */
function tabLabel(tab: SecondaryTab | undefined): string {
  if (!tab) return "";
  return tab.count === undefined ? tab.name : `${tab.name} ${tab.count}`;
}

function secondaryTabsId(page: PageId): string {
  return `pageSubnavTabs-${page}`;
}

/** 选中分页的元素 id：清单给了就用清单的，否则是页签组按自己的 id 起的（Tabs.tsx）。 */
function secondaryTabElementId(page: PageId, navigation: SecondaryNavigation): string {
  const selected = navigation.selected();
  return navigation.tabs().find((tab) => tab.id === selected)?.elementId ?? `${secondaryTabsId(page)}-tab-${selected}`;
}

/** 选中项在横向滚动的条里放不下时，把它滚回可见范围。 */
function reveal(container: HTMLElement | undefined, active: HTMLElement | null) {
  if (!container || !active) return;
  const containerRect = container.getBoundingClientRect();
  const activeRect = active.getBoundingClientRect();
  if (activeRect.left < containerRect.left) {
    container.scrollLeft -= containerRect.left - activeRect.left;
  } else if (activeRect.right > containerRect.right) {
    container.scrollLeft += activeRect.right - containerRect.right;
  }
}

function taskCenterTitle(state: TaskCenterOperationState): string {
  switch (state) {
    case "loading":
      return uiText.shell.taskCenterSyncing;
    case "disconnected":
      return uiText.shell.taskCenterDisconnected;
    case "error":
      return uiText.shell.taskCenterUnavailable;
    default:
      return uiText.shell.taskCenter;
  }
}
