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

/*
 * 详细信息 → 内存保护记录（docs/report_lifecycle_2026-10-28.md“主机内存保护的处理记录”）。
 * 记录写下时这个进程的处理已经结束，都是历史条目；持久保存，不自动删除，只由标题栏的“清空历史”删除，
 * 清空不删切片文件。数据来自设置页同一个订阅（runtime.sources.memoryProtection），页面可见时订阅；
 * 清空后后端推送新的 records 帧。容量满了以后新的处理不再记下，后端给出没记下的条数，这里照原样显示。
 */
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
