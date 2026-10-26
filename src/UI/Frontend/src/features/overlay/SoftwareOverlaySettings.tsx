import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { createEffect, createMemo, createSignal, For, Index, onCleanup, Show } from "solid-js";
import { getPerformanceOverlayMetricCatalog, getPerformanceOverlaySettings, OverlayPreferencesSaveError, savePerformanceOverlaySettings, resetPerformanceOverlayPresentation } from "../../api.ts";
import { expectedOverlayInjectionModules } from "./startupModuleSelection.ts";
import { localizedMetricLabel } from "../../presentation/metricLabels.ts";
import { userFacingErrorMessage } from "../../presentation/userFacingText.ts";
import { MetricModal } from "../monitor/MetricModal.tsx";
import { StandardSelect } from "../../components/StandardSelect.tsx";
import { uiText, currentSettingsText } from "../../text.ts";
import type { MetricDefinition, PerformanceOverlaySettings } from "../../types.ts";
import type { MetricFieldCatalogDevice } from "../../data/metricFields/metricFieldSource.ts";
import { useSource } from "../../frontendRuntime/source/useSource.ts";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext.tsx";
import { useFrontendReadIntervals } from "../../app/FrontendReadIntervalsContext.tsx";
import { fieldCardDefinitions } from "../monitor/fieldCards.ts";
import { RadioGroupItem, RadioGroupRoot } from "../../ui/primitives/RadioGroup.tsx";
import { overlaySourceChoices, overlaySourceIndex, overlaySourcesFor, withOverlaySource } from "./overlaySources.ts";

