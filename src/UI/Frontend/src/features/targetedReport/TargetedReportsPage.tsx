import { createComputed, createEffect, createMemo, createSignal, For, onCleanup, Show } from "solid-js";
import { createStore, reconcile } from "solid-js/store";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext.tsx";
import type { SourceLease } from "../../frontendRuntime/source/SourceDescriptor.ts";
import { SourceLeaseBinding } from "../../frontendRuntime/source/SourceLeaseBinding.ts";
import type { SourceSnapshot } from "../../frontendRuntime/source/SourceSnapshot.ts";
import { sourceCanRender } from "../../frontendRuntime/source/SourceSnapshot.ts";
import { useSource } from "../../frontendRuntime/source/useSource.ts";
import { frontendWorkIds } from "../../frontendWork/frontendWorkIds.ts";
import { frontendVisibilitySurface } from "../../frontendWork/frontendVisibilitySurface.ts";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext.tsx";
import { useFrontendVisibilityDemand } from "../../frontendWork/useFrontendVisibilityDemand.tsx";
import { localizedMetricLabel } from "../../presentation/metricLabels.ts";
import { softwareRecordName } from "../../presentation/softwareFacts.ts";
import { currentLanguage, uiText } from "../../text.ts";
import {
  deleteRecording, exportRecording, stopRecording,
  type RecordingHeader, type RecordingReport
} from "./targetedRecordingApi.ts";
import { TargetedReportChart, type ChartPoint } from "./TargetedReportCharts.tsx";
import { curveScale } from "./targetedReportAxis.ts";
import { CurrentPageHeading, useCurrentPageName } from "../../app/navigation/currentPageName.tsx";
import { TargetedRecordingControl } from "./TargetedRecordingControl.tsx";
import { StandardSelect, type StandardSelectOption } from "../../components/StandardSelect.tsx";
import { ContentState } from "../../ui/patterns/ContentState.tsx";
import type { ConfirmDialogRequest } from "../../components/AppFeedback";
import { exitOnUnmount } from "../../presentation/nodeAnimation.ts";

interface ResourceCurve { scope: "software" | "system"; metric: string }

const activeRecordingRefreshIntervalMs = 2_000;

