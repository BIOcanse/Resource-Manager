import { createEffect, createMemo, createSignal, For, onCleanup, Show } from "solid-js";
import { frontendWorkIds } from "../frontendWork/frontendWorkIds";
import { frontendVisibilitySurface } from "../frontendWork/frontendVisibilitySurface";
import { useFrontendWork } from "../frontendWork/FrontendWorkContext";
import { useFrontendVisibilityDemand } from "../frontendWork/useFrontendVisibilityDemand";
import { useFrontendRuntime } from "../frontendRuntime/FrontendRuntimeContext";
import { userFacingDateTime } from "../presentation/userFacingText";
import { compactUserDetailSections, userDetailItem, userDetailSection } from "../presentation/userDetails";
import type { HostManagerAppliedRecord, HostManagerRollbackStateDocument } from "../types";
import { UserDetailsDialog } from "./UserDetailsDialog";

const smartStateSubscriptionIntervalMilliseconds = 3_000;

interface DetailRecord {
  target: string;
  kind: string;
  resourceKind: string;
  acceptedCount: number;
  timedOutCount: number;
  rejectedCount: number;
  needsAttention: boolean;
  releasedMemoryBytes: number;
  releasedVramBytes: number;
  updatedAt: string;
}

export function HostManagerSmartCoordinatorDetailsReport() {
  const demandId = "details.smart-report";
  useFrontendVisibilityDemand(demandId, [frontendWorkIds.detailsSmartReport]);
  const frontendWork = useFrontendWork();
  const frontendRuntime = useFrontendRuntime();
  const [selectedRecord, setSelectedRecord] = createSignal<DetailRecord | null>(null);
  const [state, setState] = createSignal<HostManagerRollbackStateDocument>();
  const records = createMemo(() => collectDetailRecords(state()));
  const attentionCount = createMemo(() => records().filter((record) => record.needsAttention).length);
  const releasedMemoryBytes = createMemo(() => records().reduce((sum, record) => sum + record.releasedMemoryBytes, 0));
  const releasedVramBytes = createMemo(() => records().reduce((sum, record) => sum + record.releasedVramBytes, 0));

  createEffect(() => {
    if (!frontendWork.isNeeded(frontendWorkIds.detailsSmartReport)) {
      return;
    }
    const unsubscribe = frontendRuntime.sources.smartCoordinatorState.subscribe(
      smartStateSubscriptionIntervalMilliseconds,
      setState);
    onCleanup(unsubscribe);
  });

  return (
    <section
      {...frontendVisibilitySurface("visible.details.smart-report.surface", [demandId])}
      class="smart-details-report"
      aria-label="智能调度报告"
    >
      <div class="panel-header smart-details-header">
        <div class="optimization-heading">
          <h2>智能调度报告</h2>
        </div>
      </div>
      <Show
        when={state()}
        fallback={<div class="optimization-empty">暂无智能调度记录。</div>}
      >
        {(current) => (
          <>
          <div class="smart-details-summary">
            <SummaryTile label="已完成" value={formatNumber(current().appliedPlacements.length)} />
            <SummaryTile label="需要关注" value={formatNumber(attentionCount())} tone={attentionCount() > 0 ? "danger" : "normal"} />
            <SummaryTile label="内存释放" value={formatBytes(releasedMemoryBytes())} />
            <SummaryTile label="显存释放" value={formatBytes(releasedVramBytes())} />
          </div>
          <Show
            when={records().length > 0}
            fallback={<div class="optimization-empty">暂无智能调度记录。</div>}
          >
            <div class="smart-details-records">
              <For each={records().slice(0, 8)}>
                {(record) => (
                  <article class="smart-details-record">
                    <div class="smart-details-record-main">
                      <strong>{record.target}</strong>
                      <span>{formatRecordKind(record.kind)} · {formatResourceKind(record.resourceKind)}</span>
                    </div>
                    <div class="smart-details-record-metrics">
                      <span classList={{ danger: record.needsAttention }}>
                        {record.needsAttention ? "需要关注" : "已完成"}
                      </span>
                      <button class="secondary details-button" type="button" onClick={() => setSelectedRecord(record)}>
                        详细信息
                      </button>
                    </div>
                  </article>
                )}
              </For>
            </div>
          </Show>
          </>
        )}
      </Show>
      <UserDetailsDialog
        open={Boolean(selectedRecord())}
        title={`${selectedRecord()?.target ?? "调度"}详细信息`}
        sections={selectedRecord() ? detailRecordSections(selectedRecord()!) : []}
        onClose={() => setSelectedRecord(null)}
      />
    </section>
  );

}

