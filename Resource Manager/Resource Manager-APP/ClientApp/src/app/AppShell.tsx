import { Show, createEffect, on } from "solid-js";
import type { Accessor, JSX } from "solid-js";
import { ListTodo } from "lucide-solid";
import { ManagementSubpageBar } from "../components/ManagementPage";
import { ObservationStateNotice } from "../components/ObservationStateNotice";
import type { ManagementSubpageId } from "../management/managementNavigation";
import type { ManagementStore } from "../stores/managementStore";
import type { RuntimeCapabilitiesStore } from "../stores/runtimeCapabilitiesStore";
import type { PageId } from "../types";
import { postShellMessage } from "../utils";
import { PageBoundary } from "../ui/patterns/PageBoundary.tsx";
import type {
  TaskCenterOperationState
} from "../frontendRuntime/taskCenter/TaskCenterProjection.ts";
import { uiText } from "../text.ts";

interface AppShellProps {
  activePage: Accessor<PageId>;
  pageTitle: Accessor<string>;
  systemUptime: Accessor<string>;
  management: ManagementStore;
  runtimeCapabilities: RuntimeCapabilitiesStore;
  taskCenterActiveCount: Accessor<number>;
  taskCenterOperationState: Accessor<TaskCenterOperationState>;
  onPageSelect: (page: PageId) => void;
  onManagementSubpageSelect: (subpage: ManagementSubpageId) => void;
  onTaskCenterOpen: () => void;
  children: JSX.Element;
}

export function AppShell(props: AppShellProps) {
  let mainViewport: HTMLElement | undefined;
  let pageHeading: HTMLHeadingElement | undefined;
  const routeIdentity = () => props.activePage() === "components"
    ? `components:${props.management.activeSubpage()}`
    : props.activePage();

  createEffect(() => {
    document.title = uiText.shell.documentTitle(props.pageTitle(), uiText.shell.productName);
  });

  createEffect(on(routeIdentity, () => {
    queueMicrotask(() => {
      mainViewport?.scrollTo({ top: 0, left: 0, behavior: "auto" });
      pageHeading?.focus({ preventScroll: true });
    });
  }, { defer: true }));

  return (
    <>
      <header class="window-shellbar">
        <nav class="shell-pages" aria-label={uiText.shell.pageNav}>
          <button class="shell-page" classList={{ active: props.activePage() === "monitor" }} type="button" aria-current={props.activePage() === "monitor" ? "page" : undefined} onClick={() => props.onPageSelect("monitor")}>{uiText.page.monitor}</button>
          <button class="shell-page" classList={{ active: props.activePage() === "components" }} type="button" aria-current={props.activePage() === "components" ? "page" : undefined} onClick={() => props.onPageSelect("components")}>{uiText.page.components}</button>
          <Show when={props.runtimeCapabilities.optimizationEnabled()}>
            <button class="shell-page" classList={{ active: props.activePage() === "optimization" }} type="button" aria-current={props.activePage() === "optimization" ? "page" : undefined} onClick={() => props.onPageSelect("optimization")}>{uiText.page.optimization}</button>
          </Show>
          <button class="shell-page" classList={{ active: props.activePage() === "details" }} type="button" aria-current={props.activePage() === "details" ? "page" : undefined} onClick={() => props.onPageSelect("details")}>{uiText.page.details}</button>
          <button class="shell-page" classList={{ active: props.activePage() === "settings" }} type="button" aria-current={props.activePage() === "settings" ? "page" : undefined} onClick={() => props.onPageSelect("settings")}>{uiText.page.settings}</button>
        </nav>
        <div class="window-drag-region" data-window-drag onPointerDown={(event) => {
          if (event.button === 0) {
            event.preventDefault();
            postShellMessage("window.drag");
          }
        }}>
          <strong>{uiText.shell.productName}</strong>
          <span id="shellPageName">{props.pageTitle()}</span>
          <span id="captureTime">{props.systemUptime()}</span>
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

      <Show when={props.activePage() === "components"}>
        <ManagementSubpageBar
          activeSubpage={props.management.activeSubpage()}
          components={props.management.components()}
          software={props.management.software()}
          mutablePersistenceEnabled={props.runtimeCapabilities.mutablePersistenceEnabled()}
          runtimeEffectsEnabled={props.runtimeCapabilities.runtimeEffectsEnabled()}
          onSubpageChange={props.onManagementSubpageSelect}
        />
      </Show>

      <main
        ref={(element) => { mainViewport = element; }}
        class="app-shell"
        aria-labelledby="activePageTitle"
        classList={{
          "components-shell": props.activePage() === "components"
        }}
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
        <PageBoundary name={uiText.shell.currentPage} resetKey={props.activePage()}>
          {props.children}
        </PageBoundary>
      </main>
    </>
  );
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

