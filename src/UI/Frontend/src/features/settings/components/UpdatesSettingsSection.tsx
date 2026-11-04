import { StableLabel } from "../../../ui/primitives/StableLabel.tsx";
import { Show, createSignal } from "solid-js";
import { postJson } from "../../../api";
import { useFrontendRuntime } from "../../../frontendRuntime/FrontendRuntimeContext";
import { useFrontendWork } from "../../../frontendWork/FrontendWorkContext";
import { useFrontendVisibilityDemand } from "../../../frontendWork/useFrontendVisibilityDemand";
import { frontendVisibilitySurface } from "../../../frontendWork/frontendVisibilitySurface";
import { frontendWorkIds } from "../../../frontendWork/frontendWorkIds";
import { useSource } from "../../../frontendRuntime/source/useSource.ts";
import { sourceCanRender } from "../../../frontendRuntime/source/SourceSnapshot.ts";
import type { ProductVersionCatalog } from "../../../data/settingsReadSources.ts";
import { ObservationStateNotice } from "../../../components/ObservationStateNotice";
import { failedObservation, loadingObservation } from "../../../observation/observationState";
import type { AppUpdateMode, AppUpdateSettings } from "../../../types";
import type { SettingsTextBundle } from "../../../i18n/settingsTypes.ts";
import { VersionSelector } from "./VersionSelector";
import { isUpdateBusy, productUpdateCopy, updateFailure, type ProductUpdateState } from "../productUpdatePresentation.ts";
import type { ProductUpdateStore } from "../../../stores/productUpdateStore.ts";
import { updateCheckIntervalHours } from "../../../stores/settingsStore.ts";
import { formatUtcTime } from "../../../data/productUpdateStatus.ts";
import { formatElapsedDuration } from "../../../presentation/elapsedDuration.ts";
import { NumberField, SegmentedControl } from "./SettingsControls";
import { SwitchControl } from "../../../ui/primitives/Switch.tsx";
import { HelpHint } from "../../../ui/primitives/HelpHint.tsx";

