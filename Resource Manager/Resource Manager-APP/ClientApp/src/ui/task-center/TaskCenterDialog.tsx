import {
  For,
  Show,
  createEffect,
  createMemo,
  createSignal,
  type Accessor
} from "solid-js";
import {
  Ban,
  CircleCheck,
  CircleEllipsis,
  CircleX,
  Clock3,
  LoaderCircle,
  TriangleAlert
} from "lucide-solid";
import { userFacingDateTime, userFacingMessage } from "../../presentation/userFacingText.ts";
import type {
  TaskCenterItem,
  TaskCenterSnapshot,
  TaskCenterProjection
} from "../../frontendRuntime/taskCenter/TaskCenterProjection.ts";
import {
  selectTaskCenterItems,
  type TaskCenterView
} from "../../frontendRuntime/taskCenter/taskCenterSelectors.ts";
import {
  DialogActions,
  DialogBody,
  DialogHeader,
  DialogRoot
} from "../primitives/Dialog.tsx";
import {
  TabsList,
  TabsPanel,
  TabsRoot,
  TabsTrigger
} from "../primitives/Tabs.tsx";
import { uiText } from "../../text.ts";

type TaskCenterTab = TaskCenterView | "diagnostics";

const taskCenterTabs: ReadonlyArray<{
  value: TaskCenterView;
  label: string;
}> = [
  { value: "active", label: uiText.taskCenter.tabActive },
  { value: "history", label: uiText.taskCenter.tabHistory },
  { value: "all", label: uiText.taskCenter.tabAll }
];

export function TaskCenterDialog(props: {
  readonly open: boolean;
  readonly snapshot: Accessor<TaskCenterSnapshot>;
  readonly projection: TaskCenterProjection;
  readonly diagnosticsEnabled: boolean;
  readonly onClose: () => void;
  readonly onCancelError: (error: unknown) => void;
}) {
  const [activeTab, setActiveTab] = createSignal<TaskCenterTab>("active");
  const [cancelingIds, setCancelingIds] = createSignal<ReadonlySet<string>>(new Set());
  let closeButton: HTMLButtonElement | undefined;
  const visibleTabs = createMemo(() => props.diagnosticsEnabled
    ? [...taskCenterTabs, { value: "diagnostics" as const, label: uiText.taskCenter.tabDiagnostics }]
    : taskCenterTabs);

  createEffect(() => {
    if (!props.diagnosticsEnabled && activeTab() === "diagnostics") {
      setActiveTab("active");
    }
  });

  const itemsFor = (tab: TaskCenterTab) => tab === "diagnostics"
    ? selectTaskCenterItems(props.snapshot(), { visibility: "diagnostics" })
    : selectTaskCenterItems(props.snapshot(), { view: tab });

  async function cancel(item: TaskCenterItem) {
    if (!item.cancelable || cancelingIds().has(item.id)) {
      return;
    }
    setCancelingIds((current) => new Set([...current, item.id]));
    try {
      await props.projection.cancel(item.id);
    } catch (error) {
      props.onCancelError(error);
    } finally {
      setCancelingIds((current) => {
        const next = new Set(current);
        next.delete(item.id);
        return next;
      });
    }
  }

  return (
    <DialogRoot
      open={props.open}
      labelledBy="taskCenterTitle"
      describedBy="taskCenterSummary"
      backdropClass="task-center-backdrop"
      class="task-center-modal"
      initialFocus={() => closeButton}
      onDismiss={props.onClose}
    >
      <DialogHeader
        title={uiText.taskCenter.title}
        titleId="taskCenterTitle"
        closeButtonRef={(element) => { closeButton = element; }}
        onDismiss={props.onClose}
      >
        <p id="taskCenterSummary" class="task-center-summary">
          {props.snapshot().activeUserCount > 0
            ? uiText.taskCenter.activeCount(props.snapshot().activeUserCount)
            : uiText.taskCenter.noActiveTasks}
        </p>
      </DialogHeader>
      <DialogBody class="task-center-body">
        <Show when={props.snapshot().operationState === "loading"}>
          <div class="task-center-session-warning" role="status">
            <LoaderCircle class="task-center-spinner" aria-hidden="true" size={17} />
            <span>后端任务正在同步。</span>
          </div>
        </Show>
        <Show when={props.snapshot().operationState === "disconnected"}>
          <div class="task-center-session-warning" role="status">
            <TriangleAlert aria-hidden="true" size={17} />
            <span>本机服务正在重新同步，后端任务暂时不能操作。</span>
          </div>
        </Show>
        <Show when={props.snapshot().operationState === "error"}>
          <div class="task-center-session-warning" role="alert">
            <TriangleAlert aria-hidden="true" size={17} />
            <span>无法读取后端任务状态；界面任务仍可正常管理。</span>
          </div>
        </Show>
        <TabsRoot
          id="task-center"
          class="task-center-tabs"
          value={activeTab()}
          onChange={(value) => setActiveTab(value as TaskCenterTab)}
        >
          <TabsList class="ui-tabs-list" ariaLabel={uiText.taskCenter.filterLabel}>
            <For each={visibleTabs()}>
              {(tab) => (
                <TabsTrigger class="ui-tabs-trigger" value={tab.value}>
                  <span>{tab.label}</span>
                  <span class="task-center-tab-count" aria-hidden="true">
                    {itemsFor(tab.value).length}
                  </span>
                </TabsTrigger>
              )}
            </For>
          </TabsList>
          <For each={visibleTabs()}>
            {(tab) => (
              <TabsPanel class="ui-tabs-panel task-center-panel" value={tab.value}>
                <TaskCenterList
                  items={itemsFor(tab.value)}
                  cancelingIds={cancelingIds()}
                  emptyMessage={uiText.taskCenter.noTasksInFilter}
                  onCancel={(item) => void cancel(item)}
                />
              </TabsPanel>
            )}
          </For>
        </TabsRoot>
      </DialogBody>
      <DialogActions class="task-center-actions">
        <span>{itemsFor(activeTab()).length} 项</span>
        <button class="secondary" type="button" onClick={props.onClose}>关闭</button>
      </DialogActions>
    </DialogRoot>
  );
}

