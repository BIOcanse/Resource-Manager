import { createEffect, createSignal, For, on, onCleanup, Show } from "solid-js";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import { frontendVisibilitySurface } from "../../frontendWork/frontendVisibilitySurface";
import { useFrontendVisibilityDemand } from "../../frontendWork/useFrontendVisibilityDemand";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds";
import { CurrentPageHeading, useCurrentPageName } from "../../app/navigation/currentPageName.tsx";
import { readDeviceActionRecords, deviceActionRecordsPageSize as pageSize, type DeviceActionRecord } from "../../data/deviceActions/deviceActions.ts";
import { useSource } from "../../frontendRuntime/source/useSource.ts";
import { formatInterfaceNumber } from "../../presentation/interfaceNumbers.ts";
import { userFacingDateTime, userFacingErrorMessage } from "../../presentation/userFacingText.ts";
import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { useReportHistoryClear, type ReportHistoryClearFeedback } from "../reportHistory/useReportHistoryClear.ts";
import { MemoryProtectionFact as Fact } from "../memoryProtection/MemoryProtectionRecordList.tsx";
import { uiText } from "../../text.ts";
import { deviceResultText, deviceWin32DetailText } from "./deviceActionsText.ts";

/* Read newest-first pages while visible and cancel reads when hidden. Clearing removes persisted history and reloads the first page, retaining active records. */
export function DeviceActionRecordsPage(props: ReportHistoryClearFeedback) {
  const demandId = "details.device-actions";
  const workId = frontendWorkIds.detailsDeviceActions;
  useFrontendVisibilityDemand(demandId, [workId]);
  const work = useFrontendWork();
  const runtime = useFrontendRuntime();
  const pageName = useCurrentPageName();
  const copy = () => uiText.deviceActions.records;
  // Retain accepted records across unmounts; demand owns reads.
  // Replace the retained view with each accepted backend result.
  const source = useSource(runtime.sources.deviceActionRecords, () => ({ active: work.isNeeded(workId) }));
  const records = () => source.snapshot().data?.records ?? null;
  const hasMore = () => source.snapshot().data?.hasMore ?? false;
  const [loadingMore, setLoadingMore] = createSignal(false);
  const [moreError, setMoreError] = createSignal<string | null>(null);
  const loading = () => loadingMore() ? "more"
    : source.snapshot().status === "loading" || source.snapshot().status === "refreshing" ? "first" : null;
  const error = () => moreError() ?? (source.snapshot().error
    ? userFacingErrorMessage(source.snapshot().error, copy().readFailed) : null);
  let controller: AbortController | null = null;

  const read = async (more: boolean) => {
    controller?.abort();
    setMoreError(null);
    if (!more) {
      await source.refresh({ afterCurrent: true });
      return;
    }
    const current = new AbortController();
    controller = current;
    setLoadingMore(true);
    const before = records()?.at(-1)?.sequence;
    try {
      const page = await readDeviceActionRecords(runtime.requestClient,
        before === undefined ? { limit: pageSize } : { limit: pageSize, beforeSequence: before }, current.signal);
      if (current.signal.aborted) return;
      source.acceptAuthoritative({ records: [...records() ?? [], ...page.records], hasMore: page.records.length === pageSize });
    } catch (failure) {
      if (current.signal.aborted) return;
      setMoreError(userFacingErrorMessage(failure, copy().readFailed));
    } finally {
      if (controller === current) {
        controller = null;
        setLoadingMore(false);
      }
    }
  };

  // Demand owns first-page reads; cancel manual pagination when hidden.
  createEffect(on(() => work.isNeeded(workId), (needed) => {
    if (!needed) controller?.abort();
  }));
  onCleanup(() => controller?.abort());

  // Loaded pages may omit history, so confirmation cannot claim a total count.
  const historyCount = () => (records()?.filter(record => record.state === "history").length ?? 0) || (hasMore() ? 1 : 0);
  const history = useReportHistoryClear({ ...props, kind: "deviceActions", historyCount, afterClear: () => read(false) });

  return (
    <section {...frontendVisibilitySurface("visible.details.device-actions.surface", [demandId])}
      class="memory-protection-report device-action-records" aria-label={pageName()}>
      <div class="panel-header memory-protection-report-header">
        <CurrentPageHeading />
        <div class="memory-protection-report-actions">
          <button class="secondary panel-refresh-button" type="button" disabled={loading() !== null}
            onClick={() => void read(false)}>{copy().refresh}</button>
          <button class="secondary panel-refresh-button" type="button"
            disabled={historyCount() === 0 || history.clearing()} onClick={() => void history.clear()}>
            <StableLabel labels={{ idle: uiText.reportHistory.clear, busy: uiText.reportHistory.clearing }}
              current={history.clearing() ? "busy" : "idle"} />
          </button>
        </div>
      </div>
      <p class="memory-protection-report-note">{copy().intro}</p>
      <Show when={error()}>{(message) => <p class="memory-protection-report-note warning" role="alert">{message()}</p>}</Show>
      <Show when={records()} fallback={
        <Show when={!error()}>
          <p class="sr-only" role="status">{copy().loading}</p>
          {/* Preserve the record layout while loading to prevent content shifts. */}
          <p class="memory-protection-report-note" aria-hidden="true">--</p>
          <ol class="memory-protection-list" aria-hidden="true"><DeviceActionRecordPlaceholder /></ol>
        </Show>
      }>
        {(current) => (
          <>
            <p class="memory-protection-report-note">{copy().shown(formatInterfaceNumber(current().length))}</p>
            <Show when={current().length > 0} fallback={<p class="memory-protection-empty">{copy().empty}</p>}>
              <ol class="memory-protection-list">
                <For each={current()}>{(record) => <DeviceActionRecordItem record={record} />}</For>
              </ol>
            </Show>
            <Show when={hasMore()}>
              <div class="memory-protection-actions">
                <button class="secondary" type="button" disabled={loading() !== null} onClick={() => void read(true)}>
                  <StableLabel labels={{ idle: copy().loadMore, busy: copy().loadingMore }}
                    current={loading() === "more" ? "busy" : "idle"} />
                </button>
              </div>
            </Show>
          </>
        )}
      </Show>
    </section>
  );
}

