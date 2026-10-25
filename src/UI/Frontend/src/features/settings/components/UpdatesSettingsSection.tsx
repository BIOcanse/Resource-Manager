import { Show, createSignal, onMount } from "solid-js";
import { getJson, postJson } from "../../../api";
import { ObservationStateNotice } from "../../../components/ObservationStateNotice";
import { failedObservation, loadingObservation } from "../../../observation/observationState";
import type { AppNotificationSettings, AppUpdateMode, AppUpdateSettings } from "../../../types";
import type { SettingsTextBundle } from "../../../i18n/settingsTypes.ts";
import { VersionSelector, type VersionChoice } from "./VersionSelector";
import { isUpdateBusy, productUpdateCopy, updateFailure, type ProductUpdateState } from "../productUpdatePresentation.ts";
import type { ProductUpdateStore } from "../../../stores/productUpdateStore.ts";
import { updateCheckIntervalHours } from "../../../stores/settingsStore.ts";
import { formatConnectedDuration, formatUtcTime, parseUtcTime } from "../../../data/productUpdateStatus.ts";
import { NumberField, SegmentedControl } from "./SettingsControls";
import { SwitchControl } from "../../../ui/primitives/Switch.tsx";

interface ProductVersionOption extends Omit<VersionChoice, "unavailableReason"> {
  version: string;
  assetName: string;
  downloadUrl: string;
  checksumUrl: string;
  selectable: boolean;
  eligibility: "eligible" | "installedVersionUnknown" | "notNewer";
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
  /** 已保存的更新设置；“下次检查”一行按它判断间隔检查是否开着。 */
  committedSettings: AppUpdateSettings | undefined;
  notifications: AppNotificationSettings | undefined;
  productUpdates: ProductUpdateStore;
  text: SettingsTextBundle;
  onModeChange: (mode: AppUpdateMode) => void;
  onPeriodicCheckChange: (enabled: boolean) => void;
  onCheckIntervalChange: (hours: number) => void;
  onNotificationSwitchChange: (path: keyof AppNotificationSettings, enabled: boolean) => void;
}) {
  const [catalog, setCatalog] = createSignal<ProductVersionCatalog | null>(null);
  const [loading, setLoading] = createSignal(false);
  const [selected, setSelected] = createSignal<string | null>(null);
  const [preparePending, setPreparePending] = createSignal(false);
  const text = () => props.text.updates;
  const copy = () => productUpdateCopy(props.text.language);
  const selectedOption = () => catalog()?.options.find((option) => option.choice === selected());
  // 更新状态来自后端推送（设置页和界面内提示共用一份），不再定时读取。
  const updateState = props.productUpdates.status;
  const statusFailed = props.productUpdates.statusFailed;
  const actionPending = () => preparePending() || props.productUpdates.applyPending();
  const mode = () => props.settings?.mode ?? "automatic";
  const automaticOff = () => mode() === "off";
  const committedPeriodic = () => props.committedSettings?.mode !== "off" && props.committedSettings?.periodicCheckEnabled === true;

  async function refresh() {
    setLoading(true);
    try {
      const result = await getJson<ProductVersionCatalog>("/api/updates/product/versions", { timeoutMs: 35000 });
      // 目录的检查时间和更新状态一样是 UTC（「时间按照协调世界时」）；不带 Z 或 +00:00 的时间不是合同里的值。
      if (!parseUtcTime(result.checkedAt)) throw new Error("catalog checkedAt is not UTC");
      setCatalog(result);
    } catch {
      setCatalog({ status: "error", complete: false, checkedAt: new Date().toISOString(),
        options: [], errorCode: "update.catalogUnavailable" });
    } finally {
      setLoading(false);
    }
  }
  onMount(() => { void refresh(); });

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

  const notificationSwitch = (path: keyof AppNotificationSettings, title: string, description: string) =>
    <div class="settings-row">
      <div class="settings-row-copy">
        <strong>{title}</strong>
        <span>{description}</span>
      </div>
      <SwitchControl ariaLabel={title} checked={(props.notifications?.[path] ?? "on") === "on"}
        onChange={(checked) => props.onNotificationSwitchChange(path, checked)} />
    </div>;

  return <div class="settings-section-panel">
    <div class="settings-row">
      <div class="settings-row-copy">
        <strong>{text().modeTitle}</strong>
        <span>{text().modeDescription}</span>
      </div>
      <SegmentedControl value={mode()} options={text().modeOptions} ariaLabel={text().modeTitle}
        onChange={props.onModeChange} />
    </div>
    <div class="settings-row">
      <div class="settings-row-copy">
        <strong>{text().periodicTitle}</strong>
        <span>{text().periodicDescription}</span>
        <span>{text().internetNote}</span>
        <Show when={automaticOff()}><span role="note">{text().offReason}</span></Show>
      </div>
      <div class="settings-update-interval-control">
        <SwitchControl ariaLabel={text().periodicTitle} checked={props.settings?.periodicCheckEnabled === true}
          disabled={automaticOff()} title={automaticOff() ? text().offReason : undefined}
          onChange={props.onPeriodicCheckChange} />
        <NumberField label={text().intervalLabel(updateCheckIntervalHours.minimum, updateCheckIntervalHours.maximum)}
          ariaLabel={text().intervalTitle}
          value={props.settings?.checkIntervalHours ?? updateCheckIntervalHours.defaultValue}
          min={updateCheckIntervalHours.minimum} max={updateCheckIntervalHours.maximum} step={1}
          disabled={automaticOff()} onChange={props.onCheckIntervalChange} />
      </div>
    </div>
    {notificationSwitch("updateInApp", text().inAppNoticeTitle, text().inAppNoticeDescription)}
    {notificationSwitch("updateSystem", text().systemNoticeTitle, text().systemNoticeDescription)}
    <div class="settings-row">
      <div class="settings-row-copy">
        <strong>{text().installedVersionTitle}</strong>
        <span>{catalog()?.installedVersion ?? (text().unknownVersion)}</span>
        <small>{text().manualCheckNote}</small>
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
          <small>{text().lastChecked}{formatUtcTime(result().checkedAt)}</small>
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
    <Show when={!statusFailed() && updateState()?.automaticCheckStatus === "waitingForInternet"}><p role="status">{text().waitingForInternet}</p></Show>
    <Show when={!statusFailed() && updateState()?.checkResult === "noUpdate"}><p role="status">{copy().noUpdate}</p></Show>
    <Show when={!statusFailed() && updateState()?.checkResult === "available"}><p role="status">{copy().available}</p></Show>
    <Show when={!statusFailed() && updateState()?.availableVersion}>{(version) =>
      <p>{text().availableVersion}{version()}</p>}</Show>
    <Show when={!statusFailed() && updateState()?.lastCheckedAt}>{(time) =>
      <p>{text().lastChecked}{formatUtcTime(time())}</p>}</Show>
    <Show when={!statusFailed() && updateState()}>{(state) => <p>
      {text().nextCheck}{state().nextCheckAt ? formatUtcTime(state().nextCheckAt!)
        : committedPeriodic() ? text().nextCheckAfterInternet : text().nextCheckOff}
    </p>}</Show>
    <Show when={!statusFailed() && typeof updateState()?.onlineTimeSinceLastCheckSeconds === "number"}>
      <p>{text().connectedElapsed}{formatConnectedDuration(updateState()!.onlineTimeSinceLastCheckSeconds!)}</p>
    </Show>
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
      <button type="button" disabled={actionPending()}
        onClick={() => void props.productUpdates.apply((version) => window.confirm(text().confirmPrepare(version)), props.text.language)}>
        {copy().apply}
      </button>
    </Show>
  </div>;
}