function TaskCenterList(props: {
  readonly items: readonly TaskCenterItem[];
  readonly cancelingIds: ReadonlySet<string>;
  readonly emptyMessage: string;
  readonly onCancel: (item: TaskCenterItem) => void;
}) {
  return (
    <Show
      when={props.items.length > 0}
      fallback={<div class="task-center-empty">{props.emptyMessage}</div>}
    >
      <div class="task-center-list" role="list">
        <For each={props.items.map((item) => item.id)}>
          {(itemId) => {
            const item = () => props.items.find((candidate) => candidate.id === itemId)!;
            return (
            <article
              data-task-center-id={itemId}
              class="task-center-item"
              classList={{ active: item().active }}
              role="listitem"
            >
              <div class="task-center-item-leading" aria-hidden="true">
                <TaskStatusIcon item={item()} />
              </div>
              <div class="task-center-item-content">
                <div class="task-center-item-title-row">
                  <strong>{item().title}</strong>
                  <span class="task-center-state" classList={{ error: isErrorState(item()) }}>
                    {taskStatusLabel(item().status)}
                  </span>
                </div>
                <div class="task-center-meta">
                  <span>{item().source === "operation" ? uiText.taskCenter.sourceOperation : uiText.taskCenter.sourceFrontend}</span>
                  <span>{taskKindLabel(item())}</span>
                  <span>{userFacingDateTime(item().updatedAt)}</span>
                </div>
                <Show when={item().progress}>
                  {(progress) => (
                    <div class="task-center-progress">
                      <Show when={progress().percent !== null}>
                        <progress
                          max="100"
                          value={progress().percent ?? 0}
                          aria-label={uiText.taskCenter.progressLabel(item().title)}
                        />
                        <span>{formatPercent(progress().percent)}</span>
                      </Show>
                      <Show when={progress().stage || progress().message}>
                        <p>{[progress().stage, progress().message].filter(Boolean).join(" · ")}</p>
                      </Show>
                    </div>
                  )}
                </Show>
                <Show when={item().stateUncertain}>
                  <p class="task-center-inline-warning">任务状态需要与本机服务重新确认。</p>
                </Show>
                <Show when={item().errorSummary}>
                  {(error) => (
                    <p class="task-center-error" role="alert">
                      {userFacingMessage(error(), uiText.taskCenter.taskFailed)}
                    </p>
                  )}
                </Show>
                <Show when={!item().errorSummary && item().resultSummary}>
                  {(result) => (
                    <p class="task-center-result">
                      {userFacingMessage(result(), uiText.taskCenter.taskCompleted)}
                    </p>
                  )}
                </Show>
                <details class="task-center-details">
                  <summary>详情</summary>
                  <dl>
                    <div><dt>类型</dt><dd>{item().kind}</dd></div>
                    <Show when={item().domainKey}>
                      {(domainKey) => <div><dt>目标</dt><dd>{domainKey()}</dd></div>}
                    </Show>
                    <div><dt>创建时间</dt><dd>{userFacingDateTime(item().createdAt)}</dd></div>
                    <div><dt>更新时间</dt><dd>{userFacingDateTime(item().updatedAt)}</dd></div>
                    <div><dt>标识</dt><dd>{item().ownerId}</dd></div>
                  </dl>
                </details>
              </div>
              <div class="task-center-item-actions">
                <button
                  class="icon-button secondary"
                  type="button"
                  disabled={!item().cancelable || props.cancelingIds.has(itemId)}
                  aria-label={taskCancelLabel(item())}
                  title={props.cancelingIds.has(itemId)
                    ? uiText.taskCenter.canceling
                    : item().actionBlockedReason ?? uiText.taskCenter.cancelTask}
                  onClick={() => props.onCancel(item())}
                >
                  <Ban aria-hidden="true" size={17} />
                </button>
              </div>
            </article>
            );
          }}
        </For>
      </div>
    </Show>
  );
}

