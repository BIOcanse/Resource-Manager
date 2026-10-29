import { usePushSource } from "../../frontendRuntime/push/usePushSource.ts";
import { formatInterfaceNumber } from "../../presentation/interfaceNumbers.ts";
import { useFrontendReadIntervals } from "../../app/FrontendReadIntervalsContext.tsx";
import { createEffect, createMemo, createSignal, For, on, onCleanup, Show } from "solid-js";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds";
import { frontendVisibilitySurface } from "../../frontendWork/frontendVisibilitySurface";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext";
import { useFrontendVisibilityDemand } from "../../frontendWork/useFrontendVisibilityDemand";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import { sourceCanRender } from "../../frontendRuntime/source/SourceSnapshot";
import { userFacingDateTime } from "../../presentation/userFacingText";
import { compactUserDetailSections, userDetailItem, userDetailSection } from "../../presentation/userDetails";
import { smartPlacementTarget } from "../../presentation/smartScheduling.ts";
import { softwareRecordName } from "../../presentation/softwareFacts.ts";
import type {
  SoftwareRecord, HostManagerAppliedRecord, HostManagerRollbackStateDocument,
  SmartRecordHistoryEntry, SmartRecordRestoreResult
} from "../../types";
import { getSmartRecordHistory, smartRecordHistoryMaximumLimit } from "../../data/optimization/smartRecordHistory.ts";
import { UserDetailsDialog } from "../../components/UserDetailsDialog";
import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { uiText } from "../../text.ts";
import { CurrentPageHeading, useCurrentPageName } from "../../app/navigation/currentPageName.tsx";
import { useReportHistoryClear, type ReportHistoryClearFeedback } from "../reportHistory/useReportHistoryClear.ts";


/** 默认列出的最近记录条数。 */
const recentRecordCount = 8;
/** 历史每次多读的条数。 */
const historyPageSize = 50;

interface DetailRecord {
  target: string;
  kind: string;
  resourceKind: string;
  updatedAt: string;
  sequence: number | null;
  recordedAt: string | null;
}

type HistoryReadStatus = "idle" | "loading" | "ready" | "failed";

/*
 * 详细信息 → 报告：智能调度记录（docs/report_lifecycle_2026-10-28.md“智能调度记录”）。
 * 上面是回滚账本里待恢复的记录（推送），下面是恢复后保留下来的历史（按序号分页读取，从新到旧）。
 * 历史在分页可见时读第一页；账本里带序号的记录离开账本（转成历史）或清空历史之后，从最新重读，条数保持已显示的条数。
 */
