import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { createEffect, createMemo, createSignal, For, Show } from "solid-js";
import { Portal } from "solid-js/web";
import { getPerformanceOverlaySettings, OverlayPreferencesSaveError, savePerformanceOverlaySettings, resetPerformanceOverlayPresentation } from "../../api.ts";
import { expectedOverlayInjectionModules } from "./startupModuleSelection.ts";
import { userFacingErrorMessage } from "../../presentation/userFacingText.ts";
import { StandardSelect } from "../../components/StandardSelect.tsx";
import { uiText, currentSettingsText } from "../../text.ts";
import type { PerformanceOverlayItem, PerformanceOverlaySettings } from "../../types.ts";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext.tsx";
import { DialogActions, DialogBody, DialogHeader, DialogRoot } from "../../ui/primitives/Dialog.tsx";
import { ItemPicker } from "../itemSelection/ItemPicker.tsx";
import { sourceName } from "../itemSelection/itemNames.ts";
import { findCatalogEntry, intervalPlaceholder, itemKinds, type CatalogEntry } from "../itemSelection/itemSelectionModel.ts";
import { getItemCatalog } from "../targetedReport/targetedRecordingApi.ts";
import { moveView } from "../targetedReport/views/viewModel.ts";
import { overlayItemLabel } from "./overlayItemLabels.ts";
import { maximumOverlayItems, overlayDefaultIntervalMs, overlayItemsIssue } from "./overlayItems.ts";

export function SoftwareOverlaySettings(props: { softwareId: string; active: boolean }) {
  const [saved, setSaved] = createSignal<PerformanceOverlaySettings | null>(null);
  const [draft, setDraft] = createSignal<PerformanceOverlaySettings | null>(null);
  // 浮层项的目录（和录制配置同一个目录，选择器只列出 uses 含 overlay 的项）。
  const [catalog, setCatalog] = createSignal<CatalogEntry[]>([]);
  const [loading, setLoading] = createSignal(false);
  const [saving, setSaving] = createSignal(false);
  const [pickerOpen, setPickerOpen] = createSignal(false);
  // 选择器里正在改的项：确定后写进草稿，取消就丢掉。
  const [pickerItems, setPickerItems] = createSignal<PerformanceOverlayItem[]>([]);
  const [pickerIssues, setPickerIssues] = createSignal(false);
  const [error, setError] = createSignal("");
  const runtime = useFrontendRuntime();
  let requestId = 0;
  const dirty = createMemo(() => JSON.stringify(saved()) !== JSON.stringify(draft()));
  // 草稿里的项不合规（旧缓存或别处写坏）时说明原因，不让保存。
  const itemsIssue = createMemo(() => {
    const current = draft();
    return current ? overlayItemsIssue(current.items) : null;
  });
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
    void Promise.all([getPerformanceOverlaySettings(props.softwareId), getItemCatalog(runtime.requestClient)])
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
      setDraft(cloneSettings(result));
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
  // 一项的来源、间隔、同步精度，写成一行跟在名称后面；留空的间隔写出实际会用的间隔和出处（自然间隔或浮层默认）。
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
          {/* 顺序就是浮层的绘制顺序，用上移、下移调整（和报告视图的排序一样）。 */}
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
      <Portal>
        <DialogRoot open={pickerOpen() && !saving()} labelledBy="overlayItemsPickerTitle" class="overlay-items-modal"
          dismissOnBackdrop={false} onDismiss={() => setPickerOpen(false)}>
          <DialogHeader title={uiText.performanceOverlay.pickerTitle} titleId="overlayItemsPickerTitle"
            onDismiss={() => setPickerOpen(false)} />
          <DialogBody class="overlay-items-body">
            <ItemPicker catalog={catalog()} use="overlay" items={pickerItems()} maximumItems={maximumOverlayItems}
              allowedKinds={itemKinds} defaultIntervalMs={overlayDefaultIntervalMs} nameOf={overlayItemLabel}
              onChange={(items) => setPickerItems(items)} onIssuesChange={setPickerIssues} />
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
