import { createEffect, createSignal, onCleanup, Show } from "solid-js";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext.tsx";
import { uiText } from "../../text.ts";
import { listRecordings, startRecording, stopRecording, type RecordingHeader } from "./targetedRecordingApi.ts";

export function TargetedRecordingControl(props: { softwareId: string }) {
  const client = useFrontendRuntime().requestClient;
  const [maximumSeconds, setMaximumSeconds] = createSignal(600);
  const [active, setActive] = createSignal<RecordingHeader | null>(null);
  const [busy, setBusy] = createSignal(false);
  const [failed, setFailed] = createSignal(false);

  const refresh = async (softwareId: string, signal?: AbortSignal) => {
    const records = await listRecordings(client, signal);
    setActive(records.find((item) => item.softwareId === softwareId && item.status === "recording") ?? null);
  };

  createEffect(() => {
    const softwareId = props.softwareId;
    setActive(null);
    setFailed(false);
    const cancellation = new AbortController();
    void refresh(softwareId, cancellation.signal).catch(() => {
      if (!cancellation.signal.aborted) setFailed(true);
    });
    onCleanup(() => cancellation.abort());
  });

  createEffect(() => {
    if (!active()) return;
    const timer = window.setInterval(() => {
      void refresh(props.softwareId).catch(() => setFailed(true));
    }, 2000);
    onCleanup(() => window.clearInterval(timer));
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
      setActive(await startRecording(client, props.softwareId, seconds));
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
      await stopRecording(client, recording.id);
      setActive(null);
    } catch {
      setFailed(true);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section class="software-detail-section targeted-recording-control">
      <h3>{uiText.targetedReport.title}</h3>
      <p>{uiText.targetedReport.intro}</p>
      <Show when={active()} fallback={
        <div class="targeted-recording-actions">
          <label>
            {uiText.targetedReport.maximumSeconds}
            <input type="number" min="1" max="3600" step="1" value={maximumSeconds()}
              onInput={(event) => setMaximumSeconds(Number(event.currentTarget.value))} />
          </label>
          <button type="button" disabled={busy()} onClick={() => void start()}>{uiText.targetedReport.start}</button>
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