export function UpdatesSettingsSection(props: {
  settings: AppUpdateSettings | undefined;
  /** Use saved settings to gate next-check and connected-time rows. */
  committedSettings: AppUpdateSettings | undefined;
  productUpdates: ProductUpdateStore;
  text: SettingsTextBundle;
  onModeChange: (mode: AppUpdateMode) => void;
  onPeriodicCheckChange: (enabled: boolean) => void;
  onCheckIntervalChange: (hours: number) => void;
}) {
  const runtime = useFrontendRuntime();
  const frontendWork = useFrontendWork();
  // Visibility owns version-catalog reads; release while hidden and reread on return.
  const workId = frontendWorkIds.settingsUpdates;
  useFrontendVisibilityDemand(workId, [workId]);
  const source = useSource(runtime.sources.settingsReads.productVersions,
    () => ({ active: frontendWork.isNeeded(workId), refreshIntervalMs: null }));
  const catalog = (): ProductVersionCatalog | null => {
    const snapshot = source.snapshot();
    if (sourceCanRender(snapshot)) return snapshot.data;
    if (snapshot.status !== "error" && snapshot.status !== "unavailable") return null;
    return { status: "error", complete: false, checkedAt: "", options: [], errorCode: "update.catalogUnavailable" };
  };
  const [refreshPending, setRefreshPending] = createSignal(false);
  const loading = () => refreshPending() || source.snapshot().status === "loading";
  const [selected, setSelected] = createSignal<string | null>(null);
  const [preparePending, setPreparePending] = createSignal(false);
  const text = () => props.text.updates;
  const copy = () => productUpdateCopy(props.text.language);
  const selectedOption = () => catalog()?.options.find((option) => option.choice === selected());
  // Share backend-pushed update state without polling.
  const updateState = props.productUpdates.status;
  const statusFailed = props.productUpdates.statusFailed;
  const actionPending = () => preparePending() || props.productUpdates.applyPending();
  const mode = () => props.settings?.mode ?? "automatic";
  const automaticOff = () => mode() === "off";
  const committedPeriodic = () => props.committedSettings?.mode !== "off" && props.committedSettings?.periodicCheckEnabled === true;
  const waitingForInternet = () => updateState()?.automaticCheckStatus === "waitingForInternet";
  // Show stages only when work is active or needs attention.
  const stageLine = (): string | null => {
    const state = updateState();
    if (!state) return null;
    if (state.stage === "waitingForExit") return text().waitingForExit;
    if (state.errorCode) return copy().error(state.errorCode, state.errorParameters);
    if (state.stage === "pendingApply") return copy().pending;
    const stage = Object.hasOwn(text().stages, state.stage) ? text().stages[state.stage] : null;
    return stage ? `${text().stageLabel}: ${stage}` : null;
  };

  async function refresh() {
    setRefreshPending(true);
    try {
      await source.refresh();
    } finally {
      setRefreshPending(false);
    }
  }

  async function prepareUpdate() {
    const option = selectedOption();
    if (!option?.selectable || actionPending() || statusFailed() || updateState()?.stage === "pendingApply" || isUpdateBusy(updateState()?.stage)) return;
    const approved = window.confirm(text().confirmPrepare(option.version));
    if (!approved) return;
    setPreparePending(true);
    try {
      props.productUpdates.accept(await postJson<ProductUpdateState>("/api/updates/product/prepare",
        { choice: option.choice }, text().prepareFailed));
    } catch (error) {
      props.productUpdates.accept(updateFailure(error, "update.preparationFailed"));
    } finally { setPreparePending(false); }
  }

  return <div class="settings-section-panel" {...frontendVisibilitySurface(workId, [workId])}>
    <div class="settings-row">
      <div class="settings-row-copy">
        <div class="settings-row-title">
          <strong>{text().modeTitle}</strong>
          <HelpHint label={text().modeTitle}>{text().modeDescription}</HelpHint>
        </div>
      </div>
      <SegmentedControl value={mode()} options={text().modeOptions} ariaLabel={text().modeTitle}
        onChange={props.onModeChange} />
    </div>
    <div class="settings-row">
      <div class="settings-row-copy">
        <div class="settings-row-title">
          <strong>{text().periodicTitle}</strong>
          <HelpHint label={text().periodicTitle}><p>{text().periodicDescription}</p><p>{text().internetNote}</p></HelpHint>
        </div>
        <Show when={automaticOff()}><span role="note">{text().offReason}</span></Show>
      </div>
      <div class="settings-update-interval-control">
        <SwitchControl ariaLabel={text().periodicTitle} checked={props.settings?.periodicCheckEnabled === true}
          disabled={automaticOff()}
          onChange={props.onPeriodicCheckChange} />
        <NumberField label={text().intervalLabel(updateCheckIntervalHours.minimum, updateCheckIntervalHours.maximum)}
          ariaLabel={text().intervalTitle}
          value={props.settings?.checkIntervalHours ?? updateCheckIntervalHours.defaultValue}
          min={updateCheckIntervalHours.minimum} max={updateCheckIntervalHours.maximum} step={1}
          disabled={automaticOff()} onChange={props.onCheckIntervalChange} />
      </div>
    </div>
    <div class="settings-row">
      <div class="settings-row-copy">
        <div class="settings-row-title">
          <strong>{text().installedVersionTitle}</strong>
          <HelpHint label={text().installedVersionTitle}>{text().manualCheckNote}</HelpHint>
        </div>
        <span>{catalog()?.installedVersion ?? (text().unknownVersion)}</span>
      </div>
      <button type="button" class="secondary" disabled={loading()} onClick={() => void refresh()}>
        <StableLabel labels={{ idle: text().checkForUpdates, busy: text().checking }} current={loading() ? "busy" : "idle"} />
      </button>
    </div>
    <Show when={catalog()}>{(result) => <>
      <Show when={result().status === "error"}>
        <ObservationStateNotice
          state={failedObservation(loadingObservation(), copy().error(result().errorCode ?? "update.catalogUnavailable", result().errorParameters))}
          label={text().versionsTitle}
        />
      </Show>
      <Show when={result().status === "partial"}>
        <p role="status">{text().historyIncomplete}</p>
      </Show>
      <Show when={result().status === "stale"}>
        <p role="status">{text().historyStale}</p>
      </Show>
      <Show when={result().status !== "error"}>
        <div class="settings-row-copy">
          <div class="settings-row-title">
            <strong>{text().versionsTitle}</strong>
            <HelpHint label={text().versionsTitle}>{text().versionsDescription}</HelpHint>
          </div>
          <small>{text().catalogCheckedAt}{formatUtcTime(result().checkedAt)}</small>
        </div>
        <VersionSelector options={result().options} choice={selected()}
          onSelect={setSelected} text={text()} />
        <button type="button" disabled={actionPending() || statusFailed() || !selectedOption()?.selectable ||
          catalog()?.status !== "loaded" || catalog()?.complete !== true ||
          updateState()?.stage === "pendingApply" || isUpdateBusy(updateState()?.stage)}
          onClick={() => void prepareUpdate()}>
          {text().prepare}
        </button>
      </Show>
    </>}</Show>
    <Show when={statusFailed()}><p role="alert">{updateState() ? copy().stale : copy().unavailable}</p></Show>
    <Show when={!statusFailed() && waitingForInternet()}><p role="status">{text().waitingForInternet}</p></Show>
    <Show when={!statusFailed() && updateState()?.checkResult === "noUpdate"}><p role="status">{copy().noUpdate}</p></Show>
    {/* A discovered version already conveys update availability. */}
    <Show when={!statusFailed() && updateState()?.checkResult === "available" && !updateState()?.availableVersion}><p role="status">{copy().available}</p></Show>
    <Show when={!statusFailed() && updateState()?.availableVersion}>{(version) =>
      <p>{text().availableVersion}{version()}</p>}</Show>
    <Show when={!statusFailed() && updateState()?.lastCheckedAt}>{(time) =>
      <p>{text().lastUpdateCheck}{formatUtcTime(time())}</p>}</Show>
    {/* Show the next check only for interval checks and avoid repeating the waiting-for-network stage. */}
    <Show when={!statusFailed() && updateState() && (updateState()?.nextCheckAt || (committedPeriodic() && !waitingForInternet()))}>
      <p>{text().nextCheck}{updateState()?.nextCheckAt ? formatUtcTime(updateState()!.nextCheckAt!) : text().nextCheckAfterInternet}</p>
    </Show>
    {/* Show nonzero connected time only for interval checks. */}
    <Show when={!statusFailed() && committedPeriodic() && (updateState()?.onlineTimeSinceLastCheckSeconds ?? 0) > 0}>
      <p>{text().connectedElapsed}{formatElapsedDuration(updateState()!.onlineTimeSinceLastCheckSeconds!)}</p>
    </Show>
    <Show when={!statusFailed() && stageLine()}>{(line) =>
      <p role={updateState()?.errorCode ? "alert" : "status"}>{line()}</p>}
    </Show>
    {/* Show target version only during an update or while awaiting application. */}
    <Show when={!statusFailed() && updateState()?.targetVersion && (isUpdateBusy(updateState()?.stage) || updateState()?.stage === "pendingApply")}>
      <p>{text().selectedTarget}{updateState()?.targetVersion}</p>
    </Show>
    <Show when={!statusFailed() && updateState()?.stage === "pendingApply"}>
      <button type="button" disabled={actionPending()}
        onClick={() => void props.productUpdates.apply((version) => window.confirm(text().confirmPrepare(version)), props.text.language)}>
        {copy().apply}
      </button>
    </Show>
  </div>;
}
