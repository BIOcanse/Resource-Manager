import { For, Show } from "solid-js";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import { usePushSource } from "../../frontendRuntime/push/usePushSource.ts";
import { sourceCanRender } from "../../frontendRuntime/source/SourceSnapshot";
import { frontendVisibilitySurface } from "../../frontendWork/frontendVisibilitySurface";
import { useFrontendVisibilityDemand } from "../../frontendWork/useFrontendVisibilityDemand";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds";
import { CurrentPageHeading, useCurrentPageName } from "../../app/navigation/currentPageName.tsx";
import type { CpuMonopolyActionRecord } from "../../data/cpuMonopolyProtection/cpuMonopolyProtection.ts";
import { formatInterfaceNumber } from "../../presentation/interfaceNumbers.ts";
import { userFacingDateTime } from "../../presentation/userFacingText.ts";
import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { useReportHistoryClear, type ReportHistoryClearFeedback } from "../reportHistory/useReportHistoryClear.ts";
import { MemoryProtectionFact as Fact } from "../memoryProtection/MemoryProtectionRecordList.tsx";
import { uiText } from "../../text.ts";
import {
  cpuMonopolyActionReasonText, cpuMonopolyPercentText, cpuMonopolyPriorityText, cpuMonopolySecondsText
} from "./cpuMonopolyText.ts";

/* Visible demand owns the shared subscription. Clearing removes persisted history while retaining active records; dropped counts come from the backend. */
export function CpuMonopolyRecordsPage(props: ReportHistoryClearFeedback) {
  const demandId = "details.cpu-monopoly";
  useFrontendVisibilityDemand(demandId, [frontendWorkIds.detailsCpuMonopoly]);
  const frontendWork = useFrontendWork();
  const frontendRuntime = useFrontendRuntime();
  const pageName = useCurrentPageName();
  const source = usePushSource(frontendRuntime.sources.cpuMonopolyProtection, () => ({
    active: frontendWork.isNeeded(frontendWorkIds.detailsCpuMonopoly)
  })).snapshot;
  const view = () => {
    const snapshot = source();
    return sourceCanRender(snapshot) ? snapshot.data : null;
  };
  const unavailable = () => source().status === "error" || source().status === "unavailable";
  const copy = () => uiText.cpuMonopoly.records;
  const historyCount = () => view()?.records.filter(record => record.state === "history").length ?? 0;
  const history = useReportHistoryClear({ ...props, kind: "cpuMonopolyProtection", historyCount });

  return (
    <section
      {...frontendVisibilitySurface("visible.details.cpu-monopoly.surface", [demandId])}
      class="memory-protection-report cpu-monopoly-report"
      aria-label={pageName()}
    >
      <div class="panel-header memory-protection-report-header">
        <CurrentPageHeading />
        <div class="memory-protection-report-actions">
          <button class="secondary panel-refresh-button" type="button"
            disabled={historyCount() === 0 || history.clearing()} onClick={() => void history.clear()}>
            <StableLabel labels={{ idle: uiText.reportHistory.clear, busy: uiText.reportHistory.clearing }}
              current={history.clearing() ? "busy" : "idle"} />
          </button>
        </div>
      </div>
      <p class="memory-protection-report-note">{copy().intro}</p>
      <Show when={view()} fallback={
        <Show when={unavailable()} fallback={
          <>
            <p class="sr-only" role="status">{copy().loading}</p>
            {/* Preserve the record layout while loading to prevent content shifts. */}
            <p class="memory-protection-report-note" aria-hidden="true">--</p>
            <ol class="memory-protection-list" aria-hidden="true"><CpuMonopolyRecordPlaceholder /></ol>
          </>
        }>
          <p class="memory-protection-report-note" role="alert">{copy().unavailable}</p>
        </Show>
      }>
        {(current) => (
          <>
            <p class="memory-protection-report-note">
              {copy().count(formatInterfaceNumber(current().records.length), formatInterfaceNumber(current().recordCapacity))}
            </p>
            <Show when={current().unrecordedRecordCount > 0}>
              <p class="memory-protection-report-note warning" role="status">{copy().unrecorded(current().unrecordedRecordCount)}</p>
            </Show>
            <Show when={current().records.length > 0} fallback={<p class="memory-protection-empty">{copy().empty}</p>}>
              <ol class="memory-protection-list">
                <For each={current().records}>{(record) => <CpuMonopolyRecord record={record} />}</For>
              </ol>
            </Show>
          </>
        )}
      </Show>
    </section>
  );
}

function CpuMonopolyRecord(props: { record: CpuMonopolyActionRecord }) {
  const copy = () => uiText.cpuMonopoly;
  const record = () => props.record;
  return (
    <li class="memory-protection-item cpu-monopoly-record" data-outcome={record().outcome} data-state={record().state}
      data-action-id={record().actionId}>
      <div class="memory-protection-item-head">
        <strong>{copy().process(record().name, record().processId)}</strong>
        <span class="memory-protection-outcome cpu-monopoly-outcome">
          {copy().records.outcomes[record().outcome]} · {copy().records.states[record().state]}
        </span>
      </div>
      <dl class="memory-protection-facts">
        <Fact label={copy().records.time}>{userFacingDateTime(record().at)}</Fact>
        <Fact label={copy().records.cpu}>{cpuMonopolyPercentText(record().cpuPercent)}</Fact>
        <Fact label={copy().records.previousPriority}>{cpuMonopolyPriorityText(record().previousPriorityClass)}</Fact>
        <Fact label={copy().records.threshold}>{cpuMonopolyPercentText(record().thresholdPercent)}</Fact>
        <Fact label={copy().records.duration}>{cpuMonopolySecondsText(record().sustainedSeconds)}</Fact>
        <Show when={record().reason}>
          {(reason) => (
            <Fact label={copy().records.reason} wide fullContent={reason()}>
              <span class="cpu-monopoly-reason" data-reason={reason()}>{cpuMonopolyActionReasonText(reason())}</span>
            </Fact>
          )}
        </Show>
        <Show when={record().detail}>
          {(detail) => <Fact label={copy().records.detail} wide><code class="memory-protection-path">{detail()}</code></Fact>}
        </Show>
        <Show when={record().endedAt}>
          {(endedAt) => <Fact label={copy().records.endedAt}>{userFacingDateTime(endedAt())}</Fact>}
        </Show>
        <Show when={record().endReason}>
          {(endReason) => (
            <Fact label={copy().records.endReason} fullContent={endReason()}>
              <span class="cpu-monopoly-end-reason" data-reason={endReason()}>{cpuMonopolyActionReasonText(endReason())}</span>
            </Fact>
          )}
        </Show>
      </dl>
    </li>
  );
}

/** Use the populated record structure with placeholder values. */
function CpuMonopolyRecordPlaceholder() {
  const copy = () => uiText.cpuMonopoly.records;
  return (
    <li class="memory-protection-item cpu-monopoly-record-placeholder">
      <div class="memory-protection-item-head"><strong>--</strong><span class="memory-protection-outcome">--</span></div>
      <dl class="memory-protection-facts">
        <Fact label={copy().time}>--</Fact>
        <Fact label={copy().cpu}>--</Fact>
        <Fact label={copy().previousPriority}>--</Fact>
        <Fact label={copy().threshold}>--</Fact>
        <Fact label={copy().duration}>--</Fact>
      </dl>
    </li>
  );
}