function SummaryTile(props: { label: string; value: string; tone?: "normal" | "danger" }) {
  return (
    <div class="smart-details-tile" classList={{ danger: props.tone === "danger" }}>
      <span>{props.label}</span>
      <strong>{props.value}</strong>
    </div>
  );
}

function collectDetailRecords(state?: HostManagerRollbackStateDocument): DetailRecord[] {
  if (!state) {
    return [];
  }

  const placementRecords = state.appliedPlacements.flatMap((placement) =>
    placement.records.map((record) => createDetailRecord(placement.displayName, placement.resourceKind, placement.updatedAt, record)));
  return placementRecords
    .sort((left, right) => Date.parse(right.updatedAt) - Date.parse(left.updatedAt));
}

function createDetailRecord(
  target: string,
  resourceKind: string,
  updatedAt: string,
  record: HostManagerAppliedRecord): DetailRecord {
  const metadata = record.metadata ?? {};
  const resultCode = parseIntValue(metadata["resourceAction.resultCode"]);
  const dangerFlags = parseIntValue(metadata["resourceAction.dangerFlags"]);
  return {
    target,
    kind: record.kind,
    resourceKind,
    acceptedCount: parseIntValue(metadata["resourceAction.acceptedCount"]),
    timedOutCount: parseIntValue(metadata["resourceAction.timedOutCount"]),
    rejectedCount: parseIntValue(metadata["resourceAction.rejectedCount"]),
    needsAttention: dangerFlags > 0 || resultCode >= 5,
    releasedMemoryBytes: parseIntValue(metadata["releasedMemoryBytes"]),
    releasedVramBytes: parseIntValue(metadata["releasedVramBytes"]),
    updatedAt
  };
}

function detailRecordSections(record: DetailRecord) {
  return compactUserDetailSections([
    userDetailSection("调度结果", [
      userDetailItem("对象", record.target),
      userDetailItem("调整内容", formatRecordKind(record.kind)),
      userDetailItem("影响资源", formatResourceKind(record.resourceKind)),
      userDetailItem("状态", record.needsAttention ? "需要关注" : "已完成"),
      userDetailItem("更新时间", userFacingDateTime(record.updatedAt))
    ]),
    userDetailSection("执行情况", [
      userDetailItem("成功", `${record.acceptedCount} 项`),
      record.timedOutCount > 0 ? userDetailItem("超时", `${record.timedOutCount} 项`) : null,
      record.rejectedCount > 0 ? userDetailItem("未执行", `${record.rejectedCount} 项`) : null,
      record.releasedMemoryBytes > 0 ? userDetailItem("已释放内存", formatBytes(record.releasedMemoryBytes)) : null,
      record.releasedVramBytes > 0 ? userDetailItem("已释放显存", formatBytes(record.releasedVramBytes)) : null
    ])
  ]);
}

function parseIntValue(value?: string) {
  const number = Number.parseInt(value ?? "0", 10);
  return Number.isFinite(number) ? number : 0;
}

function formatNumber(value: number) {
  return new Intl.NumberFormat().format(value);
}

function formatBytes(value: number) {
  if (!Number.isFinite(value) || value <= 0) {
    return "0 B";
  }

  const units = ["B", "KB", "MB", "GB", "TB"];
  let current = value;
  let index = 0;
  while (current >= 1024 && index < units.length - 1) {
    current /= 1024;
    index++;
  }

  return `${current >= 10 || index === 0 ? current.toFixed(0) : current.toFixed(1)} ${units[index]}`;
}

function formatRecordKind(kind: string) {
  switch (kind) {
    case "AdapterPolicy":
      return "软件资源设置";
    case "GpuPreference":
      return "显卡选择";
    case "GpuRuntimeRebuildTrigger":
      return "运行时显卡切换";
    case "CpuAffinity":
      return "CPU 核心分配";
    case "A1":
    case "Level1":
    case "Level2":
    case "Level3":
    case "Level4":
      return "资源优化";
    default:
      return "调度调整";
  }
}

function formatResourceKind(kind: string) {
  switch (kind.trim().toLocaleLowerCase()) {
    case "cpu": return "处理器";
    case "gpu": return "图形处理器";
    case "memory": return "内存";
    case "vram": return "显存";
    case "disk": return "磁盘";
    case "network": return "网络";
    default: return "软件资源";
  }
}
