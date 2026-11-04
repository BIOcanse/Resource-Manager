import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { createEffect, createMemo, createSignal, For, Show } from "solid-js";
import { Portal } from "solid-js/web";
import {
  getPerformanceOverlaySettings, OverlaySettingsSaveError, performanceOverlayEntry, resetPerformanceOverlayPresentation,
  savePerformanceOverlaySettings
} from "../../api.ts";
import { AppSettingsRevisionConflictError } from "../../data/appSettings/appSettingsApi.ts";
import { expectedOverlayInjectionModules } from "./startupModuleSelection.ts";
import { userFacingErrorMessage } from "../../presentation/userFacingText.ts";
import { StandardSelect } from "../../components/StandardSelect.tsx";
import { uiText, currentSettingsText } from "../../text.ts";
import type { PerformanceOverlayItem, PerformanceOverlayEditorSettings as PerformanceOverlaySettings } from "../../types.ts";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext.tsx";
import { DialogActions, DialogBody, DialogHeader, DialogRoot } from "../../ui/primitives/Dialog.tsx";
import { ItemPicker } from "../itemSelection/ItemPicker.tsx";
import { sourceName } from "../itemSelection/itemNames.ts";
import { findCatalogEntry, intervalPlaceholder, itemKinds, type CatalogEntry } from "../itemSelection/itemSelectionModel.ts";
import { getItemCatalog } from "../targetedReport/targetedRecordingApi.ts";
import { moveView } from "../targetedReport/views/viewModel.ts";
import { overlayItemLabel } from "./overlayItemLabels.ts";
import { overlayDefaultIntervalMs, overlayItemsIssue } from "./overlayItems.ts";