export function HostManagerSmartCoordinatorDetailsReport(props: ReportHistoryClearFeedback & {
  software: readonly SoftwareRecord[];
  /** 用户设置 performance.keepSmartRecordHistory（已保存的值）。 */
  keepHistory: boolean;
}) {
  const demandId = "details.smart-report";
  useFrontendVisibilityDemand(demandId, [frontendWorkIds.detailsSmartReport]);
  const frontendWork = useFrontendWork();
  const frontendRuntime = useFrontendRuntime();
  const readIntervals = useFrontendReadIntervals();
  const visible = () => frontendWork.isNeeded(frontendWorkIds.detailsSmartReport);
  const [selectedRecord, setSelectedRecord] = createSignal<DetailRecord | null>(null);
  const [selectedHistory, setSelectedHistory] = createSignal<SmartRecordHistoryEntry | null>(null);
  const stateSource = usePushSource(frontendRuntime.sources.smartCoordinatorState, () => ({
    active: visible(),
    intervalMs: readIntervals.current()?.monitor
  })).snapshot;
  const state = createMemo(() => {
    const source = stateSource();
    return source && sourceCanRender(source) ? source.data ?? undefined : undefined;
  });
  const stateMessage = createMemo(() => {
    const status = stateSource()?.status;
    return status === "error" || status === "unavailable"
      ? uiText.smartReport.readFailed
      : status === "idle" || status === "loading" || status === "refreshing" || status === undefined
        ? uiText.smartReport.loading
        : uiText.smartReport.empty;
  });
  const records = createMemo(() => collectDetailRecords(state(), props.software));
  // 默认列出最近 recentRecordCount 条；多于这个数时列表下方有一个按钮切换成全部列出，再按回到最近几条。
  const [showAllRecords, setShowAllRecords] = createSignal(false);
  const visibleRecords = createMemo(() => showAllRecords() ? records() : records().slice(0, recentRecordCount));

  // 历史：已读到的条目（序号从新到旧）、下一页从哪个序号之前读、读取状态。
  const [history, setHistory] = createSignal<SmartRecordHistoryEntry[]>([]);
  const [nextBeforeSequence, setNextBeforeSequence] = createSignal<number | null>(null);
  const [historyStatus, setHistoryStatus] = createSignal<HistoryReadStatus>("idle");
  let historyRead: AbortController | null = null;
  onCleanup(() => historyRead?.abort());

  const readHistory = async (mode: "reload" | "more") => {
    historyRead?.abort();
    const controller = new AbortController();
    historyRead = controller;
    const query = mode === "reload"
      ? { beforeSequence: null, limit: Math.min(smartRecordHistoryMaximumLimit, Math.max(historyPageSize, history().length)) }
      : { beforeSequence: nextBeforeSequence(), limit: historyPageSize };
    setHistoryStatus("loading");
    try {
      const page = await getSmartRecordHistory(frontendRuntime.requestClient, query, controller.signal);
      if (historyRead !== controller) return;
      setHistory(mode === "reload" ? page.records : [...history(), ...page.records]);
      setNextBeforeSequence(page.nextBeforeSequence);
      setHistoryStatus("ready");
    } catch {
      if (historyRead !== controller) return;
      setHistoryStatus(controller.signal.aborted ? (history().length > 0 ? "ready" : "idle") : "failed");
    } finally {
      if (historyRead === controller) historyRead = null;
    }
  };

  // 分页可见时读；离开时取消进行中的读取，已读到的保留。
  createEffect(on(visible, (shown) => {
    if (shown) void readHistory("reload");
    else historyRead?.abort();
  }));
  // 账本里带序号的记录离开账本，就是转成了历史：从最新重读。
  const activeSequences = createMemo(() => new Set(records().flatMap((record) => record.sequence === null ? [] : [record.sequence])));
  createEffect(on(activeSequences, (current, previous) => {
    if (!previous || !visible()) return;
    for (const sequence of previous) {
      if (!current.has(sequence)) {
        void readHistory("reload");
        return;
      }
    }
  }, { defer: true }));

  const historyClear = useReportHistoryClear({
    ...props,
    kind: "smartRecords",
    historyCount: () => history().length,
    afterClear: () => readHistory("reload")
  });

  const historyCopy = () => uiText.smartReport.history;
  const pageName = useCurrentPageName();
  return (
    <section
      {...frontendVisibilitySurface("visible.details.smart-report.surface", [demandId])}
      class="smart-details-report"
      aria-label={pageName()}
    >
      <CurrentPageHeading />
      <Show
        when={state()}
        fallback={<div class="optimization-empty">{stateMessage()}</div>}
      >
        {(current) => (
          <>
          <div class="smart-details-summary">
            <SummaryTile label={uiText.smartReport.completed} value={formatNumber(current().appliedPlacements.length)} />
          </div>
          <Show
            when={records().length > 0}
            fallback={<div class="optimization-empty">{uiText.smartReport.empty}</div>}
          >
            <div class="smart-details-records">
              <For each={visibleRecords()}>
                {(record) => (
                  <article class="smart-details-record">
                    <div class="smart-details-record-main">
                      <strong>{record.target}</strong>
                      <span>{formatRecordKind(record.kind)} · {formatResourceKind(record.resourceKind)}</span>
                    </div>
                    <div class="smart-details-record-metrics">
                      <Show when={record.sequence}>
                        {(sequence) => <span class="smart-details-sequence">#{formatNumber(sequence())}</span>}
                      </Show>
                      <button class="secondary details-button" type="button" onClick={() => setSelectedRecord(record)}>
                        {uiText.smartReport.details}
                      </button>
                    </div>
                  </article>
                )}
              </For>
            </div>
            <Show when={records().length > recentRecordCount}>
              <button
                class="secondary smart-details-records-toggle"
                type="button"
                aria-expanded={showAllRecords()}
                onClick={() => setShowAllRecords(!showAllRecords())}
              >
                {showAllRecords()
                  ? uiText.smartReport.showRecentRecords(recentRecordCount)
                  : uiText.smartReport.showAllRecords(records().length)}
              </button>
            </Show>
          </Show>
          </>
        )}
      </Show>
      <section class="smart-details-history" aria-label={historyCopy().title}>
        <div class="smart-details-history-header">
          <h3>{historyCopy().title}</h3>
          <button class="secondary panel-refresh-button" type="button"
            disabled={history().length === 0 || historyClear.clearing() || historyStatus() === "loading"}
            onClick={() => void historyClear.clear()}>
            <StableLabel labels={{ idle: uiText.reportHistory.clear, busy: uiText.reportHistory.clearing }}
              current={historyClear.clearing() ? "busy" : "idle"} />
          </button>
        </div>
        <Show when={!props.keepHistory}>
          <p class="smart-details-note">{historyCopy().retentionOff}</p>
        </Show>
        <Show when={historyStatus() === "failed"}>
          <div class="smart-details-note error" role="alert">
            <strong>{historyCopy().readFailed}</strong>
            <button class="secondary" type="button" onClick={() => void readHistory("reload")}>
              {uiText.observationNotice.retry}
            </button>
          </div>
        </Show>
        <Show when={history().length > 0} fallback={
          <Show when={historyStatus() !== "failed"}>
            <div class="optimization-empty">
              {historyStatus() === "ready" ? historyCopy().empty : historyCopy().loading}
            </div>
          </Show>
        }>
          <div class="smart-details-records" role="list">
            <For each={history()}>
              {(entry) => (
                <article class="smart-details-record history" role="listitem" data-smart-record-sequence={entry.sequence}>
                  <div class="smart-details-record-main">
                    <strong>{historyTarget(entry, props.software)}</strong>
                    <span>{formatRecordKind(entry.kind)} · {formatResourceKind(entry.resourceKind)} · {userFacingDateTime(entry.restoredAt)}</span>
                  </div>
                  <div class="smart-details-record-metrics">
                    <span class="smart-details-sequence">#{formatNumber(entry.sequence)}</span>
                    <span classList={{ danger: entry.restoreResult === "failed" }}>{restoreResultText(entry.restoreResult)}</span>
                    <button class="secondary details-button" type="button" onClick={() => setSelectedHistory(entry)}>
                      {uiText.smartReport.details}
                    </button>
                  </div>
                </article>
              )}
            </For>
          </div>
          <Show when={nextBeforeSequence() !== null && historyStatus() !== "failed"}>
            <button class="secondary smart-details-history-more" type="button"
              disabled={historyStatus() === "loading"} onClick={() => void readHistory("more")}>
              {historyCopy().loadMore}
            </button>
          </Show>
        </Show>
      </section>
      <UserDetailsDialog
        open={Boolean(selectedRecord())}
        title={uiText.smartReport.detailTitle(selectedRecord()?.target ?? uiText.smartReport.fallbackTarget)}
        sections={selectedRecord() ? detailRecordSections(selectedRecord()!) : []}
        onClose={() => setSelectedRecord(null)}
      />
      <UserDetailsDialog
        open={Boolean(selectedHistory())}
        title={uiText.smartReport.detailTitle(selectedHistory() ? historyTarget(selectedHistory()!, props.software) : uiText.smartReport.fallbackTarget)}
        sections={selectedHistory() ? historyRecordSections(selectedHistory()!, props.software) : []}
        onClose={() => setSelectedHistory(null)}
      />
    </section>
  );

}

function SummaryTile(props: { label: string; value: string }) {
  return (
    <div class="smart-details-tile">
      <span>{props.label}</span>
      <strong>{props.value}</strong>
    </div>
  );
}

// 带序号的记录按序号从新到旧排在前面；没有序号的（保留开关关着时进来的）按更新时间从新到旧排在后面。
function collectDetailRecords(state: HostManagerRollbackStateDocument | undefined, software: readonly SoftwareRecord[]): DetailRecord[] {
  if (!state) {
    return [];
  }

  const placementRecords = state.appliedPlacements.flatMap((placement) => {
    const owner = software.find(record => record.id === placement.softwareId);
    const target = smartPlacementTarget(placement, owner ? softwareRecordName(owner) : undefined);
    return placement.records.map(record => createDetailRecord(target, placement.resourceKind, placement.updatedAt, record));
  });
  return placementRecords.sort((left, right) => left.sequence !== null || right.sequence !== null
    ? (right.sequence ?? 0) - (left.sequence ?? 0)
    : Date.parse(right.updatedAt) - Date.parse(left.updatedAt));
}

function createDetailRecord(
  target: string,
  resourceKind: string,
  updatedAt: string,
  record: HostManagerAppliedRecord): DetailRecord {
  return { target, kind: record.kind, resourceKind, updatedAt, sequence: record.sequence, recordedAt: record.recordedAt };
}

function historyTarget(entry: SmartRecordHistoryEntry, software: readonly SoftwareRecord[]) {
  const owner = software.find(record => record.id === entry.softwareId);
  return smartPlacementTarget(entry, owner ? softwareRecordName(owner) : undefined);
}

function detailRecordSections(record: DetailRecord) {
  return compactUserDetailSections([
    userDetailSection(uiText.smartReport.section.result, [
      userDetailItem(uiText.smartReport.field.target, record.target),
      userDetailItem(uiText.smartReport.field.change, formatRecordKind(record.kind)),
      userDetailItem(uiText.smartReport.field.affectedResource, formatResourceKind(record.resourceKind)),
      userDetailItem(uiText.smartReport.field.updatedAt, userFacingDateTime(record.updatedAt)),
      record.sequence === null ? null : userDetailItem(uiText.smartReport.history.field.sequence, formatNumber(record.sequence)),
      record.recordedAt === null ? null : userDetailItem(uiText.smartReport.history.field.recordedAt, userFacingDateTime(record.recordedAt))
    ])
  ]);
}

function historyRecordSections(entry: SmartRecordHistoryEntry, software: readonly SoftwareRecord[]) {
  const field = uiText.smartReport.history.field;
  return compactUserDetailSections([
    userDetailSection(uiText.smartReport.section.result, [
      userDetailItem(uiText.smartReport.field.target, historyTarget(entry, software)),
      userDetailItem(uiText.smartReport.field.change, formatRecordKind(entry.kind)),
      userDetailItem(uiText.smartReport.field.affectedResource, formatResourceKind(entry.resourceKind)),
      userDetailItem(field.restoreResult, restoreResultText(entry.restoreResult)),
      userDetailItem(field.sequence, formatNumber(entry.sequence)),
      userDetailItem(field.appliedAt, userFacingDateTime(entry.appliedAt)),
      userDetailItem(field.recordedAt, userFacingDateTime(entry.recordedAt)),
      userDetailItem(field.restoredAt, userFacingDateTime(entry.restoredAt))
    ])
  ]);
}

function restoreResultText(result: SmartRecordRestoreResult) {
  return uiText.smartReport.history.restoreResult[result];
}

function formatNumber(value: number) {
  return formatInterfaceNumber(value);
}


function formatRecordKind(kind: string) {
  switch (kind) {
    case "adapter_policy":
      return uiText.smartReport.kind.softwareResourceSettings;
    case "gpu_preference":
      return uiText.smartReport.kind.gpuSelection;
    case "gpu_runtime_rebuild_trigger":
      return uiText.smartReport.kind.runtimeGpuSwitch;
    case "cpu_affinity":
      return uiText.smartReport.kind.cpuCoreAllocation;
    case "a1":
    case "level_1":
    case "level_2":
    case "level_3":
    case "level_4":
      return uiText.smartReport.kind.resourceOptimization;
    default:
      return uiText.smartReport.kind.schedulingAdjustment;
  }
}

function formatResourceKind(kind: string) {
  switch (kind) {
    case "cpu": return uiText.smartReport.resource.cpu;
    case "gpu": return uiText.smartReport.resource.gpu;
    case "memory": return uiText.smartReport.resource.memory;
    case "vram": return uiText.smartReport.resource.vram;
    case "disk": return uiText.smartReport.resource.disk;
    case "network": return uiText.smartReport.resource.network;
    default: return uiText.smartReport.resource.software;
  }
}
