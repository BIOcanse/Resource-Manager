import { createEffect, createMemo, createSignal, Show, type JSX } from "solid-js";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext.tsx";
import { sourceCanRender } from "../../frontendRuntime/source/SourceSnapshot.ts";
import { useSource } from "../../frontendRuntime/source/useSource.ts";
import { useFrontendWork } from "../../frontendWork/FrontendWorkContext.tsx";
import { uiText } from "../../text.ts";
import { startRecording, stopRecording, type RecordingHeader } from "./targetedRecordingApi.ts";

const activeRecordingRefreshIntervalMs = 2_000;

/*
 * 两种排版，行为相同：
 * - section（默认）：软件详情里的一节，带标题和说明，读取失败和操作失败都在这里提示。
 * - toolbar：定向报告页顶部的录制工具条。children 是排在最前面的字段（页面的软件选择）；
 *   没有录制状态要说时，状态行显示 hint（省略时显示 intro）。列表读取失败由报告页统一提示，
 *   这里只提示开始、停止本身的失败。
 * softwareId 为空表示还没有选软件：字段照常显示，开始按钮不可用。
 * active 是录制列表是否要读：所在页面按自己的可见区域给出；省略时跟随窗口是否可见（软件详情对话框）。
 */
export function TargetedRecordingControl(props: {
  softwareId: string;
  active?: boolean;
  onStarted?: (recording: RecordingHeader) => void;
  layout?: "section" | "toolbar";
  hint?: string;
  children?: JSX.Element;
}) {
  const runtime = useFrontendRuntime();
  const work = useFrontendWork();
  const toolbar = () => props.layout === "toolbar";
  const [refreshIntervalMs, setRefreshIntervalMs] = createSignal<number | null>(null);
  const recordingsSource = useSource(runtime.sources.targetedRecordings, () => ({
    active: props.active ?? work.frontendVisible(),
    refreshIntervalMs: refreshIntervalMs()
  }));
  const [maximumSeconds, setMaximumSeconds] = createSignal(600);
  const active = createMemo(() => recordingsSource.snapshot().data
    ?.find((item) => item.softwareId === props.softwareId && item.status === "recording")
    ?? null);
  const [busy, setBusy] = createSignal(false);
  const [actionFailed, setActionFailed] = createSignal(false);
  const listFailed = createMemo(() => {
    const status = recordingsSource.snapshot().status;
    return status === "error" || status === "unavailable";
  });
  const failed = () => actionFailed() || (!toolbar() && listFailed());
  const otherActive = createMemo(() => recordingsSource.snapshot().data
    ?.find((item) => item.softwareId !== props.softwareId && item.status === "recording"));

  createEffect(() => {
    const snapshot = recordingsSource.snapshot();
    setRefreshIntervalMs(active() || otherActive()
      ? activeRecordingRefreshIntervalMs
      : null);
    if (sourceCanRender(snapshot)) {
      setActionFailed(false);
    }
  });

  const start = async () => {
    if (busy()) return;
    setBusy(true);
    setActionFailed(false);
    try {
      const seconds = Math.floor(maximumSeconds());
      if (!Number.isFinite(seconds) || seconds < 1 || seconds > 3600) {
        setActionFailed(true);
        return;
      }
      const recording = await startRecording(runtime.requestClient, props.softwareId, seconds);
      recordingsSource.acceptAuthoritative(upsertRecording(
        recordingsSource.snapshot().data ?? [],
        recording));
      await recordingsSource.refresh();
      props.onStarted?.(recording);
    } catch {
      setActionFailed(true);
    } finally {
      setBusy(false);
    }
  };

  const stop = async () => {
    const recording = active();
    if (!recording || busy()) return;
    setBusy(true);
    setActionFailed(false);
    try {
      const completed = await stopRecording(runtime.requestClient, recording.id);
      recordingsSource.acceptAuthoritative(upsertRecording(
        recordingsSource.snapshot().data ?? [],
        completed));
      await recordingsSource.refresh();
    } catch {
      setActionFailed(true);
    } finally {
      setBusy(false);
    }
  };

  const copy = () => uiText.targetedReport;
  const idleHint = () => toolbar() ? props.hint ?? copy().intro : undefined;

  const body = (
    <div class="targeted-recording-actions">
      <div class="targeted-recording-fields">
        {props.children}
        <label class="targeted-recording-field targeted-recording-duration">
          <span>{copy().maximumSeconds}</span>
          <input type="number" min="1" max="3600" step="1"
            value={active()?.maximumDurationSeconds ?? maximumSeconds()}
            disabled={!!active()}
            onInput={(event) => setMaximumSeconds(Number(event.currentTarget.value))} />
        </label>
        {/* 开始、停止共用一格；隐藏的两段文字撑出较宽者的宽度，切换时按钮不变宽窄。 */}
        <span class="targeted-recording-action">
          <Show when={active()} fallback={
            <button type="button"
              disabled={busy() || !props.softwareId || !!otherActive() || !sourceCanRender(recordingsSource.snapshot())}
              onClick={() => void start()}>{copy().start}</button>
          }>
            <button type="button" disabled={busy()} onClick={() => void stop()}>{copy().stop}</button>
          </Show>
          <span class="targeted-recording-action-sizer" aria-hidden="true">
            <span>{copy().start}</span>
            <span>{copy().stop}</span>
          </span>
        </span>
      </div>
      <div class="targeted-recording-status">
        <Show when={active()} fallback={
          <Show when={otherActive()} fallback={
            <Show when={idleHint()}>{(hint) => <p class="targeted-recording-hint">{hint()}</p>}</Show>
          }>
            {(recording) => <p class="targeted-recording-notice" role="status">
              {copy().oneAtATime} <strong>{copy().heldBy(recording().softwareName)}</strong>
            </p>}
          </Show>
        }>
          {(recording) => <p class="targeted-recording-live" role="status">
            <span class="targeted-recording-live-dot" aria-hidden="true" />
            <strong>{copy().recording}</strong>
            <span>· {copy().maximumDurationValue(recording().maximumDurationSeconds)}</span>
          </p>}
        </Show>
      </div>
      <Show when={failed()}>
        <div class="targeted-recording-alert observation-state error" role="alert">
          <strong>{copy().failed}</strong>
          <button class="secondary" type="button" disabled={busy()}
            onClick={() => void recordingsSource.refresh().catch(() => setActionFailed(true))}>
            {uiText.observationNotice.retry}
          </button>
        </div>
      </Show>
    </div>
  );

  return (
    <Show when={toolbar()} fallback={
      <section class="software-detail-section targeted-recording-control">
        <h3>{copy().title}</h3>
        <p>{copy().intro}</p>
        {body}
      </section>
    }>
      <div class="targeted-recording-control targeted-recording-toolbar">{body}</div>
    </Show>
  );
}

function upsertRecording<T extends { id: string }>(items: T[], incoming: T): T[] {
  const index = items.findIndex((item) => item.id === incoming.id);
  if (index < 0) return [incoming, ...items];
  return items.map((item, itemIndex) => itemIndex === index ? incoming : item);
}
