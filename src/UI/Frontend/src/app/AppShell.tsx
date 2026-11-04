import { useHorizontalReveal } from "../interactions/solid/horizontalReveal.ts";
import { HelpHint } from "../ui/primitives/HelpHint.tsx";
import { For, Show, createComputed, createEffect, createMemo, on } from "solid-js";
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
import { keptPages } from "./navigation/pageHost.tsx";

interface AppShellProps {
  activePage: Accessor<PageId>;
  pageTitle: Accessor<string>;
  systemUptime: Accessor<string>;
  /** Shared navigation tree for both tab bars. */
  navigation: NavigationTree;
  runtimeCapabilities: RuntimeCapabilitiesStore;
  taskCenterActiveCount: Accessor<number>;
  taskCenterOperationState: Accessor<TaskCenterOperationState>;
  /** Mounted views supply deep navigation targets; ordinary navigation focuses the heading. */
  pageFocusTarget?: Accessor<HTMLElement | undefined>;
  onPageTargetFocused?: (target: HTMLElement) => void;
  onPageSelect: (page: PageId) => void;
  onTaskCenterOpen: () => void;
  /** Notices above the page, outside page transitions. */
  notices: JSX.Element;
  /** Current page content. */
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
  /** Mount each page's subpage tabs once, then toggle visibility. */
  const pagesWithSecondary = tree.pages.filter((page) => page.secondary);
  const secondary = createMemo(() => tree.page(props.activePage()).secondary);
  const secondarySelected = () => secondary()?.selected() ?? null;
  const routeIdentity = () => `${props.activePage()}:${secondarySelected() ?? ""}`;
  const routeOrder = () => tree.order(props.activePage(), secondarySelected());
  const primaryOrder = () => tree.pages.findIndex((page) => page.id === props.activePage());

  createEffect(() => {
    document.title = uiText.shell.documentTitle(props.pageTitle(), uiText.shell.productName);
  });

  // Page changes reset scroll and focus the heading; subpage changes keep focus in the tab bar.
  // Deep navigation focuses the mounted target once; consumption or unmounting must not refocus.
  let viewportScrolled = false;
  let displayedPage = props.activePage();
  // Focus the persistent heading before replacing content to avoid synchronous table layout.
  // Update displayedPage afterward so transitions can choose whether to move or clone old content.
  createComputed(on(routeIdentity, () => {
    if (viewportScrolled) mainViewport?.scrollTo({ top: 0, left: 0, behavior: "auto" });
    if (props.activePage() !== displayedPage) pageHeading?.focus({ preventScroll: true });
  }, { defer: true }));
  createEffect(on([routeIdentity, () => props.pageFocusTarget?.()], ([route, target], previous) => {
    const routeChanged = route !== previous?.[0];
    if (!routeChanged && !target) return;
    displayedPage = props.activePage();
    // Deep targets require mounted page nodes; the persistent heading is immediately available.
    if (target) {
      if (target.isConnected) {
        target.focus({ preventScroll: true });
        props.onPageTargetFocused?.(target);
      }
    }
  }, { defer: true }));

  // Scroll positioning and indicators share the post-layout read/write boundary.
  useHorizontalReveal({
    container: () => pageNav,
    selected: () => pageNav?.querySelector<HTMLElement>(".shell-page.active") ?? null,
    change: routeIdentity
  });
  useHorizontalReveal({
    container: () => subnav,
    selected: () => subnavSlot?.querySelector<HTMLElement>(".page-subnav-group:not([hidden]) [role='tab'][aria-selected='true']") ?? null,
    change: routeIdentity
  });
  // The primary indicator follows the fixed page order.
  useSelectionIndicator({
    name: "一级页签",
    container: () => pageNav,
    indicator: () => primaryIndicator,
    selected: () => pageNav?.querySelector<HTMLElement>(".shell-page.active") ?? null,
    items: () => [...(pageNav?.querySelectorAll<HTMLElement>(".shell-page") ?? [])],
    order: primaryOrder
  });
  // Keep subpage tab groups mounted and toggle visibility without entry animation.
  // Each group retains its selected-tab indicator across page changes.
  // Capture outgoing content only from the old page's host.
  // Move unmounted pages into the exit layer; clone retained or in-place updated content.
  // Animate incoming content on the whole stage.
  useContentTransition({
    name: "页面内容",
    key: routeIdentity,
    order: routeOrder,
    stage: () => pageStage,
    leaving: () => pageStage?.querySelector<HTMLElement>(`:scope > .page-host[data-page-id="${displayedPage}"]`) ?? undefined,
    layer: () => pageLayer,
    snapshot: () => (props.activePage() !== displayedPage && !keptPages.has(displayedPage) ? "move" : "clone")
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
        {/* Select title text without dragging; drag only from the surrounding blank space. */}
        <div class="window-drag-region" data-window-drag onPointerDown={(event) => {
          if (event.button === 0 && !(event.target instanceof Element && event.target.closest(".shell-title-text"))) {
            event.preventDefault();
            postShellMessage("window.drag");
          }
        }}>
          <strong class="shell-title-text">{uiText.shell.productName}</strong>
          <span class="shell-uptime shell-title-text">
            <span class="shell-uptime-label">{uiText.shell.uptime}</span>
            <span id="captureTime">{props.systemUptime()}</span>
            <HelpHint>{uiText.shell.uptimeTitle}</HelpHint>
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
            aria-description={taskCenterStatus(props.taskCenterOperationState()) ?? undefined}
            data-name-tooltip=""
            data-full-content={taskCenterStatus(props.taskCenterOperationState()) ?? undefined}
            onClick={props.onTaskCenterOpen}
          >
            <ListTodo aria-hidden="true" size={18} />
            <Show when={props.taskCenterActiveCount() > 0}>
              <span class="shell-task-count count-badge" aria-hidden="true">
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
        onScroll={(event) => { viewportScrolled = Boolean(event.currentTarget.scrollTop || event.currentTarget.scrollLeft); }}
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
          presentation="announce-loading"
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

/** Subpage tabs use their page list's selection, keyboard navigation, and indicator. */
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

/** Tab name followed by its optional count. */
function tabLabel(tab: SecondaryTab | undefined): string {
  if (!tab) return "";
  return tab.count === undefined ? tab.name : `${tab.name} ${tab.count}`;
}

function secondaryTabsId(page: PageId): string {
  return `pageSubnavTabs-${page}`;
}

/** Use the declared tab element ID, or the tab group's generated ID. */
function secondaryTabElementId(page: PageId, navigation: SecondaryNavigation): string {
  const selected = navigation.selected();
  return navigation.tabs().find((tab) => tab.id === selected)?.elementId ?? `${secondaryTabsId(page)}-tab-${selected}`;
}

/** Task-list status for the button tooltip and accessible description; null when ready. */
function taskCenterStatus(state: TaskCenterOperationState): string | null {
  switch (state) {
    case "loading":
      return uiText.shell.taskCenterSyncing;
    case "disconnected":
      return uiText.shell.taskCenterDisconnected;
    case "error":
      return uiText.shell.taskCenterUnavailable;
    default:
      return null;
  }
}
