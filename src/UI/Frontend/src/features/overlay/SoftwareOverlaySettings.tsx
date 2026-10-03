import { createEffect, createMemo, createSignal, For, Show } from "solid-js";
import { getComponents, getPerformanceOverlayMetricCatalog, getPerformanceOverlaySettings, savePerformanceOverlaySettings } from "../../api.ts";
import { localizedMetricLabel } from "../../presentation/metricLabels.ts";
import { userFacingErrorMessage } from "../../presentation/userFacingText.ts";
import { MetricModal } from "../monitor/MetricModal.tsx";
import { StandardSelect } from "../../components/StandardSelect.tsx";
import { uiText } from "../../text.ts";
import type { ManagedComponent, MetricDefinition, PerformanceOverlaySettings } from "../../types.ts";

export function SoftwareOverlaySettings(props: { softwareId: string; active: boolean }) {
  const [saved, setSaved] = createSignal<PerformanceOverlaySettings | null>(null);
  const [draft, setDraft] = createSignal<PerformanceOverlaySettings | null>(null);
  const [catalog, setCatalog] = createSignal<MetricDefinition[]>([]);
  const [components, setComponents] = createSignal<ManagedComponent[]>([]);
  const [loading, setLoading] = createSignal(false);
  const [saving, setSaving] = createSignal(false);
  const [pickerOpen, setPickerOpen] = createSignal(false);
  const [pickerMetrics, setPickerMetrics] = createSignal<string[]>([]);
  const [error, setError] = createSignal("");
  let requestId = 0;
  const dirty = createMemo(() => JSON.stringify(saved()) !== JSON.stringify(draft()));

  createEffect(() => {
    if (!props.active || !props.softwareId) return;
    const id = ++requestId;
    setLoading(true);
    setError("");
    void Promise.all([getPerformanceOverlaySettings(props.softwareId), getPerformanceOverlayMetricCatalog(), getComponents()])
      .then(([settings, definitions, installed]) => {
        if (id !== requestId) return;
        setSaved(settings);
        setDraft({ ...settings, metrics: [...settings.metrics] });
        setCatalog(definitions);
        setComponents(installed);
      })
      .catch((cause: unknown) => {
        if (id === requestId) setError(userFacingErrorMessage(cause, uiText.status.actionFailed));
      })
      .finally(() => {
        if (id === requestId) setLoading(false);
      });
  });

  const update = (patch: Partial<PerformanceOverlaySettings>) =>
    setDraft((current) => current ? { ...current, ...patch } : current);
  const numberValue = (text: string, previous: number) => {
    const value = Number(text);
    return Number.isFinite(value) ? value : previous;
  };
  const save = async () => {
    const current = draft();
    if (!current) return;
    setSaving(true);
    setError("");
    try {
      const result = await savePerformanceOverlaySettings(current);
      setSaved(result);
      setDraft({ ...result, metrics: [...result.metrics] });
    } catch (cause) {
      setError(userFacingErrorMessage(cause, uiText.status.actionFailed));
    } finally {
      setSaving(false);
    }
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
        <button type="button" disabled={!draft() || !dirty() || saving()} onClick={() => void save()}>
          {saving() ? uiText.softwareDetail.processing : uiText.softwareDetail.save}
        </button>
      </div>
      <Show when={error()}>{(message) => <p class="software-detail-hint error" role="alert">{message()}</p>}</Show>
      <Show when={!loading() && draft()} fallback={<p class="software-detail-empty">{uiText.softwareDetail.loadingPolicy}</p>}>
        {(current) => <div class="overlay-settings-fields">
          <label class="overlay-settings-check">
            <input type="checkbox" checked={current().enabled} onChange={(event) => update({ enabled: event.currentTarget.checked })} />
            {uiText.performanceOverlay.enabled}
          </label>
          <label>{uiText.performanceOverlay.mode}</label>
          <StandardSelect value={current().mode} ariaLabel={uiText.performanceOverlay.mode}
            options={[
              { value: "external", label: uiText.performanceOverlay.external },
              { value: "injected", label: uiText.performanceOverlay.injected }
            ]}
            onChange={(mode) => update({ mode })} />
          <Show when={current().mode === "injected"}>
            <p class="software-detail-hint" role="note">{uiText.performanceOverlay.injectionWarning}</p>
          </Show>
          <div class="software-policy-section-header">
            <h4>{uiText.performanceOverlay.metrics}</h4>
            <button class="secondary" type="button" onClick={() => {
              setPickerMetrics([...current().metrics]);
              setPickerOpen(true);
            }}>{uiText.performanceOverlay.chooseMetrics}</button>
          </div>
          <ol class="overlay-settings-metrics">
            <For each={current().metrics}>
              {(id, index) => <li>
                <span>{localizedMetricLabel(id, catalog().find((item) => item.id === id)?.label)}</span>
                <button class="secondary" type="button" disabled={index() === 0}
                  aria-label={uiText.performanceOverlay.moveUp} onClick={() => moveMetric(index(), -1)}>↑</button>
                <button class="secondary" type="button" disabled={index() === current().metrics.length - 1}
                  aria-label={uiText.performanceOverlay.moveDown} onClick={() => moveMetric(index(), 1)}>↓</button>
                <button class="secondary" type="button" aria-label={uiText.performanceOverlay.removeMetric}
                  onClick={() => update({ metrics: current().metrics.filter((metric) => metric !== id) })}>×</button>
              </li>}
            </For>
          </ol>
          <label>{uiText.performanceOverlay.sizeMode}</label>
          <StandardSelect value={current().sizeMode} ariaLabel={uiText.performanceOverlay.sizeMode}
            options={[
              { value: "absolutePixels", label: uiText.performanceOverlay.absolutePixels },
              { value: "windowRatio", label: uiText.performanceOverlay.windowRatio }
            ]}
            onChange={(sizeMode) => update({ sizeMode })} />
          <Show when={current().sizeMode === "absolutePixels"}>
            <label>{uiText.performanceOverlay.fontSizePx}
              <input type="number" min="8" max="72" value={current().fontSizePx}
                onInput={(event) => update({ fontSizePx: numberValue(event.currentTarget.value, current().fontSizePx) })} />
            </label>
          </Show>
          <Show when={current().sizeMode === "windowRatio"}>
            <label>{uiText.performanceOverlay.regionWidthRatio}
              <input type="number" min="0.05" max="1" step="0.01" value={current().regionWidthRatio}
                onInput={(event) => update({ regionWidthRatio: numberValue(event.currentTarget.value, current().regionWidthRatio) })} />
            </label>
            <label>{uiText.performanceOverlay.regionHeightRatio}
              <input type="number" min="0.05" max="1" step="0.01" value={current().regionHeightRatio}
                onInput={(event) => update({ regionHeightRatio: numberValue(event.currentTarget.value, current().regionHeightRatio) })} />
            </label>
          </Show>
          <label>{uiText.performanceOverlay.anchor}</label>
          <StandardSelect value={current().anchor} ariaLabel={uiText.performanceOverlay.anchor}
            options={[
              { value: "topLeft", label: uiText.performanceOverlay.topLeft },
              { value: "topRight", label: uiText.performanceOverlay.topRight },
              { value: "bottomLeft", label: uiText.performanceOverlay.bottomLeft },
              { value: "bottomRight", label: uiText.performanceOverlay.bottomRight }
            ]}
            onChange={(anchor) => update({ anchor })} />
          <label>{uiText.performanceOverlay.marginPx}
            <input type="number" min="0" max="256" value={current().marginPx}
              onInput={(event) => update({ marginPx: numberValue(event.currentTarget.value, current().marginPx) })} />
          </label>
        </div>}
      </Show>
      <MetricModal open={pickerOpen()} catalog={catalog()} components={components()}
        selectedMetricId={pickerMetrics()[0] ?? null} selectedMetricIds={pickerMetrics()} multiSelect
        onSelect={(id) => setPickerMetrics((current) => current.includes(id)
          ? current.filter((item) => item !== id) : [...current, id])}
        onClose={() => setPickerOpen(false)}
        onConfirm={() => { update({ metrics: pickerMetrics() }); setPickerOpen(false); }}
        onDependencyPrompt={(_metric, dependency) => setError(uiText.metricPicker.needsInstall(dependency.name))} />
    </section>
  );
}
