import { formatTrimmedDigits } from "../../presentation/interfaceNumbers.ts";
import { createSignal, onCleanup, Show } from "solid-js";
import {
  getCpuBaselineRatio,
  isValidCpuBaselineRatio,
  resetCpuBaselineRatio,
  saveCpuBaselineRatio,
  type CpuBaselineRatioSettings,
  type CpuBaselineRatioUpdateResult
} from "../../data/cpu/cpuBaselineRatioApi.ts";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import { userFacingErrorMessage } from "../../presentation/userFacingText";
import { uiText } from "../../text.ts";

/** CPU 基准比例：显示当前 CPU 的默认值与来源，允许设定或清除用户值。 */
export function CpuBaselineRatioPanel(props: { runtimeEffectsEnabled: boolean }) {
  const runtime = useFrontendRuntime();
  const [settings, setSettings] = createSignal<CpuBaselineRatioSettings | null>(null);
  const [readState, setReadState] = createSignal<"loading" | "ready" | "failed">("loading");
  const [editing, setEditing] = createSignal(false);
  const [draft, setDraft] = createSignal("");
  const [saving, setSaving] = createSignal(false);
  const [commandError, setCommandError] = createSignal<string | null>(null);
  const [notice, setNotice] = createSignal<string | null>(null);
  let readController: AbortController | null = null;
  onCleanup(() => readController?.abort());

  function load() {
    readController?.abort();
    const controller = new AbortController();
    readController = controller;
    setReadState("loading");
    getCpuBaselineRatio(runtime.requestClient, controller.signal)
      .then((value) => {
        if (controller.signal.aborted) return;
        setSettings(value);
        setReadState("ready");
      })
      .catch(() => {
        if (!controller.signal.aborted) setReadState("failed");
      });
  }
  load();

  function startEditing(current: CpuBaselineRatioSettings) {
    setDraft(String(Number(current.ratio.toFixed(4))));
    setCommandError(null);
    setNotice(null);
    setEditing(true);
  }

  // savedNotApplied 时运行计划仍是旧值，所以只有带 settings 的结果才替换显示。
  function applyResult(result: CpuBaselineRatioUpdateResult) {
    if (result.settings) setSettings(result.settings);
    setNotice(result.runtimeApplicationDisposition === "savedNotApplied"
      ? uiText.cpuBaseline.savedNotApplied
      : result.runtimeApplicationDisposition === "appliedWithDeliveryFailures"
        ? uiText.cpuBaseline.deliveryFailures
        : null);
    setEditing(false);
  }

  async function save() {
    if (!props.runtimeEffectsEnabled || saving()) return;
    const text = draft().trim();
    const ratio = Number(text);
    if (text === "" || !isValidCpuBaselineRatio(ratio)) {
      setCommandError(uiText.cpuBaseline.invalidRatio);
      return;
    }
    setSaving(true);
    setCommandError(null);
    try {
      applyResult(await saveCpuBaselineRatio(runtime.requestClient, ratio));
    } catch (error) {
      setCommandError(userFacingErrorMessage(error, uiText.cpuBaseline.saveFailed));
    } finally {
      setSaving(false);
    }
  }

  async function reset() {
    if (!props.runtimeEffectsEnabled || saving()) return;
    setSaving(true);
    setCommandError(null);
    try {
      applyResult(await resetCpuBaselineRatio(runtime.requestClient));
    } catch (error) {
      setCommandError(userFacingErrorMessage(error, uiText.cpuBaseline.resetFailed));
    } finally {
      setSaving(false);
    }
  }

  return (
    <section class="cpu-topology-panel cpu-baseline-panel" aria-label={uiText.cpuBaseline.title}>
      <div class="panel-header cpu-topology-header">
        <div class="optimization-heading">
          <h2>{uiText.cpuBaseline.title}</h2>
        </div>
        <Show when={settings()}>
          {(current) => (
            <div class="cpu-topology-actions">
              <Show when={editing()} fallback={
                <Show when={props.runtimeEffectsEnabled}>
                  <button class="secondary" type="button" onClick={() => startEditing(current())}>
                    {uiText.cpuBaseline.edit}
                  </button>
                </Show>
              }>
                <button class="secondary" type="button" disabled={saving()} onClick={() => setEditing(false)}>
                  {uiText.cpuBaseline.cancel}
                </button>
                <Show when={current().overrideRatio !== null}>
                  <button class="secondary" type="button" disabled={saving()} onClick={() => void reset()}>
                    {uiText.cpuBaseline.reset}
                  </button>
                </Show>
                <button type="button" disabled={saving()} onClick={() => void save()}>
                  {uiText.cpuBaseline.save}
                </button>
              </Show>
            </div>
          )}
        </Show>
      </div>
      <p class="cpu-baseline-description">{uiText.cpuBaseline.description}</p>
      <Show when={commandError()}>
        {(message) => <p role="alert">{message()}</p>}
      </Show>
      <Show when={notice()}>
        {(message) => <p role="status">{message()}</p>}
      </Show>
      <Show when={readState() !== "loading"} fallback={<div class="optimization-empty">-</div>}>
        <Show when={readState() === "ready" && settings()} fallback={
          <div class="cpu-baseline-unavailable">
            <p role="alert">
              {readState() === "failed" ? uiText.cpuBaseline.readFailed : uiText.cpuBaseline.unavailable}
            </p>
            <button class="secondary" type="button" onClick={load}>{uiText.cpuBaseline.retry}</button>
          </div>
        }>
          {(current) => (
            <>
              <div class="cpu-topology-meta">
                <strong title={current().cpuName}>{current().cpuName}</strong>
              </div>
              <div class="smart-details-summary">
                <BaselineTile
                  label={uiText.cpuBaseline.currentRatio}
                  value={`${formatRatio(current().ratio)} · ${current().overrideRatio === null
                    ? uiText.cpuBaseline.defaultActive
                    : uiText.cpuBaseline.overrideActive}`}
                />
                <BaselineTile label={uiText.cpuBaseline.multiplier} value={`×${formatMultiplier(current().multiplier)}`} />
                <BaselineTile label={uiText.cpuBaseline.defaultRatio} value={formatRatio(current().defaultRatio)} />
                <BaselineTile
                  label={uiText.cpuBaseline.defaultSource}
                  value={uiText.cpuBaseline.sources[current().defaultSource]}
                />
                <BaselineTile
                  label={uiText.cpuBaseline.matchedModel}
                  value={current().matchedModel ?? uiText.cpuBaseline.noMatchedModel}
                />
                <BaselineTile label={uiText.cpuBaseline.datasetVersion} value={current().datasetVersion} />
              </div>
              <Show when={editing()}>
                <label class="cpu-score-field cpu-baseline-field">
                  <span>{uiText.cpuBaseline.ratioInput}</span>
                  <input
                    class="cpu-score-input"
                    type="number"
                    min="0"
                    max="1"
                    step="any"
                    disabled={saving()}
                    value={draft()}
                    onInput={(event) => setDraft(event.currentTarget.value)}
                    onKeyDown={(event) => { if (event.key === "Enter") void save(); }}
                  />
                  <small>{uiText.cpuBaseline.ratioHint}</small>
                </label>
              </Show>
            </>
          )}
        </Show>
      </Show>
    </section>
  );
}

function BaselineTile(props: { label: string; value: string }) {
  return (
    <div class="smart-details-tile">
      <span>{props.label}</span>
      <strong title={props.value}>{props.value}</strong>
    </div>
  );
}

function formatRatio(value: number) {
  return formatTrimmedDigits(value, 4);
}

function formatMultiplier(value: number) {
  return formatTrimmedDigits(value, 3);
}
