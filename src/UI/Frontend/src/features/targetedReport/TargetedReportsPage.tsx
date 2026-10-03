import { createMemo, createSignal, For, onMount, Show } from "solid-js";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext.tsx";
import { formatBytes, formatBytesPerSecond } from "../../presentation/byteUnits.ts";
import { localizedMetricLabel } from "../../presentation/metricLabels.ts";
import { uiText } from "../../text.ts";
import {
  deleteRecording, exportRecording, getRecording, listRecordings,
  type RecordingHeader, type RecordingReport
} from "./targetedRecordingApi.ts";
import { TargetedReportChart, type ChartPoint } from "./TargetedReportCharts.tsx";

interface ResourceCurve { scope: "software" | "system"; metric: string }

export function TargetedReportsPage() {
  const client = useFrontendRuntime().requestClient;
  const [recordings, setRecordings] = createSignal<RecordingHeader[]>([]);
  const [report, setReport] = createSignal<RecordingReport | null>(null);
  const [loading, setLoading] = createSignal(false);
  const [failed, setFailed] = createSignal(false);
  const [selectedId, setSelectedId] = createSignal<string | null>(null);
  let selectionVersion = 0;
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
    setFailed(false);
    try {
      setRecordings(await listRecordings(client));
    } catch {
      setFailed(true);
    }
  };
  onMount(() => { void refresh(); });

  const select = async (id: string) => {
    const version = ++selectionVersion;
    setSelectedId(id);
    setReport(null);
    setLoading(true);
    setFailed(false);
    try {
      const next = await getRecording(client, id);
      if (version === selectionVersion) setReport(next);
    } catch {
      if (version === selectionVersion) setFailed(true);
    } finally {
      if (version === selectionVersion) setLoading(false);
    }
  };

  const remove = async (id: string) => {
    if (!window.confirm(uiText.targetedReport.confirmDelete)) return;
    setFailed(false);
    try {
      await deleteRecording(client, id);
      if (selectedId() === id) {
        selectionVersion++;
        setSelectedId(null);
        setReport(null);
        setLoading(false);
      }
      await refresh();
    } catch {
      setFailed(true);
    }
  };

  const download = async (id: string, format: "json" | "csv") => {
    setFailed(false);
    try {
      const bytes = await exportRecording(client, id, format);
      const url = URL.createObjectURL(new Blob([bytes], { type: format === "csv" ? "text/csv" : "application/json" }));
      const link = document.createElement("a");
      link.href = url;
      link.download = `targeted-recording-${id}.${format}`;
      link.click();
      window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch {
      setFailed(true);
    }
  };

  return (
    <section class="targeted-reports-page" aria-label={uiText.targetedReport.title}>
      <div class="targeted-report-toolbar">
        <h2>{uiText.targetedReport.title}</h2>
        <button type="button" onClick={() => void refresh()}>{uiText.targetedReport.refresh}</button>
      </div>
      <Show when={failed()}><p role="alert">{uiText.targetedReport.failed}</p></Show>
      <Show when={recordings().length > 0} fallback={<p>{uiText.targetedReport.empty}</p>}>
        <div class="targeted-report-list" role="list">
          <For each={recordings()}>
            {(item) => (
              <div class="targeted-report-list-row" role="listitem">
                <button type="button" classList={{ active: selectedId() === item.id }}
                  onClick={() => void select(item.id)}>
                  <strong>{item.softwareName}</strong>
                  <span>{dateTime(item.startedAt)} · {item.status === "recording"
                    ? uiText.targetedReport.recording : stopReason(item.stopReason)}</span>
                </button>
                <Show when={item.status === "completed"}>
                  <button class="secondary" type="button" onClick={() => void remove(item.id)}>
                    {uiText.targetedReport.delete}
                  </button>
                </Show>
              </div>
            )}
          </For>
        </div>
      </Show>
      <Show when={loading()}><p role="status">{uiText.targetedReport.loading}</p></Show>
      <Show when={report()}>
        {(current) => (
          <div class="targeted-report-detail">
            <div class="targeted-report-toolbar">
              <h3>{current().recording.softwareName} · {dateTime(current().recording.startedAt)}</h3>
              <div class="targeted-report-actions">
                <button type="button" onClick={() => void download(current().recording.id, "json")}>{uiText.targetedReport.exportJson}</button>
                <button type="button" onClick={() => void download(current().recording.id, "csv")}>{uiText.targetedReport.exportCsv}</button>
              </div>
            </div>
            <p>{uiText.targetedReport.applicationPresent} · {current().recording.status === "recording"
              ? uiText.targetedReport.recording : stopReason(current().recording.stopReason)}</p>
            <Show when={current().recording.incomplete}><p role="status">{uiText.targetedReport.incomplete}</p></Show>
            <Show when={current().summary} fallback={<p>{uiText.targetedReport.noFrames}</p>}>
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
              <section class="targeted-report-card">
                <h4>{uiText.targetedReport.frameTime}</h4>
                <TargetedReportChart points={framePoints()} from={start()} to={end()}
                  mode="frames" unit="ms" ariaLabel={uiText.targetedReport.frameTime} />
              </section>
              <section class="targeted-report-card">
                <h4>{uiText.targetedReport.distribution}</h4>
                <TargetedReportChart points={distributionPoints()}
                  from={distributionPoints()[0]?.x ?? 0}
                  to={(distributionPoints().at(-1)?.x ?? 0) + 10}
                  mode="bars" unit="FPS" ariaLabel={uiText.targetedReport.distribution} />
              </section>
            </Show>
            <h4>{uiText.targetedReport.resources}</h4>
            <div class="targeted-report-resource-grid">
              <For each={resourceCurves()}>
                {(curve) => {
                  const points = () => current().resourceSamples.map((sample) => ({
                    x: Date.parse(sample.capturedAt), y: sample[curve.scope][curve.metric] ?? null
                  }));
                  const latest = () => current().resourceSamples.at(-1)?.[curve.scope][curve.metric] ?? null;
                  const label = () => `${curve.scope === "software" ? uiText.targetedReport.software : uiText.targetedReport.system} · ${localizedMetricLabel(curve.metric)}`;
                  return <section class="targeted-report-card">
                    <h5>{label()} <span>{displayMetric(curve.metric, latest())}</span></h5>
                    <TargetedReportChart points={points()} from={start()} to={end()} mode="line"
                      unit={metricUnit(curve.metric)} ariaLabel={label()} />
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
  return Number.isFinite(timestamp) ? new Intl.DateTimeFormat(undefined, {
    year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit"
  }).format(timestamp) : value;
}

function metricUnit(id: string) {
  if (/^(disk\.|network\.)/.test(id)) return "B/s";
  if (/^(memory\.|virtualMemory\.)/.test(id) || /\.vram$/.test(id)) return "B";
  if (/temperature$/.test(id)) return "°C";
  if (/frequency$/.test(id)) return "MHz";
  return "%";
}

function displayMetric(id: string, value: number | null) {
  if (value === null) return "–";
  const unit = metricUnit(id);
  if (unit === "B") return formatBytes(value, "memory");
  if (unit === "B/s") return formatBytesPerSecond(value);
  return `${value.toFixed(1)} ${unit}`;
}