export function SoftwareOverlaySettings(props: { softwareId: string; active: boolean }) {
  const [saved, setSaved] = createSignal<PerformanceOverlaySettings | null>(null);
  const [draft, setDraft] = createSignal<PerformanceOverlaySettings | null>(null);
  const [catalog, setCatalog] = createSignal<MetricDefinition[]>([]);
  const [loading, setLoading] = createSignal(false);
  const [saving, setSaving] = createSignal(false);
  const [pickerOpen, setPickerOpen] = createSignal(false);
  const [pickerMetrics, setPickerMetrics] = createSignal<string[]>([]);
  const [error, setError] = createSignal("");
  // 先选项目、再选来源：来源是部件地址的后端候选，候选字段要在字段目录里（和卡片选择器一样）。
  const runtime = useFrontendRuntime();
  const componentSource = useSource(runtime.sources.managementComponents, () => ({ active: props.active }));
  const components = () => componentSource.snapshot().data ?? [];
  const readError = () => error() || (componentSource.snapshot().error
    ? userFacingErrorMessage(componentSource.snapshot().error, uiText.status.actionFailed) : "");
  const readIntervals = useFrontendReadIntervals();
  const [fieldDevices, setFieldDevices] = createSignal<readonly MetricFieldCatalogDevice[]>([]);
  createEffect(() => {
    const intervalMs = readIntervals.current()?.monitor;
    if (!props.active || intervalMs === undefined) return;
    const unsubscribe = runtime.sources.metricFieldCatalog.subscribeSnapshot(intervalMs,
      (snapshot) => setFieldDevices(snapshot.data?.fields ?? []));
    onCleanup(unsubscribe);
  });
  const sourceCatalog = createMemo(() => [...catalog(), ...fieldCardDefinitions(fieldDevices())]);
  let requestId = 0;
  const dirty = createMemo(() => JSON.stringify(saved()) !== JSON.stringify(draft()));
  // 后端登记的注入模块和这里的选择不一致（外部改过，或上次只保存了一半）：显示提示，并允许保存来对齐。
  const modulesMismatch = () => {
    const current = saved();
    return current !== null && current.injectionModules !== expectedOverlayInjectionModules(current);
  };
  const modulesNotice = () => {
    const current = saved();
    if (!current || !modulesMismatch()) return null;
    return current.injectionModules === "partial" ? uiText.performanceOverlay.injectedModulesPartial
      : current.injectionModules === "complete" ? uiText.performanceOverlay.externalModeModulesPresent
        : null;
  };

  createEffect(() => {
    if (!props.active || !props.softwareId) return;
    const id = ++requestId;
    setLoading(true);
    setError("");
    void Promise.all([getPerformanceOverlaySettings(props.softwareId), getPerformanceOverlayMetricCatalog()])
      .then(([settings, definitions]) => {
        if (id !== requestId) return;
        setSaved(settings);
        setDraft({ ...settings, metrics: [...settings.metrics] });
        setCatalog(definitions);
      })
      .catch((cause: unknown) => {
        if (id === requestId) setError(userFacingErrorMessage(cause, uiText.status.actionFailed));
      })
      .finally(() => {
        if (id === requestId) setLoading(false);
      });
  });

  const update = (patch: Partial<PerformanceOverlaySettings>) =>
    setDraft((current) => current && !saving() ? { ...current, ...patch } : current);
  type NumberField = "fontSizePx" | "regionWidthRatio" | "regionHeightRatio" | "marginPx";
  // 打到一半（清空、"0."）时数字框的 value 是 ""：只有有限数才写进草稿，否则草稿不变，
  // Solid 不回写，框里留着用户正在打的内容（Number("") 是 0，直接换算会把框改成 "0"）。
  const editNumber = (field: NumberField, input: HTMLInputElement) => {
    const value = input.value.trim() === "" ? Number.NaN : Number(input.value);
    if (Number.isFinite(value)) update({ [field]: value });
  };
  // 草稿值写进框：框里已经是这个数（比如正在打的 "0"）时不写，程序改 value 会打断输入、把光标挪走。
  const bindNumber = (field: NumberField) => (input: HTMLInputElement) => {
    createEffect(() => {
      const current = draft();
      if (current && (input.value.trim() === "" || Number(input.value) !== current[field])) {
        input.value = String(current[field]);
      }
    });
  };
  // 离开框或确认时把框显示成草稿值，用户看到的就是会保存的值。
  const showDraftNumber = (field: NumberField, input: HTMLInputElement) => {
    const current = draft();
    if (current) input.value = String(current[field]);
  };
  const save = async () => {
    const current = draft();
    if (!current || saving()) return;
    setSaving(true);
    setError("");
    try {
      const result = await savePerformanceOverlaySettings(current, saved() ?? undefined);
      setSaved(result);
      setDraft({ ...result, metrics: [...result.metrics] });
    } catch (cause) {
      setError(cause instanceof OverlayPreferencesSaveError
        ? uiText.performanceOverlay.modulesSavedPreferencesFailed
        : userFacingErrorMessage(cause, uiText.status.actionFailed));
    } finally {
      setSaving(false);
    }
  };
  const resetPresentation = async () => {
    if (!draft() || saving()) return;
    setSaving(true);
    setError("");
    try {
      const presentation = await resetPerformanceOverlayPresentation(props.softwareId);
      setSaved(current => current && { ...current, ...presentation });
      setDraft(current => current && { ...current, ...presentation });
    } catch (cause) { setError(userFacingErrorMessage(cause, uiText.status.actionFailed)); }
    finally { setSaving(false); }
  };
  const moveMetric = (index: number, direction: number) => {
    const current = draft();
    if (!current || index + direction < 0 || index + direction >= current.metrics.length) return;
    const metrics = [...current.metrics];
    [metrics[index], metrics[index + direction]] = [metrics[index + direction], metrics[index]];
    update({ metrics });
  };

  return (
    <section class="software-detail-section overlay-settings">
      <div class="software-policy-section-header">
        <h3>{uiText.performanceOverlay.tab}</h3>
        <button type="button" class="secondary" disabled={!draft() || saving()} onClick={() => void resetPresentation()}>
          {currentSettingsText().actions.restoreDefaults}
        </button>
        <button type="button" disabled={!draft() || !(dirty() || modulesMismatch()) || saving()} onClick={() => void save()}>
          <StableLabel labels={{ idle: uiText.softwareDetail.save, busy: uiText.softwareDetail.processing }} current={saving() ? "busy" : "idle"} />
        </button>
      </div>
      <Show when={readError()}>{(message) => <p class="software-detail-hint error" role="alert">{message()}</p>}</Show>
      <Show when={!loading() && draft()} fallback={<p class="software-detail-empty">{uiText.softwareDetail.loadingPolicy}</p>}>
        {(current) => <div class="overlay-settings-fields">
          <label class="overlay-settings-check">
            <input disabled={saving()} type="checkbox" checked={current().enabled} onChange={(event) => update({ enabled: event.currentTarget.checked })} />
            {uiText.performanceOverlay.enabled}
          </label>
          <label>{uiText.performanceOverlay.mode}</label>
          <StandardSelect disabled={saving()} value={current().mode} ariaLabel={uiText.performanceOverlay.mode}
            options={[
              { value: "external", label: uiText.performanceOverlay.external },
              { value: "injected", label: uiText.performanceOverlay.injected }
            ]}
            onChange={(mode) => update({ mode })} />
          <Show when={current().mode === "injected"}>
            <p class="software-detail-hint" role="note">{uiText.performanceOverlay.injectionWarning}</p>
          </Show>
          <Show when={modulesNotice()}>
            {(notice) => <p class="software-detail-hint overlay-modules-notice" role="status">{notice()}</p>}
          </Show>
          <div class="software-policy-section-header">
            <h4>{uiText.performanceOverlay.metrics}</h4>
            <button class="secondary" type="button" disabled={saving()} onClick={() => {
              setPickerMetrics([...current().metrics]);
              setPickerOpen(true);
            }}>{uiText.performanceOverlay.chooseMetrics}</button>
          </div>
          <ol class="overlay-settings-metrics">
            <For each={current().metrics}>
              {(id, index) => <li>
                <span>{localizedMetricLabel(id, catalog().find((item) => item.id === id)?.label)}</span>
                <button class="secondary" type="button" disabled={saving() || index() === 0}
                  aria-label={uiText.performanceOverlay.moveUp} onClick={() => moveMetric(index(), -1)}>↑</button>
                <button class="secondary" type="button" disabled={saving() || index() === current().metrics.length - 1}
                  aria-label={uiText.performanceOverlay.moveDown} onClick={() => moveMetric(index(), 1)}>↓</button>
                <button class="secondary" type="button" disabled={saving()} aria-label={uiText.performanceOverlay.removeMetric}
                  onClick={() => {
                    const metrics = current().metrics.filter((metric) => metric !== id);
                    update({ metrics, metricSources: overlaySourcesFor(current().metricSources, metrics) });
                  }}>×</button>
                <OverlayMetricSourceChoice
                  disabled={saving()}
                  metricId={id}
                  label={localizedMetricLabel(id, catalog().find((item) => item.id === id)?.label)}
                  catalog={sourceCatalog()}
                  current={current().metricSources[id]}
                  onChange={(source) => update({ metricSources: withOverlaySource(current().metricSources, current().metrics, id, source) })} />
              </li>}
            </For>
          </ol>
          <label>{uiText.performanceOverlay.sizeMode}</label>
          <StandardSelect disabled={saving()} value={current().sizeMode} ariaLabel={uiText.performanceOverlay.sizeMode}
            options={[
              { value: "absolutePixels", label: uiText.performanceOverlay.absolutePixels },
              { value: "windowRatio", label: uiText.performanceOverlay.windowRatio }
            ]}
            onChange={(sizeMode) => update({ sizeMode })} />
          <Show when={current().sizeMode === "absolutePixels"}>
            <label>{uiText.performanceOverlay.fontSizePx}
              <input disabled={saving()} type="number" min="8" max="72" ref={bindNumber("fontSizePx")}
                onInput={(event) => editNumber("fontSizePx", event.currentTarget)}
                onChange={(event) => showDraftNumber("fontSizePx", event.currentTarget)} />
            </label>
          </Show>
          <Show when={current().sizeMode === "windowRatio"}>
            <label>{uiText.performanceOverlay.regionWidthRatio}
              <input disabled={saving()} type="number" min="0.05" max="1" step="0.01" ref={bindNumber("regionWidthRatio")}
                onInput={(event) => editNumber("regionWidthRatio", event.currentTarget)}
                onChange={(event) => showDraftNumber("regionWidthRatio", event.currentTarget)} />
            </label>
            <label>{uiText.performanceOverlay.regionHeightRatio}
              <input disabled={saving()} type="number" min="0.05" max="1" step="0.01" ref={bindNumber("regionHeightRatio")}
                onInput={(event) => editNumber("regionHeightRatio", event.currentTarget)}
                onChange={(event) => showDraftNumber("regionHeightRatio", event.currentTarget)} />
            </label>
          </Show>
          <label>{uiText.performanceOverlay.anchor}</label>
          <StandardSelect disabled={saving()} value={current().anchor} ariaLabel={uiText.performanceOverlay.anchor}
            options={[
              { value: "topLeft", label: uiText.performanceOverlay.topLeft },
              { value: "topRight", label: uiText.performanceOverlay.topRight },
              { value: "bottomLeft", label: uiText.performanceOverlay.bottomLeft },
              { value: "bottomRight", label: uiText.performanceOverlay.bottomRight }
            ]}
            onChange={(anchor) => update({ anchor })} />
          <label>{uiText.performanceOverlay.marginPx}
            <input disabled={saving()} type="number" min="0" max="256" ref={bindNumber("marginPx")}
              onInput={(event) => editNumber("marginPx", event.currentTarget)}
                onChange={(event) => showDraftNumber("marginPx", event.currentTarget)} />
          </label>
        </div>}
      </Show>
      <MetricModal open={pickerOpen() && !saving()} catalog={catalog()} components={components()}
        selectedMetricId={pickerMetrics()[0] ?? null} selectedMetricIds={pickerMetrics()} multiSelect
        onSelect={(id) => setPickerMetrics((current) => current.includes(id)
          ? current.filter((item) => item !== id) : [...current, id])}
        onClose={() => setPickerOpen(false)}
        onConfirm={() => {
          update({ metrics: pickerMetrics(), metricSources: overlaySourcesFor(draft()?.metricSources ?? {}, pickerMetrics()) });
          setPickerOpen(false);
        }}
        onDependencyPrompt={(_metric, dependency) => setError(uiText.metricPicker.needsInstall(dependency.name))} />
    </section>
  );
}

