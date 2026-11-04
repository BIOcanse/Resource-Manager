import { createEffect, createMemo, createSignal, on, Show, type JSX } from "solid-js";
import type { ConfirmDialogRequest } from "../../components/AppFeedback";
import { StandardSelect } from "../../components/StandardSelect.tsx";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext.tsx";
import { sourceCanRender } from "../../frontendRuntime/source/SourceSnapshot.ts";
import { useSource } from "../../frontendRuntime/source/useSource.ts";
import { uiText } from "../../text.ts";
import { TargetedRecordingProfilesDialog } from "./profiles/TargetedRecordingProfilesDialog.tsx";
import {
  recordingErrorText, recordingFailure, startRecording, stopRecording, type RecordingHeader
} from "./targetedRecordingApi.ts";

const activeRecordingRefreshIntervalMs = 2_000;

/* Share recording behavior between section and toolbar layouts. Backend start validation is authoritative; refresh targets on activation or target-not-running, and recordings after an untyped 409. */
export function TargetedRecordingControl(props: {
  softwareId: string;
  active: boolean;
  onStarted?: (recording: RecordingHeader) => void;
  layout?: "section" | "toolbar";
  hint?: string;
  confirmDialog: (request: ConfirmDialogRequest) => Promise<boolean>;
  children?: JSX.Element;
}) {
  const runtime = useFrontendRuntime();
  const toolbar = () => props.layout === "toolbar";
  const [refreshIntervalMs, setRefreshIntervalMs] = createSignal<number | null>(null);
  const recordingsSource = useSource(runtime.sources.targetedRecordings, () => ({
    active: props.active,
    refreshIntervalMs: refreshIntervalMs()
  }));
  const targetsSource = useSource(runtime.sources.targetedRecordingTargets, () => ({ active: props.active }));
  const profilesSource = useSource(runtime.sources.targetedRecordingProfiles, () => ({ active: props.active }));
  const profiles = createMemo(() => profilesSource.snapshot().data ?? []);
  const [chosenProfileId, setChosenProfileId] = createSignal("");
  // Fall back visibly to the first profile when the saved selection is absent.
  const profileId = createMemo(() =>
    profiles().some((item) => item.id === chosenProfileId()) ? chosenProfileId() : profiles()[0]?.id ?? "");
  const profilesFailed = createMemo(() => {
    const status = profilesSource.snapshot().status;
    return status === "error" || status === "unavailable";
  });
  const [managing, setManaging] = createSignal(false);
  const [maximumSeconds, setMaximumSeconds] = createSignal(600);
  const active = createMemo(() => recordingsSource.snapshot().data
    ?.find((item) => item.softwareId === props.softwareId && item.status === "recording")
    ?? null);
  const [busy, setBusy] = createSignal(false);
  const [actionFailed, setActionFailed] = createSignal(false);
  // Use a specific start or stop failure explanation when available.
  const [failureText, setFailureText] = createSignal<string | null>(null);
  const listFailed = createMemo(() => {
    const status = recordingsSource.snapshot().status;
    return status === "error" || status === "unavailable";
  });
  const failed = () => actionFailed() || profilesFailed() || (!toolbar() && listFailed());
  const otherActive = createMemo(() => recordingsSource.snapshot().data
    ?.find((item) => item.softwareId !== props.softwareId && item.status === "recording"));

  createEffect(() => {
    const snapshot = recordingsSource.snapshot();
    setRefreshIntervalMs(active() || otherActive()
      ? activeRecordingRefreshIntervalMs
      : null);
    if (sourceCanRender(snapshot)) {
      setActionFailed(false);
      setFailureText(null);
    }
  });

  // Refresh targets on activation because process state may have changed.
  createEffect(on(() => props.active, (active) => {
    if (active) void targetsSource.refresh().catch(() => undefined);
  }));

  const fail = (text: string | null) => {
    setFailureText(text);
    setActionFailed(true);
  };

  const start = async () => {
    if (busy()) return;
    setBusy(true);
    setActionFailed(false);
    setFailureText(null);
    try {
      const seconds = maximumSeconds();
      if (!Number.isInteger(seconds) || seconds < 1 || seconds > 3600) {
        fail(copy().durationInvalid);
        return;
      }
      const recording = await startRecording(runtime.requestClient,
        { softwareId: props.softwareId, profileId: profileId(), maximumDurationSeconds: seconds });
      recordingsSource.acceptAuthoritative(upsertRecording(
        recordingsSource.snapshot().data ?? [],
        recording));
      await recordingsSource.refresh();
      props.onStarted?.(recording);
    } catch (error) {
      const failure = recordingFailure(error);
      fail(recordingErrorText(failure, "start"));
      if (failure.code === "target-not-running") void targetsSource.refresh().catch(() => undefined);
      // An empty-body 409 may mean another recording is active; refresh the list.
      if (failure.status === 409 && failure.code === null) void recordingsSource.refresh().catch(() => undefined);
      if (failure.code === "targeted-recording-profile-not-found") void profilesSource.refresh().catch(() => undefined);
    } finally {
      setBusy(false);
    }
  };

  const stop = async () => {
    const recording = active();
    if (!recording || busy()) return;
    setBusy(true);
    setActionFailed(false);
    setFailureText(null);
    try {
      const completed = await stopRecording(runtime.requestClient, recording.id);
      recordingsSource.acceptAuthoritative(upsertRecording(
        recordingsSource.snapshot().data ?? [],
        completed));
      await recordingsSource.refresh();
    } catch (error) {
      fail(recordingErrorText(recordingFailure(error), "other"));
    } finally {
      setBusy(false);
    }
  };

  const retry = () => {
    setActionFailed(false);
    setFailureText(null);
    void Promise.all([recordingsSource.refresh(), profilesSource.refresh(), targetsSource.refresh()])
      .catch(() => setActionFailed(true));
  };

  const copy = () => uiText.targetedReport;
  const idleHint = () => toolbar() ? props.hint ?? copy().intro : undefined;

  const body = (
    <div class="targeted-recording-actions">
      <div class="targeted-recording-fields">
        {props.children}
        <div class="targeted-recording-field targeted-recording-profile">
          <span aria-hidden="true">{copy().profile}</span>
          <div class="targeted-recording-profile-row">
            <StandardSelect value={profileId()} ariaLabel={copy().profile}
              options={profiles().length > 0
                ? profiles().map((item) => ({ value: item.id, label: item.name }))
                : [{ value: "", label: profilesSource.snapshot().data === null ? copy().profilesLoading : copy().noProfile }]}
              disabled={!!active() || profiles().length === 0} onChange={setChosenProfileId} />
            <button class="secondary" type="button" disabled={profilesSource.snapshot().data === null}
              onClick={() => setManaging(true)}>{copy().manageProfiles}</button>
          </div>
        </div>
        <label class="targeted-recording-field targeted-recording-duration">
          <span>{copy().maximumSeconds}</span>
          <input type="number" min="1" max="3600" step="1"
            value={active()?.maximumDurationSeconds ?? maximumSeconds()}
            disabled={!!active()}
            aria-invalid={!Number.isInteger(maximumSeconds()) || maximumSeconds() < 1 || maximumSeconds() > 3600}
            onInput={(event) => setMaximumSeconds(Number(event.currentTarget.value))} />
        </label>
        {/* Reserve the wider start/stop label width to avoid button resizing. */}
        <span class="targeted-recording-action">
          <Show when={active()} fallback={
            <button type="button"
              disabled={busy() || !props.softwareId || !profileId() || !!otherActive() || !sourceCanRender(recordingsSource.snapshot())}
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
            <Show when={profilesSource.snapshot().data !== null && profiles().length === 0} fallback={
              <Show when={idleHint()}>{(hint) => <p class="targeted-recording-hint">{hint()}</p>}</Show>
            }>
              <p class="targeted-recording-notice" role="status">{copy().noProfile}</p>
            </Show>
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
          <strong>{failureText() ?? copy().failed}</strong>
          <button class="secondary" type="button" disabled={busy()} onClick={retry}>
            {uiText.observationNotice.retry}
          </button>
        </div>
      </Show>
      <TargetedRecordingProfilesDialog open={managing()} initialProfileId={profileId() || null}
        confirmDialog={props.confirmDialog} onClose={() => setManaging(false)} />
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