export function SoftwareOverlaySettings(props: { softwareId: string; active: boolean }) {
  const [saved, setSaved] = createSignal<PerformanceOverlaySettings | null>(null);
  const [draft, setDraft] = createSignal<PerformanceOverlaySettings | null>(null);
  // Share the recording catalog and filter items by overlay use.
  const [catalog, setCatalog] = createSignal<CatalogEntry[]>([]);
  const [loading, setLoading] = createSignal(false);
  const [saving, setSaving] = createSignal(false);
  const [pickerOpen, setPickerOpen] = createSignal(false);
  // Keep picker changes local until confirmation.
  const [pickerItems, setPickerItems] = createSignal<PerformanceOverlayItem[]>([]);
  const [pickerIssues, setPickerIssues] = createSignal(false);
  const [error, setError] = createSignal("");
  const runtime = useFrontendRuntime();
  let requestId = 0;
  // Compare backend item fields only; revisions and startup-module registration are save metadata.
  const dirty = createMemo(() => {
    const before = saved();
    const current = draft();
    return before !== null && current !== null
      && JSON.stringify(performanceOverlayEntry(before)) !== JSON.stringify(performanceOverlayEntry(current));
  });
  // Explain invalid draft items and block saving.
  const itemsIssue = createMemo(() => {
    const current = draft();
    const constraints = saved()?.editingConstraints;
    return current && constraints ? overlayItemsIssue(current.items, constraints) : null;
  });
  // Allow saving to reconcile backend injection modules with the selected method.
  const modulesMismatch = () => {
    const current = saved();
    return current !== null && current.injectionModules !== expectedOverlayInjectionModules(current);
  };
  const modulesNotice = () => {
    const current = saved();
    if (!current || !modulesMismatch()) return null;
    return current.injectionModules === "partial" ? uiText.performanceOverlay.injectedModulesPartial
      : current.injectionModules === "complete" ? uiText.performanceOverlay.externalModeModulesPresent
        : uiText.performanceOverlay.injectedModulesMissing;
  };

  createEffect(() => {
    if (!props.active || !props.softwareId) return;
    const id = ++requestId;
    setLoading(true);
    setError("");
    void Promise.all([getPerformanceOverlaySettings(runtime.requestClient, props.softwareId), getItemCatalog(runtime.requestClient)])
      .then(([settings, entries]) => {
        if (id !== requestId) return;
        setSaved(settings);
        setDraft(cloneSettings(settings));
        setCatalog(entries);
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
  // Only finite numeric input updates the draft.
  // Preserve partial text instead of coercing empty input to zero.
  const editNumber = (field: NumberField, input: HTMLInputElement) => {
    const value = input.value.trim() === "" ? Number.NaN : Number(input.value);
    if (Number.isFinite(value)) update({ [field]: value });
  };
  // Avoid rewriting equivalent numeric text so input and caret remain intact.
  const bindNumber = (field: NumberField) => (input: HTMLInputElement) => {
    createEffect(() => {
      const current = draft();
      if (current && (input.value.trim() === "" || Number(input.value) !== current[field])) {
        input.value = String(current[field]);
      }
    });
  };
  // On commit, display the draft value that will be saved.
  const showDraftNumber = (field: NumberField, input: HTMLInputElement) => {
    const current = draft();
    if (current) input.value = String(current[field]);
  };
  // After external changes, refresh the saved baseline while retaining the draft for another save.
  const reloadAfterConflict = async (cause: unknown) => {
    const conflict = cause instanceof AppSettingsRevisionConflictError ? cause
      : cause instanceof OverlaySettingsSaveError && cause.cause instanceof AppSettingsRevisionConflictError ? cause.cause : null;
    if (!conflict) return;
    try { setSaved(await getPerformanceOverlaySettings(runtime.requestClient, props.softwareId)); }
    catch (reloadError) { setError(userFacingErrorMessage(reloadError, uiText.status.actionFailed)); }
  };
  const save = async () => {
    const current = draft();
    const before = saved();
    if (!current || !before || saving()) return;
    setSaving(true);
    setError("");
    try {
      const result = await savePerformanceOverlaySettings(runtime.requestClient, current, before);
      setSaved(result);
      setDraft(cloneSettings(result));
    } catch (cause) {
      setError(cause instanceof OverlaySettingsSaveError
        ? uiText.performanceOverlay.modulesSavedSettingsFailed
        : userFacingErrorMessage(cause, uiText.status.actionFailed));
      await reloadAfterConflict(cause);
    } finally {
      setSaving(false);
    }
  };
  const resetPresentation = async () => {
    const before = saved();
    if (!draft() || !before || saving()) return;
    setSaving(true);
    setError("");
    try {
      const result = await resetPerformanceOverlayPresentation(runtime.requestClient, before);
      setSaved(result);
      setDraft(cloneSettings(result));
    } catch (cause) {
      setError(userFacingErrorMessage(cause, uiText.status.actionFailed));
      await reloadAfterConflict(cause);
    }
    finally { setSaving(false); }
  };
  // Show source, interval, and precision; explain the effective default when the interval is blank.
  const itemSummary = (item: PerformanceOverlayItem) => {
    const picker = uiText.targetedReport.itemPicker;
    const fallback = intervalPlaceholder(findCatalogEntry(catalog(), item.kind, item.id), overlayDefaultIntervalMs);
    const interval = item.kind === "frames" ? picker.perPresent
      : item.intervalMs !== null ? uiText.targetedReport.reportInfo.intervalValue(item.intervalMs)
        : fallback?.origin === "natural" ? picker.intervalNatural(fallback.intervalMs)
          : uiText.performanceOverlay.picker.intervalDefault(fallback?.intervalMs ?? overlayDefaultIntervalMs);
    return uiText.performanceOverlay.itemSummary(sourceName(item.source), interval, picker.precisions[item.syncPrecision]);
  };

  return (
    <section class="software-detail-section overlay-settings">
      <div class="software-policy-section-header">
        <h3>{uiText.performanceOverlay.tab}</h3>
        <button type="button" class="secondary" disabled={!draft() || saving()} onClick={() => void resetPresentation()}>
          {currentSettingsText().actions.restoreDefaults}
        </button>
        <button type="button" disabled={!draft() || !(dirty() || modulesMismatch()) || itemsIssue() !== null || saving()} onClick={() => void save()}>
          <StableLabel labels={{ idle: uiText.softwareDetail.save, busy: uiText.softwareDetail.processing }} current={saving() ? "busy" : "idle"} />
        </button>
      </div>
      <Show when={error()}>{(message) => <p class="software-detail-hint error" role="alert">{message()}</p>}</Show>
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
            <h4>{uiText.performanceOverlay.items}</h4>
            <button class="secondary" type="button" disabled={saving()} onClick={() => {
              setPickerItems(current().items.map((item) => ({ ...item })));
              setPickerIssues(false);
              setPickerOpen(true);
            }}>{uiText.performanceOverlay.chooseItems}</button>
          </div>
          <Show when={itemsIssue()}>
            {(issue) => <p class="software-detail-hint error" role="alert">{uiText.performanceOverlay.itemsInvalid[issue()]}</p>}
          </Show>
          {/* Item order is overlay drawing order. */}
          <ol class="overlay-settings-metrics">
            <For each={current().items}>
              {(item, index) => <li>
                <span>{overlayItemLabel(item.kind, item.id)}</span>
                <button class="secondary" type="button" disabled={saving() || index() === 0}
                  aria-label={uiText.performanceOverlay.moveUp} onClick={() => update({ items: moveView(current().items, index(), -1) })}>↑</button>
                <button class="secondary" type="button" disabled={saving() || index() === current().items.length - 1}
                  aria-label={uiText.performanceOverlay.moveDown} onClick={() => update({ items: moveView(current().items, index(), 1) })}>↓</button>
                <button class="secondary" type="button" disabled={saving()} aria-label={uiText.performanceOverlay.removeItem}
                  onClick={() => update({ items: current().items.filter((_, position) => position !== index()) })}>×</button>
                <small class="overlay-settings-item-summary">{itemSummary(item)}</small>
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
              <input disabled={saving()} type="number" min={saved()!.editingConstraints.fontSizePxMinimum} max={saved()!.editingConstraints.fontSizePxMaximum} ref={bindNumber("fontSizePx")}
                onInput={(event) => editNumber("fontSizePx", event.currentTarget)}
                onChange={(event) => showDraftNumber("fontSizePx", event.currentTarget)} />
            </label>
          </Show>
          <Show when={current().sizeMode === "windowRatio"}>
            <label>{uiText.performanceOverlay.regionWidthRatio}
              <input disabled={saving()} type="number" min={saved()!.editingConstraints.regionRatioMinimum} max={saved()!.editingConstraints.regionRatioMaximum} step={saved()!.editingConstraints.regionRatioStep} ref={bindNumber("regionWidthRatio")}
                onInput={(event) => editNumber("regionWidthRatio", event.currentTarget)}
                onChange={(event) => showDraftNumber("regionWidthRatio", event.currentTarget)} />
            </label>
            <label>{uiText.performanceOverlay.regionHeightRatio}
              <input disabled={saving()} type="number" min={saved()!.editingConstraints.regionRatioMinimum} max={saved()!.editingConstraints.regionRatioMaximum} step={saved()!.editingConstraints.regionRatioStep} ref={bindNumber("regionHeightRatio")}
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
            <input disabled={saving()} type="number" min={saved()!.editingConstraints.marginPxMinimum} max={saved()!.editingConstraints.marginPxMaximum} ref={bindNumber("marginPx")}
              onInput={(event) => editNumber("marginPx", event.currentTarget)}
                onChange={(event) => showDraftNumber("marginPx", event.currentTarget)} />
          </label>
        </div>}
      </Show>
      <Portal>
        <DialogRoot open={pickerOpen() && !saving()} labelledBy="overlayItemsPickerTitle" class="overlay-items-modal"
          dismissOnBackdrop={false} onDismiss={() => setPickerOpen(false)}>
          <DialogHeader title={uiText.performanceOverlay.pickerTitle} titleId="overlayItemsPickerTitle"
            onDismiss={() => setPickerOpen(false)} />
          <DialogBody class="overlay-items-body">
            <Show when={saved()?.editingConstraints}>{(constraints) =>
              <ItemPicker catalog={catalog()} use="overlay" items={pickerItems()} maximumItems={constraints().maximumItems}
                allowedKinds={itemKinds} defaultIntervalMs={overlayDefaultIntervalMs} nameOf={overlayItemLabel}
                onChange={(items) => setPickerItems(items)} onIssuesChange={setPickerIssues} />
            }</Show>
          </DialogBody>
          <DialogActions>
            <button class="secondary" type="button" disabled={saving()} onClick={() => setPickerOpen(false)}>{uiText.metricPicker.cancel}</button>
            <button type="button" disabled={pickerIssues() || saving()} onClick={() => {
              update({ items: pickerItems() });
              setPickerOpen(false);
            }}>{uiText.metricPicker.confirm}</button>
          </DialogActions>
        </DialogRoot>
      </Portal>
    </section>
  );
}

function cloneSettings(settings: PerformanceOverlaySettings): PerformanceOverlaySettings {
  return { ...settings, items: settings.items.map((item) => ({ ...item })) };
}