/** 一项的来源：只有不止一个来源时摆出来，第一项是自动选择（写出后端现在选的来源）。统一分段样式。 */
function OverlayMetricSourceChoice(props: {
  metricId: string;
  label: string;
  catalog: readonly MetricDefinition[];
  current: PerformanceOverlaySettings["metricSources"][string] | undefined;
  disabled: boolean;
  onChange: (source: PerformanceOverlaySettings["metricSources"][string] | null) => void;
}) {
  const choices = createMemo(() => overlaySourceChoices(props.metricId, props.catalog));
  const groupLabel = () => uiText.resourceTable.columnSourceLabel(props.label);
  return (
    <Show when={choices().length > 0}>
      <div class="overlay-settings-metric-source">
        <strong>{uiText.metricPicker.sourceLabel}</strong>
        <RadioGroupRoot class="segmented overlay-settings-metric-source-choices" ariaLabel={groupLabel()} disabled={props.disabled}
          value={(() => { const index = overlaySourceIndex(choices(), props.current); return index < 0 ? "" : `source-${index}`; })()}
          onChange={(value) => {
            const choice = choices()[Number(value.slice("source-".length))];
            if (choice) props.onChange(choice.source);
          }}>
          <Index each={choices()}>
            {(choice, index) => (
              <RadioGroupItem value={`source-${index}`} class="segment" title={choice().label}>
                {choice().label}
                <Show when={choice().preferred}>
                  <small class="metric-source-choice-mark">{uiText.metricPicker.sourcePreferred}</small>
                </Show>
              </RadioGroupItem>
            )}
          </Index>
        </RadioGroupRoot>
      </div>
    </Show>
  );
}
