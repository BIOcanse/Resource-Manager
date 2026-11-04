import { Show } from "solid-js";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import { usePushSource } from "../../frontendRuntime/push/usePushSource.ts";
import { sourceCanRender } from "../../frontendRuntime/source/SourceSnapshot";
import { frontendVisibilitySurface } from "../../frontendWork/frontendVisibilitySurface";
import { useFrontendVisibilityDemand } from "../../frontendWork/useFrontendVisibilityDemand";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds";
import { CurrentPageHeading, useCurrentPageName } from "../../app/navigation/currentPageName.tsx";
import { formatInterfaceNumber } from "../../presentation/interfaceNumbers.ts";
import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { useReportHistoryClear, type ReportHistoryClearFeedback } from "../reportHistory/useReportHistoryClear.ts";
import { uiText } from "../../text.ts";
import { MemoryProtectionActionList } from "./MemoryProtectionRecordList.tsx";

/* All records are persisted history. Clearing retains slice files; visible demand owns the shared subscription and the backend supplies capacity-dropped counts. */
export function MemoryProtectionReportPage(props: ReportHistoryClearFeedback) {
  const demandId = "details.memory-protection";
  useFrontendVisibilityDemand(demandId, [frontendWorkIds.detailsMemoryProtection]);
  const frontendWork = useFrontendWork();
  const frontendRuntime = useFrontendRuntime();
  const pageName = useCurrentPageName();
  const source = usePushSource(frontendRuntime.sources.memoryProtection, () => ({
    active: frontendWork.isNeeded(frontendWorkIds.detailsMemoryProtection)
  })).snapshot;
  const view = () => {
    const snapshot = source();
    return sourceCanRender(snapshot) ? snapshot.data : null;
  };
  const unavailable = () => source().status === "error" || source().status === "unavailable";
  const copy = () => uiText.memoryProtection;
  const historyCount = () => view()?.records.length ?? 0;
  const history = useReportHistoryClear({ ...props, kind: "memoryProtection", historyCount });

  return (
    <section
      {...frontendVisibilitySurface("visible.details.memory-protection.surface", [demandId])}
      class="memory-protection-report"
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
      <p class="memory-protection-report-note">{copy().reportIntro}</p>
      <Show when={view()} fallback={
        <p class="memory-protection-report-note" role={unavailable() ? "alert" : "status"}>
          {unavailable() ? copy().recordsUnavailable : copy().recordsLoading}
        </p>
      }>
        {(current) => (
          <>
            <p class="memory-protection-report-note">
              {copy().recordsCount(formatInterfaceNumber(current().records.length), formatInterfaceNumber(current().recordCapacity))}
            </p>
            <Show when={current().unrecordedRecordCount > 0}>
              <p class="memory-protection-report-note warning" role="status">
                {copy().recordsUnrecorded(current().unrecordedRecordCount)}
              </p>
            </Show>
            <Show when={current().records.length > 0} fallback={<p class="memory-protection-empty">{copy().recordsEmpty}</p>}>
              <MemoryProtectionActionList records={current().records} />
            </Show>
          </>
        )}
      </Show>
    </section>
  );
}
