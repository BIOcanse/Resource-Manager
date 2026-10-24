import { Show, createSignal, onCleanup, onMount } from "solid-js";
import { getJson, postJson } from "../../../api";
import { ObservationStateNotice } from "../../../components/ObservationStateNotice";
import { failedObservation, loadingObservation } from "../../../observation/observationState";
import type { AppUpdateSettings } from "../../../types";
import type { SettingsTextBundle } from "../../../i18n/settingsTypes.ts";
import { VersionSelector, type VersionChoice } from "./VersionSelector";
import { isUpdateBusy, productUpdateCopy, updateFailure, type ProductUpdateState } from "../productUpdatePresentation.ts";
import { postShellMessage } from "../../../utils";

interface ProductVersionOption extends VersionChoice {
  version: string;
  assetName: string;
  downloadUrl: string;
  checksumUrl: string;
  selectable: boolean;
}

interface ProductVersionCatalog {
  installedVersion?: string | null;
  status: "loaded" | "partial" | "stale" | "error";
  complete: boolean;
  checkedAt: string;
  options: ProductVersionOption[];
  errorCode?: string | null;
  errorParameters?: Record<string, string> | null;
}

export function UpdatesSettingsSection(props: {
  settings: AppUpdateSettings | undefined;
  text: SettingsTextBundle;
  onAutoUpdateChange: (enabled: boolean) => void;
  onCheckIntervalChange: (hours: number) => void;
}) {
  const [catalog, setCatalog] = createSignal<ProductVersionCatalog | null>(null);
  const [loading, setLoading] = createSignal(false);
  const [selected, setSelected] = createSignal<string | null>(null);
  const [updateState, setUpdateState] = createSignal<ProductUpdateState | null>(null);
  const [statusFailed, setStatusFailed] = createSignal(false);
  const [actionPending, setActionPending] = createSignal(false);
  let statusReading = false;
  const text = () => props.text.updates;
  const copy = () => productUpdateCopy(props.text.language);
  const selectedOption = () => catalog()?.options.find((option) => option.choice === selected());

  async function refresh() {
    setLoading(true);
    try {
      setCatalog(await getJson<ProductVersionCatalog>("/api/updates/product/versions", { timeoutMs: 35000 }));
    } catch {
      setCatalog({ status: "error", complete: false, checkedAt: new Date().toISOString(),
        options: [], errorCode: "update.catalogUnavailable" });
    } finally {
      setLoading(false);
    }
  }
  async function refreshUpdateState() {
    if (statusReading || actionPending()) return;
    statusReading = true;
    const previous = updateState();
    try {
      const state = await getJson<ProductUpdateState>("/api/updates/product/status");
      // A read started before a command must not replace that command's result.
      if (!actionPending() && updateState() === previous) {
        setUpdateState(state);
        setStatusFailed(false);
      }
    } catch {
      if (!actionPending() && updateState() === previous) setStatusFailed(true);
    }
    finally { statusReading = false; }
  }
  onMount(() => { void refresh(); void refreshUpdateState(); });
  const interval = window.setInterval(() => {
    void refreshUpdateState();
  }, 3000);
  onCleanup(() => window.clearInterval(interval));

  async function prepareUpdate() {
    const option = selectedOption();
    if (!option?.selectable || actionPending() || statusFailed() || updateState()?.stage === "pendingApply" || isUpdateBusy(updateState()?.stage)) return;
    const approved = window.confirm(text().confirmPrepare(option.version));
    if (!approved) return;
    setActionPending(true);
    try {
      setUpdateState(await postJson<ProductUpdateState>("/api/updates/product/prepare",
        { choice: option.choice }, text().prepareFailed));
      setStatusFailed(false);
    } catch (error) {
      setUpdateState(updateFailure(error, "update.preparationFailed"));
    } finally { setActionPending(false); }
  }

  async function applyUpdate() {
    if (actionPending() || statusFailed() || updateState()?.stage !== "pendingApply") return;
    if (!window.confirm(text().confirmPrepare(updateState()?.targetVersion ?? ""))) return;
    setActionPending(true);
    try {
      const state = await postJson<ProductUpdateState>("/api/updates/product/apply", {}, copy().error("update.applyFailed"));
      setUpdateState(state);
      setStatusFailed(false);
      if (state.stage === "waitingForExit") postShellMessage("update.applyExit");
    } catch (error) {
      setUpdateState(updateFailure(error, "update.applyFailed"));
    } finally { setActionPending(false); }
  }

  return <div class="settings-section-panel">
    <div class="settings-row">
      <div class="settings-row-copy">
        <strong>{text().autoUpdateTitle}</strong>
        <span>{text().autoUpdateDescription}</span>
      </div>
      <label class="settings-switch">
        <input type="checkbox" checked={props.settings?.autoUpdateEnabled === true}
          aria-label={text().autoUpdateTitle}
          onChange={(event) => props.onAutoUpdateChange(event.currentTarget.checked)} />
        <span />
      </label>
    </div>
    <div class="settings-row">
      <label class="settings-row-copy" for="update-check-interval"><strong>{copy().interval}</strong></label>
      <input id="update-check-interval" type="number" min="1" max="168" step="1"
        value={props.settings?.checkIntervalHours ?? 6}
        onChange={(event) => {
          const value = event.currentTarget.valueAsNumber;
          if (Number.isInteger(value) && value >= 1 && value <= 168) props.onCheckIntervalChange(value);
        }} />
    </div>
    <div class="settings-row">
      <div class="settings-row-copy">
        <strong>{text().installedVersionTitle}</strong>
        <span>{catalog()?.installedVersion ?? (text().unknownVersion)}</span>
      </div>
      <button type="button" class="secondary" disabled={loading()} onClick={() => void refresh()}>
        {loading() ? text().checking : text().checkForUpdates}
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
          <strong>{text().versionsTitle}</strong>
          <span>{text().versionsDescription}</span>
          <small>{text().lastChecked}{new Date(result().checkedAt).toLocaleString(props.text.language)}</small>
        </div>
        <VersionSelector options={result().options} choice={selected()}
          onSelect={setSelected} text={text()} />
        <Show when={selectedOption()}>{(option) => <p role="status">
          {text().selectedTarget}{option().version}
        </p>}</Show>
        <button type="button" disabled={actionPending() || statusFailed() || !selectedOption()?.selectable ||
          catalog()?.status !== "loaded" || catalog()?.complete !== true ||
          updateState()?.stage === "pendingApply" || isUpdateBusy(updateState()?.stage)}
          onClick={() => void prepareUpdate()}>
          {text().prepare}
        </button>
      </Show>
    </>}</Show>
    <Show when={statusFailed()}><p role="alert">{updateState() ? copy().stale : copy().unavailable}</p></Show>
    <Show when={!statusFailed() && updateState()?.checkResult === "noUpdate"}><p role="status">{copy().noUpdate}</p></Show>
    <Show when={!statusFailed() && updateState()?.checkResult === "available"}><p role="status">{copy().available}</p></Show>
    <Show when={!statusFailed() && updateState()?.lastCheckedAt}><p>{text().lastChecked}{new Date(updateState()!.lastCheckedAt!).toLocaleString(props.text.language)}</p></Show>
    <Show when={!statusFailed() && updateState() && (updateState()?.stage !== "idle" || updateState()?.errorCode)}>
      <p role={updateState()?.errorCode ? "alert" : "status"}>
        {updateState()?.stage === "waitingForExit"
          ? text().waitingForExit
          : updateState()?.errorCode
            ? copy().error(updateState()?.errorCode, updateState()?.errorParameters)
            : updateState()?.stage === "pendingApply"
              ? copy().pending
            : `${text().stageLabel}: ${text().stages[updateState()?.stage ?? ""] ?? text().unknownStage}`}
      </p>
    </Show>
    <Show when={!statusFailed() && updateState()?.targetVersion && updateState()?.stage !== "idle"}>
      <p>{text().selectedTarget}{updateState()?.targetVersion}</p>
    </Show>
    <Show when={!statusFailed() && updateState()?.stage === "pendingApply"}>
      <button type="button" disabled={actionPending()} onClick={() => void applyUpdate()}>{copy().apply}</button>
    </Show>
  </div>;
}
