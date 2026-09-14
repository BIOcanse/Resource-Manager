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

type TaskCenterTab = TaskCenterView | "diagnostics";

const taskCenterTabs: ReadonlyArray<{
  value: TaskCenterView;
  label: string;
}> = [
  { value: "active", label: "进行中" },
  { value: "history", label: "历史" },
  { value: "all", label: "全部" }
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
    ? [...taskCenterTabs, { value: "diagnostics" as const, label: "诊断" }]
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
        title="任务中心"
        titleId="taskCenterTitle"
        closeButtonRef={(element) => { closeButton = element; }}
        onDismiss={props.onClose}
      >
        <p id="taskCenterSummary" class="task-center-summary">
          {props.snapshot().activeUserCount > 0
            ? `${props.snapshot().activeUserCount} 项正在进行`
            : "当前没有进行中的任务"}
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
          <TabsList class="ui-tabs-list" ariaLabel="任务筛选">
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
                  emptyMessage="此筛选下没有任务"
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
                  <span>{item().source === "operation" ? "后端操作" : "界面任务"}</span>
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
                          aria-label={`${item().title}进度`}
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
                      {userFacingMessage(error(), "任务未完成")}
                    </p>
                  )}
                </Show>
                <Show when={!item().errorSummary && item().resultSummary}>
                  {(result) => (
                    <p class="task-center-result">
                      {userFacingMessage(result(), "任务已完成")}
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
                    ? "正在取消"
                    : item().actionBlockedReason ?? "取消任务"}
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
    "component.download": "下载组件",
    "component.install": "安装组件",
    "dependency.download": "下载依赖",
    "dependency.launch-installer": "安装依赖",
    "software.uninstall": "卸载软件",
    "migration.execute": "迁移软件数据",
    "migration.restore": "恢复软件数据",
    "discovery.start": "发现迁移数据",
    "resource-breakdown.layout-settle": "资源布局过渡"
  };
  return known[item.kind] ?? item.kind;
}

function taskCancelLabel(item: TaskCenterItem): string {
  const identity = item.domainKey?.trim() || shortTaskId(item.ownerId);
  return `取消 ${item.title}（${identity}）`;
}

function shortTaskId(value: string): string {
  return value.length <= 8 ? value : value.slice(-8);
}

function taskStatusLabel(status: string): string {
  const labels: Record<string, string> = {
    queued: "等待中",
    startPending: "正在启动",
    running: "进行中",
    cancelPending: "正在取消",
    retryWait: "等待重试",
    recoveryPending: "正在恢复",
    succeeded: "已完成",
    failed: "未完成",
    canceled: "已取消",
    cancelled: "已取消",
    superseded: "已替换",
    stateUncertain: "状态待确认"
  };
  return labels[status] ?? "状态未知";
}

function isErrorState(item: TaskCenterItem): boolean {
  return item.status === "failed" || item.stateUncertain;
}

function formatPercent(value: number | null): string {
  return value === null ? "" : `${Math.round(value)}%`;
}
