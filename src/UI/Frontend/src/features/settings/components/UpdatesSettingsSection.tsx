import { Show, createSignal, onCleanup, onMount } from "solid-js";
import { getJson, postJson } from "../../../api";
import type { AppUpdateSettings } from "../../../types";
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
  language: string;
  onAutoUpdateChange: (enabled: boolean) => void;
}) {
  const [catalog, setCatalog] = createSignal<ProductVersionCatalog | null>(null);
  const [loading, setLoading] = createSignal(false);
  const [selected, setSelected] = createSignal<string | null>(null);
  const [updateState, setUpdateState] = createSignal<ProductUpdateState | null>(null);
  const chinese = () => props.language.startsWith("zh");
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
    const approved = window.confirm(chinese()
      ? `将更新到 ${option.version}。更新器会校验发行包，等待你退出所有桌面界面，停止服务，保留并迁移数据，验证新版本；失败时恢复旧版。继续准备吗？`
      : `Update to ${option.version}? The updater will verify the package, wait for all desktop sessions to exit, stop the service, preserve and migrate data, and check the new version. It restores the previous version on failure.`);
    if (!approved) return;
    try {
      setUpdateState(await postJson<ProductUpdateState>("/api/updates/product/prepare",
        { choice: option.choice }, chinese() ? "无法准备更新。" : "Unable to prepare update."));
    } catch (error) {
      setUpdateState({ stage: "error", error: error instanceof Error ? error.message : String(error) });
    }
  }

  return <div class="settings-section-panel">
    <div class="settings-row">
      <div class="settings-row-copy">
        <strong>{chinese() ? "自动更新" : "Automatic updates"}</strong>
        <span>{chinese()
          ? "保存自动更新偏好。可信发布签名接入前不会自动安装；手动更新可用。"
          : "Save the automatic update preference. Automatic installation waits for trusted release signing; manual updates are available."}</span>
      </div>
      <label class="settings-switch">
        <input type="checkbox" checked={props.settings?.autoUpdateEnabled === true}
          aria-label={chinese() ? "自动更新" : "Automatic updates"}
          onChange={(event) => props.onAutoUpdateChange(event.currentTarget.checked)} />
        <span />
      </label>
    </div>
    <div class="settings-row">
      <div class="settings-row-copy">
        <strong>{chinese() ? "当前安装版本" : "Installed version"}</strong>
        <span>{catalog()?.installedVersion ?? (chinese() ? "无法识别" : "Unknown")}</span>
      </div>
      <button type="button" class="secondary" disabled={loading()} onClick={() => void refresh()}>
        {loading() ? chinese() ? "检查中…" : "Checking…"
          : chinese() ? "检查更新" : "Check for updates"}
      </button>
    </div>
    <Show when={catalog()}>{(result) => <>
      <Show when={result().status === "error"}>
        <p role="alert">{chinese() ? "无法读取版本目录：" : "Unable to read release catalog: "}{result().error}</p>
      </Show>
      <Show when={result().status === "partial"}>
        <p role="status">{chinese() ? "版本历史尚未完整读取。" : "Release history is incomplete."}</p>
      </Show>
      <Show when={result().status === "stale"}>
        <p role="status">{chinese() ? "正在显示上次读取的版本目录；请联网后重试。" : "Showing the previous release catalog; reconnect and retry."}</p>
      </Show>
      <Show when={result().status !== "error"}>
        <div class="settings-row-copy">
          <strong>{chinese() ? "产品版本" : "Product versions"}</strong>
          <span>{chinese() ? "从新到旧排列；每个系列默认显示最新一版。" : "Newest first; each series shows its newest release by default."}</span>
          <small>{chinese() ? "上次检查：" : "Last checked: "}{new Date(result().checkedAt).toLocaleString(props.language)}</small>
        </div>
        <VersionSelector options={result().options} choice={selected()}
          onSelect={setSelected} language={props.language} />
        <Show when={selectedOption()}>{(option) => <p role="status">
          {chinese() ? "已选择目标版本：" : "Selected target: "}{option().version}
        </p>}</Show>
        <button type="button" disabled={!selectedOption()?.selectable ||
          catalog()?.status !== "loaded" || catalog()?.complete !== true ||
          updateState()?.stage === "checking" || updateState()?.stage === "downloading" ||
          updateState()?.stage === "verifying" || updateState()?.stage === "waitingForExit"}
          onClick={() => void prepareUpdate()}>
          {chinese() ? "准备并安装所选更新" : "Prepare and install selected update"}
        </button>
      </Show>
    </>}</Show>
    <Show when={updateState() && updateState()?.stage !== "idle"}>
      <p role={updateState()?.stage === "error" ? "alert" : "status"}>
        {updateState()?.stage === "waitingForExit"
          ? chinese() ? "更新包已准备。请从托盘退出所有 Resource Manager 桌面界面；更新器会随后切换版本。" : "Update ready. Exit every Resource Manager desktop session from the tray to apply it."
          : updateState()?.stage === "error"
            ? updateState()?.error
            : `${chinese() ? "更新状态" : "Update status"}: ${updateState()?.stage}`}
      </p>
    </Show>
  </div>;
}