function TaskStatusIcon(props: { readonly item: TaskCenterItem }) {
  if (props.item.stateUncertain) {
    return <TriangleAlert size={19} />;
  }
  if (props.item.active) {
    return props.item.status === "queued" || props.item.status === "retryWait"
      ? <Clock3 size={19} />
      : <LoaderCircle class="task-center-spinner" size={19} />;
  }
  if (props.item.status === "succeeded") {
    return <CircleCheck size={19} />;
  }
  if (props.item.status === "failed") {
    return <CircleX size={19} />;
  }
  return <CircleEllipsis size={19} />;
}

function taskKindLabel(item: TaskCenterItem): string {
  const known: Record<string, string> = {
    "component.download": uiText.taskCenter.operation.componentDownload,
    "component.install": uiText.taskCenter.operation.componentInstall,
    "dependency.download": uiText.taskCenter.operation.dependencyDownload,
    "dependency.launch-installer": uiText.taskCenter.operation.dependencyLaunchInstaller,
    "software.uninstall": uiText.taskCenter.operation.softwareUninstall,
    "migration.execute": uiText.taskCenter.operation.migrationExecute,
    "migration.restore": uiText.taskCenter.operation.migrationRestore,
    "discovery.start": uiText.taskCenter.operation.discoveryStart,
    "resource-breakdown.layout-settle": uiText.taskCenter.operation.resourceBreakdownLayoutSettle
  };
  return known[item.kind] ?? item.kind;
}

function taskCancelLabel(item: TaskCenterItem): string {
  const identity = item.domainKey?.trim() || shortTaskId(item.ownerId);
  return uiText.taskCenter.cancelWithTitle(item.title, identity);
}

function shortTaskId(value: string): string {
  return value.length <= 8 ? value : value.slice(-8);
}

function taskStatusLabel(status: string): string {
  const labels: Record<string, string> = {
    queued: uiText.taskCenter.status.queued,
    startPending: uiText.taskCenter.status.startPending,
    running: uiText.taskCenter.tabActive,
    cancelPending: uiText.taskCenter.canceling,
    retryWait: uiText.taskCenter.status.retryWait,
    recoveryPending: uiText.taskCenter.status.recoveryPending,
    succeeded: uiText.taskCenter.status.succeeded,
    failed: uiText.taskCenter.status.failed,
    canceled: uiText.taskCenter.status.canceled,
    cancelled: uiText.taskCenter.status.canceled,
    superseded: uiText.taskCenter.status.superseded,
    stateUncertain: uiText.taskCenter.status.stateUncertain
  };
  return labels[status] ?? uiText.taskCenter.status.unknown;
}

function isErrorState(item: TaskCenterItem): boolean {
  return item.status === "failed" || item.stateUncertain;
}

function formatPercent(value: number | null): string {
  return value === null ? "" : `${Math.round(value)}%`;
}