function DeviceActionRecordItem(props: { record: DeviceActionRecord }) {
  const copy = () => uiText.deviceActions.records;
  const record = () => props.record;
  return (
    <li class="memory-protection-item device-action-record" data-state={record().state} data-result={record().resultCode}
      data-sequence={record().sequence}>
      <div class="memory-protection-item-head">
        <strong>{deviceResultText(record().resultCode)}</strong>
        <span class="memory-protection-outcome">{copy().states[record().state]}</span>
      </div>
      <dl class="memory-protection-facts">
        <Fact label={copy().time}>{userFacingDateTime(record().occurredAt)}</Fact>
        <Fact label={copy().sequence}>{formatInterfaceNumber(record().sequence)}</Fact>
        <Show when={deviceWin32DetailText(record().detail)}>
          {(detail) => <Fact label={copy().detail}><code class="memory-protection-path">{detail()}</code></Fact>}
        </Show>
        <Fact label={copy().device} wide><code class="memory-protection-path">{record().deviceInstanceId}</code></Fact>
        <Fact label={copy().report} wide><code class="memory-protection-path">{record().reportEntryId}</code></Fact>
      </dl>
    </li>
  );
}

function DeviceActionRecordPlaceholder() {
  const copy = () => uiText.deviceActions.records;
  return (
    <li class="memory-protection-item device-action-record-placeholder">
      <div class="memory-protection-item-head"><strong>--</strong><span class="memory-protection-outcome">--</span></div>
      <dl class="memory-protection-facts">
        <Fact label={copy().time}>--</Fact>
        <Fact label={copy().sequence}>--</Fact>
        <Fact label={copy().device} wide>--</Fact>
        <Fact label={copy().report} wide>--</Fact>
      </dl>
    </li>
  );
}
