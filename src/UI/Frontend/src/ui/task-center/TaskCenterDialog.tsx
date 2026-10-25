import { renderBackendMessage } from "../../presentation/backendMessage.ts";
import {
  For,
  Match,
  Show,
  Switch,
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
import { userFacingDateTime } from "../../presentation/userFacingText.ts";
import {
  operationTitleForKind,
  operationTitle,
  type TaskCenterItem,
  type TaskCenterSnapshot,
  type TaskCenterProjection
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
  const visibleTabs = createMemo(() => {
    const tabs: ReadonlyArray<{ value: TaskCenterView; label: string }> = [
      { value: "active", label: uiText.taskCenter.tabActive },
      { value: "history", label: uiText.taskCenter.tabHistory },
      { value: "all", label: uiText.taskCenter.tabAll }
    ];
    return props.diagnosticsEnabled
      ? [...tabs, { value: "diagnostics" as const, label: uiText.taskCenter.tabDiagnostics }]
      : tabs;
  });

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
            <span>{uiText.taskCenter.backendSyncing}</span>
          </div>
        </Show>
        <Show when={props.snapshot().operationState === "disconnected"}>
          <div class="task-center-session-warning" role="status">
            <TriangleAlert aria-hidden="true" size={17} />
            <span>{uiText.taskCenter.backendResyncing}</span>
          </div>
        </Show>
        <Show when={props.snapshot().operationState === "error"}>
          <div class="task-center-session-warning" role="alert">
            <TriangleAlert aria-hidden="true" size={17} />
            <span>{uiText.taskCenter.backendUnavailable}</span>
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
                <TabsTrigger class="ui-tabs-trigger page-tab" value={tab.value}>
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
      <DialogActions>
        <button class="secondary" type="button" onClick={props.onClose}>{uiText.taskCenter.close}</button>
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
                  <strong>{taskTitle(item())}</strong>
                  <span class="task-center-state" classList={{ error: isErrorState(item()) }}>
                    {taskStatusLabel(item().status)}
                  </span>
                </div>
                <div class="task-center-meta">
                  <span>{item().source === "operation" ? uiText.taskCenter.sourceOperation : uiText.taskCenter.sourceFrontend}</span>
                  <Show when={item().source !== "operation"}>
                    <span>{taskKindLabel(item())}</span>
                  </Show>
                  <span>{userFacingDateTime(item().updatedAt)}</span>
                </div>
                <Show when={item().progress}>
                  {(progress) => (
                    <Show when={progress().percent !== null}>
                      <div class="task-center-progress">
                        <progress
                          max="100"
                          value={progress().percent ?? 0}
                          aria-label={uiText.taskCenter.progressLabel(taskTitle(item()))}
                        />
                        <span>{formatPercent(progress().percent)}</span>
                      </div>
                    </Show>
                  )}
                </Show>
                <Show when={item().domainKey}>
                  <p class="task-center-target">{item().domainKey}</p>
                </Show>
                <Show when={item().stateUncertain}>
                  <p class="task-center-inline-warning">{renderBackendMessage(item().errorMessage, uiText.taskCenter.needsResync)}</p>
                </Show>
                <Show when={item().status === "failed"}>
                  <p class="task-center-error" role="alert">{renderBackendMessage(item().errorMessage, uiText.taskCenter.taskFailed)}</p>
                </Show>
                <Show when={item().status === "succeeded" || item().status === "completed"}>
                  <p class="task-center-result">{renderBackendMessage(item().resultMessage, uiText.taskCenter.taskCompleted)}</p>
                </Show>
                <details class="task-center-details">
                  <summary>{uiText.taskCenter.detailsSummary}</summary>
                  <dl>
                    <div><dt>{uiText.taskCenter.kind}</dt><dd>{taskKindLabel(item())}</dd></div>
                    <Show when={item().domainKey}>
                      {(domainKey) => <div><dt>{uiText.taskCenter.target}</dt><dd>{domainKey()}</dd></div>}
                    </Show>
                    <div><dt>{uiText.taskCenter.createdAt}</dt><dd>{userFacingDateTime(item().createdAt)}</dd></div>
                    <div><dt>{uiText.taskCenter.updatedAt}</dt><dd>{userFacingDateTime(item().updatedAt)}</dd></div>
                    <Show when={item().resultCode}>
                      <div><dt>{uiText.managementFeedback.resultCode}</dt><dd>{item().resultCode}</dd></div>
                    </Show>
                    <Show when={item().progressStage === "Download"}>
                      <div><dt>{uiText.taskCenter.progressLabel(taskTitle(item()))}</dt><dd>{uiText.managementFeedback.stageDownload}</dd></div>
                    </Show>
                    <div><dt>{uiText.taskCenter.identity}</dt><dd>{item().ownerId}</dd></div>
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

// 同一任务 ID 的行会保留下来，状态变化时图标必须跟着变，所以按状态响应式切换，而不是创建时选一次。
function TaskStatusIcon(props: { readonly item: TaskCenterItem }) {
  const glyph = () => {
    if (props.item.stateUncertain) return "uncertain";
    if (props.item.active) {
      return props.item.status === "queued" || props.item.status === "retryWait" ? "waiting" : "running";
    }
    if (props.item.status === "succeeded") return "succeeded";
    if (props.item.status === "failed") return "failed";
    return "other";
  };
  return (
    <Switch fallback={<CircleEllipsis size={19} />}>
      <Match when={glyph() === "uncertain"}><TriangleAlert size={19} /></Match>
      <Match when={glyph() === "waiting"}><Clock3 size={19} /></Match>
      <Match when={glyph() === "running"}><LoaderCircle class="task-center-spinner" size={19} /></Match>
      <Match when={glyph() === "succeeded"}><CircleCheck size={19} /></Match>
      <Match when={glyph() === "failed"}><CircleX size={19} /></Match>
    </Switch>
  );
}

function taskKindLabel(item: TaskCenterItem): string {
  if (item.source === "operation") {
    return operationTitleForKind(item.kind);
  }
  const known: Record<string, string> = {
    "resource-breakdown.layout-settle": uiText.taskCenter.operation.resourceBreakdownLayoutSettle
  };
  return known[item.kind] ?? item.kind;
}

function taskTitle(item: TaskCenterItem): string {
  return item.source === "operation"
    ? operationTitle({ kind: item.kind, subject: item.subject ?? null })
    : item.title;
}

function taskCancelLabel(item: TaskCenterItem): string {
  const identity = item.source === "operation"
    ? shortTaskId(item.ownerId)
    : item.domainKey?.trim() || shortTaskId(item.ownerId);
  return uiText.taskCenter.cancelWithTitle(taskTitle(item), identity);
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
