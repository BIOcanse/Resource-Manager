import { createEffect, createMemo, createSignal, For, onCleanup, Show } from "solid-js";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds";
import { frontendVisibilitySurface } from "../../frontendWork/frontendVisibilitySurface";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext";
import { useFrontendVisibilityDemand } from "../../frontendWork/useFrontendVisibilityDemand";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import { sourceCanRender, type SourceSnapshot } from "../../frontendRuntime/source/SourceSnapshot";
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
  updatedAt: string;
}

export function HostManagerSmartCoordinatorDetailsReport() {
  const demandId = "details.smart-report";
  useFrontendVisibilityDemand(demandId, [frontendWorkIds.detailsSmartReport]);
  const frontendWork = useFrontendWork();
  const frontendRuntime = useFrontendRuntime();
  const [selectedRecord, setSelectedRecord] = createSignal<DetailRecord | null>(null);
  const [stateSource, setStateSource] =
    createSignal<SourceSnapshot<HostManagerRollbackStateDocument> | null>(null);
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
  const records = createMemo(() => collectDetailRecords(state()));

  createEffect(() => {
    if (!frontendWork.isNeeded(frontendWorkIds.detailsSmartReport)) {
      setStateSource(null);
      return;
    }
    const unsubscribe = frontendRuntime.sources.smartCoordinatorState.subscribeSnapshot(
      smartStateSubscriptionIntervalMilliseconds,
      setStateSource);
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
              <For each={records().slice(0, 8)}>
                {(record) => (
                  <article class="smart-details-record">
                    <div class="smart-details-record-main">
                      <strong>{record.target}</strong>
                      <span>{formatRecordKind(record.kind)} · {formatResourceKind(record.resourceKind)}</span>
                    </div>
                    <div class="smart-details-record-metrics">
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

function SummaryTile(props: { label: string; value: string }) {
  return (
    <div class="smart-details-tile">
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
  return { target, kind: record.kind, resourceKind, updatedAt };
}

function detailRecordSections(record: DetailRecord) {
  return compactUserDetailSections([
    userDetailSection(uiText.smartReport.section.result, [
      userDetailItem(uiText.smartReport.field.target, record.target),
      userDetailItem(uiText.smartReport.field.change, formatRecordKind(record.kind)),
      userDetailItem(uiText.smartReport.field.affectedResource, formatResourceKind(record.resourceKind)),
      userDetailItem(uiText.smartReport.field.updatedAt, userFacingDateTime(record.updatedAt))
    ])
  ]);
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
