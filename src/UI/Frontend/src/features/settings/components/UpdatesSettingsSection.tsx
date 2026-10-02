import { Show, createSignal, onCleanup, onMount } from "solid-js";
import { getJson, postJson } from "../../../api";
import type { AppUpdateSettings } from "../../../types";
import type { SettingsTextBundle } from "../../../i18n/settingsTypes.ts";
import { VersionSelector, type VersionChoice } from "./VersionSelector";

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
  error?: string | null;
}

interface ProductUpdateState {
  stage: string;
  targetVersion?: string | null;
  workspace?: string | null;
  error?: string | null;
}

export function UpdatesSettingsSection(props: {
  settings: AppUpdateSettings | undefined;
  text: SettingsTextBundle;
  onAutoUpdateChange: (enabled: boolean) => void;
}) {
  const [catalog, setCatalog] = createSignal<ProductVersionCatalog | null>(null);
  const [loading, setLoading] = createSignal(false);
  const [selected, setSelected] = createSignal<string | null>(null);
  const [updateState, setUpdateState] = createSignal<ProductUpdateState | null>(null);
  const text = () => props.text.updates;
  const selectedOption = () => catalog()?.options.find((option) => option.choice === selected());

  async function refresh() {
    setLoading(true);
    try {
      setCatalog(await getJson<ProductVersionCatalog>("/api/updates/product/versions", { timeoutMs: 35000 }));
    } catch (error) {
      setCatalog({ status: "error", complete: false, checkedAt: new Date().toISOString(),
        options: [], error: error instanceof Error ? error.message : String(error) });
    } finally {
      setLoading(false);
    }
  }
  async function refreshUpdateState() {
    try { setUpdateState(await getJson<ProductUpdateState>("/api/updates/product/status")); }
    catch { /* The service can briefly disappear while the updater switches versions. */ }
  }
  onMount(() => { void refresh(); void refreshUpdateState(); });
  const interval = window.setInterval(() => {
    if (updateState()?.stage && updateState()?.stage !== "idle") void refreshUpdateState();
  }, 3000);
  onCleanup(() => window.clearInterval(interval));

  async function prepareUpdate() {
    const option = selectedOption();
    if (!option?.selectable) return;
    const approved = window.confirm(text().confirmPrepare(option.version));
    if (!approved) return;
    try {
      setUpdateState(await postJson<ProductUpdateState>("/api/updates/product/prepare",
        { choice: option.choice }, text().prepareFailed));
    } catch (error) {
      setUpdateState({ stage: "error", error: error instanceof Error ? error.message : String(error) });
    }
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
        <p role="alert">{text().catalogFailed}{result().error}</p>
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
        <button type="button" disabled={!selectedOption()?.selectable ||
          catalog()?.status !== "loaded" || catalog()?.complete !== true ||
          updateState()?.stage === "checking" || updateState()?.stage === "downloading" ||
          updateState()?.stage === "verifying" || updateState()?.stage === "waitingForExit"}
          onClick={() => void prepareUpdate()}>
          {text().prepare}
        </button>
      </Show>
    </>}</Show>
    <Show when={updateState() && updateState()?.stage !== "idle"}>
      <p role={updateState()?.stage === "error" ? "alert" : "status"}>
        {updateState()?.stage === "waitingForExit"
          ? text().waitingForExit
          : updateState()?.stage === "error"
            ? updateState()?.error
            : `${text().stageLabel}: ${text().stages[updateState()?.stage ?? ""] ?? text().unknownStage}`}
      </p>
    </Show>
  </div>;
}