export function TargetedReportsPage(props: {
  /** 应用统一的确认对话框（由路由层经详细信息页传入）。 */
  confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>;
}) {
  const runtime = useFrontendRuntime();
  const frontendWork = useFrontendWork();
  const demandId = "targeted-reports.page";
  useFrontendVisibilityDemand(demandId, [frontendWorkIds.targetedReportsPage]);
  const [recordingRefreshIntervalMs, setRecordingRefreshIntervalMs] =
    createSignal<number | null>(null);
  const recordingsSource = useSource(runtime.sources.targetedRecordings, () => ({
    active: frontendWork.isNeeded(frontendWorkIds.targetedReportsPage),
    refreshIntervalMs: recordingRefreshIntervalMs()
  }));
  const recordings = createMemo<RecordingHeader[]>(() =>
    recordingsSource.snapshot().data ?? []);
  // 列表的行按录制 id 保持：同一条录制刷新后还是同一个行节点，行只在录制出现、消失时进场、退场。
  // reconcile 会改它的目标，所以行列表是这里自己的一份，不改共享的来源快照。
  const [recordingRows, setRecordingRows] = createStore<RecordingHeader[]>([]);
  createComputed(() => setRecordingRows(reconcile(structuredClone(recordings()), { key: "id" })));
  const targetsSource = useSource(runtime.sources.targetedRecordingTargets, () => ({
    active: frontendWork.isNeeded(frontendWorkIds.targetedReportsPage), refreshIntervalMs: null
  }));
  const [targetId, setTargetId] = createSignal("");
  const [stoppingId, setStoppingId] = createSignal<string | null>(null);
  const [reportSource, setReportSource] =
    createSignal<SourceSnapshot<RecordingReport> | null>(null);
  const report = createMemo(() => reportSource()?.data ?? null);
  const [listLoading, setListLoading] = createSignal(false);
  const [reportLoading, setReportLoading] = createSignal(false);
  const loading = createMemo(() => listLoading() || reportLoading());
  const [listFailed, setListFailed] = createSignal(false);
  const [reportFailed, setReportFailed] = createSignal(false);
  const [actionFailed, setActionFailed] = createSignal(false);
  const failed = createMemo(() => listFailed() || reportFailed() || actionFailed());
  const [selectedId, setSelectedId] = createSignal<string | null>(null);
  const reportBinding = new SourceLeaseBinding<RecordingReport>(setReportSource);
  let reportLease: SourceLease<RecordingReport> | null = null;
  let finalReportRefreshId: string | null = null;
  onCleanup(() => reportBinding.release());

  createEffect(() => {
    const snapshot = recordingsSource.snapshot();
    setRecordingRefreshIntervalMs(snapshot.data?.some((item) => item.status === "recording")
      ? activeRecordingRefreshIntervalMs
      : null);
    if (snapshot.status === "error" || snapshot.status === "unavailable") {
      setListFailed(true);
      setListLoading(false);
    } else if (snapshot.status === "ready") {
      setListFailed(false);
      setListLoading(false);
    } else if (!snapshot.data) {
      setListLoading(true);
    }
  });

  createEffect(() => {
    const id = selectedId();
    const active = frontendWork.isNeeded(frontendWorkIds.targetedReportsPage);
    reportSource();
    const header = recordings().find((item) => item.id === id);

    if (!id) {
      reportBinding.release();
      reportLease = null;
      setReportSource(null);
      setReportLoading(false);
      setReportFailed(false);
      finalReportRefreshId = null;
      return;
    }

    reportLease = reportBinding.switch(id, () =>
      runtime.sources.targetedRecordingReport.acquire(
        { recordingId: id },
        { active: false, refreshIntervalMs: null }));
    const currentSnapshot = reportLease.snapshot;
    const activeRecording = header
      ? header.status === "recording"
      : currentSnapshot.data?.recording.status === "recording";
    const refreshIntervalMs = active && activeRecording
      ? activeRecordingRefreshIntervalMs
      : null;
    reportLease.setDemand({ active, refreshIntervalMs });

    if (!currentSnapshot.data) {
      setReportLoading(active && currentSnapshot.status === "loading");
      setReportFailed(currentSnapshot.status === "error"
        || currentSnapshot.status === "unavailable");
    } else {
      setReportLoading(false);
      setReportFailed(false);
    }

    if (header?.status === "completed"
        && currentSnapshot.data?.recording.status === "recording"
        && finalReportRefreshId !== id) {
      finalReportRefreshId = id;
      void reportLease.refresh().catch(() => setReportFailed(true));
    } else if (header?.status === "recording" && finalReportRefreshId === id) {
      finalReportRefreshId = null;
    }
  });

  const framePoints = createMemo<ChartPoint[]>(() =>
    report()?.frameIntervals.map((frame) => ({ x: Date.parse(frame.endedAt), y: frame.durationMs })) ?? []);
  const distributionPoints = createMemo<ChartPoint[]>(() =>
    report()?.fpsDistribution.map((bucket) => ({ x: bucket.fromFps, y: bucket.count })) ?? []);
  const resourceCurves = createMemo<ResourceCurve[]>(() => {
    const found = new Set<string>();
    for (const sample of report()?.resourceSamples ?? []) {
      for (const scope of ["software", "system"] as const) {
        for (const metric of Object.keys(sample[scope])) found.add(`${scope}:${metric}`);
      }
    }
    return [...found].sort().map((key) => {
      const separator = key.indexOf(":");
      return { scope: key.slice(0, separator) as ResourceCurve["scope"], metric: key.slice(separator + 1) };
    });
  });
  const start = () => Date.parse(report()?.recording.startedAt ?? "");
  const end = () => Date.parse(report()?.recording.endedAt ?? "")
    || Date.parse(report()?.resourceSamples.at(-1)?.capturedAt ?? "") || start() + 1000;

  const refresh = async () => {
    setActionFailed(false);
    setListLoading(true);
    try {
      await Promise.all([
        recordingsSource.refresh(),
        targetsSource.refresh(),
        reportLease?.refresh() ?? Promise.resolve(null)
      ]);
    } catch {
      setActionFailed(true);
    } finally {
      setListLoading(false);
    }
  };

  const select = (id: string) => {
    setSelectedId(id);
    setReportLoading(true);
    setReportFailed(false);
  };

  const remove = async (recording: RecordingHeader) => {
    const id = recording.id;
    const confirmed = await props.confirmDialog({
      title: uiText.targetedReport.confirmDelete,
      message: `${recording.softwareName} · ${dateTime(recording.startedAt)}`,
      details: [uiText.targetedReport.deleteDetail],
      tone: "warning",
      confirmLabel: uiText.targetedReport.delete
    });
    if (!confirmed) return;
    setActionFailed(false);
    try {
      await deleteRecording(runtime.requestClient, id);
      if (selectedId() === id) {
        setSelectedId(null);
        setReportLoading(false);
      }
      recordingsSource.acceptAuthoritative(recordings().filter((item) => item.id !== id));
      await recordingsSource.refresh();
    } catch {
      setActionFailed(true);
    }
  };

  const stop = async (id: string) => {
    if (stoppingId()) return;
    setStoppingId(id);
    setActionFailed(false);
    try {
      const completed = await stopRecording(runtime.requestClient, id);
      recordingsSource.acceptAuthoritative(recordings().map((item) => item.id === id ? completed : item));
      select(id);
      await Promise.all([recordingsSource.refresh(), reportLease?.refresh()]);
    } catch {
      setActionFailed(true);
    } finally {
      setStoppingId(null);
    }
  };

  const download = async (id: string, format: "json" | "csv") => {
    setActionFailed(false);
    try {
      const bytes = await exportRecording(runtime.requestClient, id, format);
      const url = URL.createObjectURL(new Blob([bytes], { type: format === "csv" ? "text/csv" : "application/json" }));
      const link = document.createElement("a");
      link.href = url;
      link.download = `targeted-recording-${id}.${format}`;
      link.click();
      window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch {
      setActionFailed(true);
    }
  };

  const pageName = useCurrentPageName();
  const targets = createMemo(() => targetsSource.snapshot().data ?? []);
  // 选中的软件不在当前列表里（例如刷新后被移除）时按“未选择”显示；记住的选择保留，软件回到列表后恢复。
  const selectedTarget = createMemo(() => targets().some((software) => software.id === targetId()) ? targetId() : "");
  const targetOptions = createMemo<StandardSelectOption[]>(() => [
    { value: "", label: uiText.targetedReport.chooseSoftware },
    ...targets().map((software) => ({ value: software.id, label: softwareRecordName(software) }))
  ]);
  const targetsFailed = () => targetsSource.snapshot().status === "error"
    || targetsSource.snapshot().status === "unavailable";
  const toolbarHint = () => {
    const snapshot = targetsSource.snapshot();
    if (snapshot.status === "loading" && !snapshot.data) {
      return uiText.observationNotice.loading(uiText.targetedReport.software);
    }
    return snapshot.status === "ready" && targets().length === 0 ? uiText.targetedReport.noSoftware : undefined;
  };

  return (
    <section
      class="targeted-reports-page"
      aria-label={pageName()}
      {...frontendVisibilitySurface("visible.targeted-reports.page", [demandId])}
    >
      <div class="panel-header targeted-report-header">
        <CurrentPageHeading />
        <div class="targeted-report-header-actions">
          <span class="targeted-report-busy" role="status">{loading() ? uiText.targetedReport.loading : ""}</span>
          <button class="secondary panel-refresh-button" type="button" onClick={() => void refresh()}>{uiText.targetedReport.refresh}</button>
        </div>
      </div>
      <p class="targeted-report-note">{uiText.targetedReport.workflow}</p>
      <TargetedRecordingControl layout="toolbar" softwareId={selectedTarget()} hint={toolbarHint()}
        active={frontendWork.isNeeded(frontendWorkIds.targetedReportsPage)}
        onStarted={(recording) => select(recording.id)}>
        <div class="targeted-recording-field targeted-recording-target">
          <span aria-hidden="true">{uiText.targetedReport.software}</span>
          <StandardSelect value={selectedTarget()} options={targetOptions()} ariaLabel={uiText.targetedReport.software}
            disabled={!sourceCanRender(targetsSource.snapshot())} onChange={setTargetId} />
        </div>
      </TargetedRecordingControl>
      <Show when={failed() || targetsFailed()}>
        <div class="targeted-report-note error observation-state" role="alert">
          <strong>{uiText.targetedReport.failed}</strong>
          <button class="secondary" type="button" onClick={() => void refresh()}>{uiText.observationNotice.retry}</button>
        </div>
      </Show>
      <section class="targeted-report-history" aria-label={uiText.targetedReport.history}>
        <h3>{uiText.targetedReport.history}</h3>
        {/* 读取失败由上方的提示负责（含重试），这里只说明列表暂不可用，不显示成“暂无录制”。 */}
        <Show when={recordings().length > 0} fallback={<Show when={!listFailed()}
          fallback={<p class="targeted-report-note">{uiText.observationNotice.unavailable(uiText.targetedReport.history)}</p>}>
          <ContentState kind={loading() ? "waiting" : "empty"}
            title={loading() ? uiText.targetedReport.loading : uiText.targetedReport.empty}
            detail={loading() ? undefined : uiText.targetedReport.emptyDetail} />
        </Show>}>
          <div class="targeted-report-list" role="list">
            <For each={recordingRows}>
              {(item) => (
                <div ref={(element) => exitOnUnmount(element)} class="targeted-report-list-row" role="listitem"
                  classList={{ active: selectedId() === item.id, recording: item.status === "recording" }}>
                  <button class="targeted-report-open" type="button" classList={{ active: selectedId() === item.id }}
                    aria-pressed={selectedId() === item.id} onClick={() => void select(item.id)}>
                    <strong>{item.softwareName}</strong>
                    <span>{dateTime(item.startedAt)} · {item.status === "recording"
                      ? uiText.targetedReport.recording : stopReason(item.stopReason)}</span>
                  </button>
                  <Show when={item.status === "recording"}>
                    <span class="targeted-report-row-meta">{uiText.targetedReport.maximumDurationValue(item.maximumDurationSeconds)}</span>
                  </Show>
                  {/* 停止、删除共用一格；隐藏的两段文字撑出较宽者的宽度，录制结束换成删除时按钮不变宽窄。 */}
                  <span class="targeted-report-row-action">
                    <Show when={item.status === "recording"}>
                      <button class="secondary panel-refresh-button" type="button" disabled={!!stoppingId()}
                        onClick={() => void stop(item.id)}>{uiText.targetedReport.stop}</button>
                    </Show>
                    <Show when={item.status === "completed"}>
                      <button class="secondary panel-refresh-button" type="button" onClick={() => void remove(item)}>
                        {uiText.targetedReport.delete}
                      </button>
                    </Show>
                    <span class="targeted-report-row-action-sizer" aria-hidden="true">
                      <span>{uiText.targetedReport.stop}</span>
                      <span>{uiText.targetedReport.delete}</span>
                    </span>
                  </span>
                </div>
              )}
            </For>
          </div>
        </Show>
      </section>
      <Show when={report()}>
        {(current) => (
          <div ref={(element) => exitOnUnmount(element)} class="targeted-report-detail">
            <div class="targeted-report-toolbar">
              <h3>{current().recording.softwareName} · {dateTime(current().recording.startedAt)}</h3>
              <div class="targeted-report-actions">
                <button class="secondary panel-refresh-button" type="button" onClick={() => void download(current().recording.id, "json")}>{uiText.targetedReport.exportJson}</button>
                <button class="secondary panel-refresh-button" type="button" onClick={() => void download(current().recording.id, "csv")}>{uiText.targetedReport.exportCsv}</button>
              </div>
            </div>
            <p class="targeted-report-note">{uiText.targetedReport.applicationPresent} · {current().recording.status === "recording"
              ? uiText.targetedReport.recording : stopReason(current().recording.stopReason)}</p>
            <Show when={current().recording.incomplete}><p class="targeted-report-note warning" role="status">{uiText.targetedReport.incomplete}</p></Show>
            <Show when={current().summary} fallback={<p class="targeted-report-note">{uiText.targetedReport.noFrames}</p>}>
              {(summary) => (
                <dl class="targeted-report-summary">
                  <For each={[
                    [uiText.targetedReport.averageFps, summary().averageFps, "FPS"],
                    [uiText.targetedReport.onePercentLow, summary().onePercentLowFps, "FPS"],
                    [uiText.targetedReport.pointOnePercentLow, summary().pointOnePercentLowFps, "FPS"],
                    [uiText.targetedReport.p50, summary().p50FrameTimeMs, "ms"],
                    [uiText.targetedReport.p95, summary().p95FrameTimeMs, "ms"],
                    [uiText.targetedReport.p99, summary().p99FrameTimeMs, "ms"],
                    [uiText.targetedReport.maxFrameTime, summary().maxFrameTimeMs, "ms"],
                    [uiText.targetedReport.frameCount, summary().intervalCount, ""]
                  ] as const}>
                    {([label, value, unit]) => <div><dt>{label}</dt><dd>{value.toFixed(unit ? 1 : 0)} {unit}</dd></div>}
                  </For>
                </dl>
              )}
            </Show>
            <Show when={framePoints().length > 0}>
              <div class="targeted-report-chart-grid">
                <section class="targeted-report-card">
                  <h4>{uiText.targetedReport.frameTime}</h4>
                  <TargetedReportChart points={framePoints()} from={start()} to={end()}
                    mode="frames" axis={{ unit: "ms", divisor: 1, integerTicks: false }} ariaLabel={uiText.targetedReport.frameTime} />
                </section>
                <section class="targeted-report-card">
                  <h4>{uiText.targetedReport.distribution}</h4>
                  <TargetedReportChart points={distributionPoints()}
                    from={distributionPoints()[0]?.x ?? 0}
                    to={(distributionPoints().at(-1)?.x ?? 0) + 10}
                    mode="bars" axis={{ unit: uiText.targetedReport.frameCount, divisor: 1, integerTicks: true }} ariaLabel={uiText.targetedReport.distribution} />
                </section>
              </div>
            </Show>
            <h4>{uiText.targetedReport.resources}</h4>
            <div class="targeted-report-resource-grid">
              <For each={resourceCurves()}>
                {(curve) => {
                  const points = () => current().resourceSamples.map((sample) => ({
                    x: Date.parse(sample.capturedAt), y: sample[curve.scope][curve.metric] ?? null
                  }));
                  const latest = () => current().resourceSamples.at(-1)?.[curve.scope][curve.metric] ?? null;
                  const scale = () => curveScale(curve.metric, points());
                  const label = () => `${curve.scope === "software" ? uiText.targetedReport.software : uiText.targetedReport.system} · ${localizedMetricLabel(curve.metric)}`;
                  return <section class="targeted-report-card">
                    <h5><span class="targeted-report-card-label">{label()}</span> <span class="targeted-report-card-value">{latest() === null ? "–" : scale().format(latest() as number)}</span></h5>
                    <TargetedReportChart points={points()} from={start()} to={end()} mode="line"
                      axis={scale().axis} ariaLabel={label()} />
                  </section>;
                }}
              </For>
            </div>
          </div>
        )}
      </Show>
    </section>
  );
}

function stopReason(reason: string | null) {
  const values = uiText.targetedReport.stopReasons;
  switch (reason) {
    case "user": return values.user;
    case "process-exit": return values.processExit;
    case "max-duration": return values.maxDuration;
    case "interrupted": return values.interrupted;
    case "capture-error": return values.captureError;
    default: return uiText.targetedReport.completed;
  }
}

function dateTime(value: string) {
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) ? new Intl.DateTimeFormat(currentLanguage(), {
    year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit"
  }).format(timestamp) : value;
}
