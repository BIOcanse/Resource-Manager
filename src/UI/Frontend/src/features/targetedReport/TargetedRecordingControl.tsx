import { createEffect, createMemo, createSignal, Show } from "solid-js";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext.tsx";
import { sourceCanRender } from "../../frontendRuntime/source/SourceSnapshot.ts";
import { useSource } from "../../frontendRuntime/source/useSource.ts";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext.tsx";
import { uiText } from "../../text.ts";
import { startRecording, stopRecording } from "./targetedRecordingApi.ts";

const activeRecordingRefreshIntervalMs = 2_000;

export function TargetedRecordingControl(props: { softwareId: string }) {
  const runtime = useFrontendRuntime();
  const work = useFrontendWork();
  const [refreshIntervalMs, setRefreshIntervalMs] = createSignal<number | null>(null);
  const recordingsSource = useSource(runtime.sources.targetedRecordings, () => ({
    active: work.frontendVisible(),
    refreshIntervalMs: refreshIntervalMs()
  }));
  const [maximumSeconds, setMaximumSeconds] = createSignal(600);
  const active = createMemo(() => recordingsSource.snapshot().data
    ?.find((item) => item.softwareId === props.softwareId && item.status === "recording")
    ?? null);
  const [busy, setBusy] = createSignal(false);
  const [failed, setFailed] = createSignal(false);

  createEffect(() => {
    const snapshot = recordingsSource.snapshot();
    setRefreshIntervalMs(active()
      ? activeRecordingRefreshIntervalMs
      : null);
    if (snapshot.status === "error" || snapshot.status === "unavailable") {
      setFailed(true);
    } else if (sourceCanRender(snapshot)) {
      setFailed(false);
    }
  });

  const start = async () => {
    if (busy()) return;
    setBusy(true);
    setFailed(false);
    try {
      const seconds = Math.floor(maximumSeconds());
      if (!Number.isFinite(seconds) || seconds < 1 || seconds > 3600) {
        setFailed(true);
        return;
      }
      const recording = await startRecording(runtime.requestClient, props.softwareId, seconds);
      recordingsSource.acceptAuthoritative(upsertRecording(
        recordingsSource.snapshot().data ?? [],
        recording));
      await recordingsSource.refresh();
    } catch {
      setFailed(true);
    } finally {
      setBusy(false);
    }
  };

  const stop = async () => {
    const recording = active();
    if (!recording || busy()) return;
    setBusy(true);
    setFailed(false);
    try {
      const completed = await stopRecording(runtime.requestClient, recording.id);
      recordingsSource.acceptAuthoritative(upsertRecording(
        recordingsSource.snapshot().data ?? [],
        completed));
      await recordingsSource.refresh();
    } catch {
      setFailed(true);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section
      class="software-detail-section targeted-recording-control"
    >
      <h3>{uiText.targetedReport.title}</h3>
      <p>{uiText.targetedReport.intro}</p>
      <Show when={active()} fallback={
        <div class="targeted-recording-actions">
          <label>
            {uiText.targetedReport.maximumSeconds}
            <input type="number" min="1" max="3600" step="1" value={maximumSeconds()}
              onInput={(event) => setMaximumSeconds(Number(event.currentTarget.value))} />
          </label>
          <button type="button" disabled={busy() || !sourceCanRender(recordingsSource.snapshot())} onClick={() => void start()}>{uiText.targetedReport.start}</button>
        </div>
      }>
        {(recording) => (
          <div class="targeted-recording-actions">
            <span role="status">{uiText.targetedReport.recording} · {recording().maximumDurationSeconds} {uiText.targetedReport.seconds}</span>
            <button type="button" disabled={busy()} onClick={() => void stop()}>{uiText.targetedReport.stop}</button>
          </div>
        )}
      </Show>
      <Show when={failed()}><p role="alert">{uiText.targetedReport.failed}</p></Show>
    </section>
  );
}

function upsertRecording<T extends { id: string }>(items: T[], incoming: T): T[] {
  const index = items.findIndex((item) => item.id === incoming.id);
  if (index < 0) return [incoming, ...items];
  return items.map((item, itemIndex) => itemIndex === index ? incoming : item);
}
