import { formatBytes } from "../../presentation/byteUnits.ts";
import { createEffect, createMemo, createSignal, For, onCleanup, Show } from "solid-js";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds";
import { frontendVisibilitySurface } from "../../frontendWork/frontendVisibilitySurface";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext";
import { useFrontendVisibilityDemand } from "../../frontendWork/useFrontendVisibilityDemand";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import { userFacingDateTime } from "../../presentation/userFacingText";
import { compactUserDetailSections, userDetailItem, userDetailSection } from "../../presentation/userDetails";
import type { HostManagerAppliedRecord, HostManagerRollbackStateDocument } from "../../types";
import { UserDetailsDialog } from "../../components/UserDetailsDialog";
import { uiText } from "../../text.ts";

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
      aria-label={uiText.smartReport.panel}
    >
      <div class="panel-header smart-details-header">
        <div class="optimization-heading">
          <h2>{uiText.smartReport.panel}</h2>
        </div>
      </div>
      <Show
        when={state()}
        fallback={<div class="optimization-empty">{uiText.smartReport.empty}</div>}
      >
        {(current) => (
          <>
          <div class="smart-details-summary">
            <SummaryTile label={uiText.smartReport.completed} value={formatNumber(current().appliedPlacements.length)} />
            <SummaryTile label={uiText.smartReport.needsAttention} value={formatNumber(attentionCount())} tone={attentionCount() > 0 ? "danger" : "normal"} />
            <SummaryTile label={uiText.smartReport.releasedMemory} value={formatBytes(releasedMemoryBytes(), "memory")} />
            <SummaryTile label={uiText.smartReport.releasedVram} value={formatBytes(releasedVramBytes(), "memory")} />
          </div>
          <Show
            when={records().length > 0}
            fallback={<div class="optimization-empty">{uiText.smartReport.empty}</div>}
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
                        {record.needsAttention ? uiText.smartReport.needsAttention : uiText.smartReport.completed}
                      </span>
                      <button class="secondary details-button" type="button" onClick={() => setSelectedRecord(record)}>
                        {uiText.smartReport.details}
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
        title={uiText.smartReport.detailTitle(selectedRecord()?.target ?? uiText.smartReport.fallbackTarget)}
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
    userDetailSection(uiText.smartReport.section.result, [
      userDetailItem(uiText.smartReport.field.target, record.target),
      userDetailItem(uiText.smartReport.field.change, formatRecordKind(record.kind)),
      userDetailItem(uiText.smartReport.field.affectedResource, formatResourceKind(record.resourceKind)),
      userDetailItem(uiText.smartReport.field.state, record.needsAttention ? uiText.smartReport.needsAttention : uiText.smartReport.completed),
      userDetailItem(uiText.smartReport.field.updatedAt, userFacingDateTime(record.updatedAt))
    ]),
    userDetailSection(uiText.smartReport.section.execution, [
      userDetailItem(uiText.smartReport.field.accepted, uiText.smartReport.itemCount(record.acceptedCount)),
      record.timedOutCount > 0 ? userDetailItem(uiText.smartReport.field.timedOut, uiText.smartReport.itemCount(record.timedOutCount)) : null,
      record.rejectedCount > 0 ? userDetailItem(uiText.smartReport.field.rejected, uiText.smartReport.itemCount(record.rejectedCount)) : null,
      record.releasedMemoryBytes > 0 ? userDetailItem(uiText.smartReport.field.releasedMemory, formatBytes(record.releasedMemoryBytes, "memory")) : null,
      record.releasedVramBytes > 0 ? userDetailItem(uiText.smartReport.field.releasedVram, formatBytes(record.releasedVramBytes, "memory")) : null
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


function formatRecordKind(kind: string) {
  switch (kind) {
    case "AdapterPolicy":
      return uiText.smartReport.kind.softwareResourceSettings;
    case "GpuPreference":
      return uiText.smartReport.kind.gpuSelection;
    case "GpuRuntimeRebuildTrigger":
      return uiText.smartReport.kind.runtimeGpuSwitch;
    case "CpuAffinity":
      return uiText.smartReport.kind.cpuCoreAllocation;
    case "A1":
    case "Level1":
    case "Level2":
    case "Level3":
    case "Level4":
      return uiText.smartReport.kind.resourceOptimization;
    default:
      return uiText.smartReport.kind.schedulingAdjustment;
  }
}

function formatResourceKind(kind: string) {
  switch (kind.trim().toLocaleLowerCase()) {
    case "cpu": return uiText.smartReport.resource.cpu;
    case "gpu": return uiText.smartReport.resource.gpu;
    case "memory": return uiText.smartReport.resource.memory;
    case "vram": return uiText.smartReport.resource.vram;
    case "disk": return uiText.smartReport.resource.disk;
    case "network": return uiText.smartReport.resource.network;
    default: return uiText.smartReport.resource.software;
  }
}
