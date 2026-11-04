import { createMemo, createResource, createSignal, createUniqueId, For, onMount, Show, type JSX } from "solid-js";
import { StandardSelect } from "../../components/StandardSelect";
import {
  freezeSettingsDraftView,
  freezeSettingsWithChange,
  mergeFreezeSettingsChange,
  parseEventIds,
  readFreezeSettings,
  saveFreezeSettings,
  type FreezeSettings,
  type FreezeSettingsChange,
  type SystemEventRule
} from "../../data/freezeReports/freezeSettings.ts";
import { systemEventChannels, systemEventRuleCategories, systemEventTargetKinds } from "../../data/freezeReports/systemEvents.ts";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import { CurrentPageHeading } from "../../app/navigation/currentPageName.tsx";
import { ContentState } from "../../ui/patterns/ContentState.tsx";
import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { NumberField } from "../settings/components/SettingsControls.tsx";
import { uiText } from "../../text.ts";

/* Edit a backend-supplied settings document and save it whole. Disable fields the backend locks while enabled; defaults and thresholds remain backend-owned. */
export function FreezeSettingsPage(props: { readonly part: "systemEvents" | "freezeRecorder"; readonly onBack: () => void }) {
  const runtime = useFrontendRuntime();
  const copy = () => uiText.freezeReports.settings;
  const [loaded] = createResource(() => readFreezeSettings(runtime.requestClient));
  const [savedOverride, setSavedOverride] = createSignal<FreezeSettings | null>(null);
  const saved = () => savedOverride() ?? (loaded.error ? null : loaded.latest ?? null);
  const [change, setChange] = createSignal<FreezeSettingsChange>({});
  const [saving, setSaving] = createSignal(false);
  const [result, setResult] = createSignal<"saved" | "invalid" | "conflict" | "resource-bound" | "failed" | null>(null);
  // Validate the saved document plus draft changes with the same decoder before allowing save.
  const draft = createMemo(() => {
    const settings = saved();
    if (!settings) return null;
    try {
      return freezeSettingsWithChange(settings, change());
    } catch {
      return null;
    }
  });
  const shown = createMemo(() => {
    const settings = saved();
    return settings ? freezeSettingsDraftView(settings, change()) : null;
  });
  const dirty = () => Object.keys(change()).length > 0;
  const edit = (next: FreezeSettingsChange) => {
    setChange((current) => mergeFreezeSettingsChange(current, next));
    setResult(null);
  };
  const save = async () => {
    const settings = saved();
    if (!settings || saving() || !draft()) return;
    setSaving(true);
    try {
      const outcome = await saveFreezeSettings(runtime.requestClient, settings.wire, change());
      if (outcome.status === "saved") {
        setSavedOverride(outcome.settings);
        setChange({});
      }
      setResult(outcome.status);
    } catch {
      setResult("failed");
    } finally {
      setSaving(false);
    }
  };
  const resultText = () => {
    switch (result()) {
      case "saved": return copy().saved;
      case "invalid": return copy().invalid;
      case "conflict": return copy().conflict;
      case "resource-bound": return copy().resourceBound;
      case "failed": return copy().saveFailed;
      default: return draft() === null && saved() ? copy().draftInvalid : "";
    }
  };
  const titleId = `freeze-settings-title-${createUniqueId()}`;
  let title: HTMLHeadingElement | undefined;
  onMount(() => title?.focus());

  const rows = () => props.part === "systemEvents" ? systemEventRows(saved, edit) : recorderRows(saved, edit);
  return (
    <article class="freeze-report-subpage freeze-settings" aria-labelledby={titleId} aria-busy={saved() ? undefined : "true"}>
      <div class="panel-header freeze-report-subpage-header">
        <CurrentPageHeading />
        <button class="secondary freeze-report-back" type="button" onClick={() => props.onBack()}>{copy().back}</button>
        <h3 ref={title} id={titleId} class="freeze-report-subpage-title" tabindex="-1">
          {props.part === "systemEvents" ? copy().systemEventsTitle : copy().recorderTitle}
        </h3>
        <div class="freeze-report-header-actions">
          <button class="secondary panel-refresh-button" type="button" disabled={!dirty() || saving()}
            onClick={() => { setChange({}); setResult(null); }}>{copy().discard}</button>
          <button class="panel-refresh-button" type="button" disabled={!dirty() || saving() || !draft()} onClick={() => void save()}>
            <StableLabel labels={{ idle: copy().save, busy: copy().saving }} current={saving() ? "busy" : "idle"} />
          </button>
        </div>
      </div>
      <Show when={loaded.error && !savedOverride()}>
        <ContentState kind="unavailable" title={copy().readFailed} />
      </Show>
      <Show when={!loaded.error || savedOverride()}>
        {/* Reserve result-row height to prevent content shifts. */}
        <p class="freeze-report-note freeze-settings-result" classList={{ warning: result() !== null && result() !== "saved" || (draft() === null && !!saved()) }}
          role="status">{resultText()}</p>
        <div class="settings-section-panel freeze-settings-rows">
          <For each={rows()}>
            {(row) => (
              <div class="settings-row" classList={{ "settings-row-block": row.block === true }} data-freeze-setting={row.id}>
                <div class="settings-row-copy">
                  <strong>{row.title}</strong>
                  <span>{row.description}</span>
                  <Show when={row.lockedNote?.()}>{(note) => <span class="freeze-settings-lock">{note()}</span>}</Show>
                </div>
                <Show when={shown()} fallback={<span class="freeze-settings-control-placeholder" aria-hidden="true" />}>
                  {(settings) => row.control(settings)}
                </Show>
              </div>
            )}
          </For>
        </div>
      </Show>
    </article>
  );
}

interface SettingRow {
  readonly id: string;
  readonly title: string;
  readonly description: string;
  readonly block?: boolean;
  readonly lockedNote?: () => string | null;
  readonly control: (settings: () => FreezeSettings) => JSX.Element;
}

type Edit = (change: FreezeSettingsChange) => void;
type Read = () => FreezeSettings | null;

// Match the unsigned 32-bit wire limit; the backend enforces resource limits.
const integerMaximum = 4_294_967_295;

function Switch(props: { readonly label: string; readonly checked: boolean; readonly onChange: (checked: boolean) => void }) {
  return (
    <label class="settings-switch">
      <input type="checkbox" aria-label={props.label} checked={props.checked} onChange={(event) => props.onChange(event.currentTarget.checked)} />
      <span />
    </label>
  );
}

function systemEventRows(saved: Read, edit: Edit): SettingRow[] {
  const copy = uiText.freezeReports.settings;
  const fields = copy.systemEvents;
  const units = copy.units;
  const rulesLocked = () => saved()?.systemEvents.enabled === true;
  return [
    { id: "enabled", ...fields.enabled, control: (settings) =>
      <Switch label={fields.enabled.title} checked={settings().systemEvents.enabled} onChange={(enabled) => edit({ systemEvents: { enabled } })} /> },
    { id: "history-after", ...fields.historyAfter, control: (settings) =>
      <NumberField label={units.minutes} ariaLabel={fields.historyAfter.title} value={settings().systemEvents.historyAfterMs / 60_000}
        min={1} max={Math.floor(Number.MAX_SAFE_INTEGER / 60_000)} step={1}
        onChange={(minutes) => edit({ systemEvents: { history_after_ms: Math.round(minutes * 60_000) } })} /> },
    { id: "event-tail-capacity", ...fields.eventTailCapacity, control: (settings) =>
      <NumberField label={units.count} ariaLabel={fields.eventTailCapacity.title} value={settings().systemEvents.eventTailCapacity}
        min={1} max={integerMaximum} step={1} onChange={(value) => edit({ systemEvents: { event_tail_capacity: value } })} /> },
    { id: "entry-capacity", ...fields.entryCapacity, control: (settings) =>
      <NumberField label={units.count} ariaLabel={fields.entryCapacity.title} value={settings().systemEvents.entryCapacity}
        min={1} max={integerMaximum} step={1} onChange={(value) => edit({ systemEvents: { entry_capacity: value } })} /> },
    { id: "live-kernel-root", ...fields.liveKernelRoot, control: (settings) =>
      <input class="settings-text-field" type="text" aria-label={fields.liveKernelRoot.title} value={settings().systemEvents.liveKernelRoot}
        onChange={(event) => edit({ systemEvents: { live_kernel_root: event.currentTarget.value.trim() } })} /> },
    { id: "live-kernel-directories", ...fields.liveKernelDirectories, control: (settings) =>
      <textarea class="settings-text-field freeze-settings-lines" aria-label={fields.liveKernelDirectories.title}
        rows={Math.max(3, settings().systemEvents.liveKernelDirectories.length)}
        value={settings().systemEvents.liveKernelDirectories.join("\n")}
        onChange={(event) => edit({ systemEvents: { live_kernel_directories: event.currentTarget.value.split(/\r?\n/).map((line) => line.trim()).filter(Boolean) } })} /> },
    { id: "live-kernel-entry-capacity", ...fields.liveKernelEntryCapacity, control: (settings) =>
      <NumberField label={units.count} ariaLabel={fields.liveKernelEntryCapacity.title} value={settings().systemEvents.liveKernelEntryCapacity}
        min={1} max={integerMaximum} step={1} onChange={(value) => edit({ systemEvents: { live_kernel_entry_capacity: value } })} /> },
    { id: "rules", ...fields.rules, block: true, lockedNote: () => rulesLocked() ? copy.rulesLocked : null, control: (settings) =>
      <RuleTable rules={settings().systemEvents.rules} disabled={rulesLocked()} onChange={(rules) => edit({ systemEvents: { rules } })} /> }
  ];
}

function recorderRows(saved: Read, edit: Edit): SettingRow[] {
  const copy = uiText.freezeReports.settings;
  const fields = copy.recorder;
  const units = copy.units;
  const fileLocked = () => saved()?.freezeRecorder.enabled === true;
  const percent = (title: string, value: () => number, onChange: (value: number) => void) =>
    <NumberField label={units.percent} ariaLabel={title} value={value()} min={0.1} max={100} step={0.1} onChange={onChange} />;
  return [
    { id: "enabled", ...fields.enabled, control: (settings) =>
      <Switch label={fields.enabled.title} checked={settings().freezeRecorder.enabled} onChange={(enabled) => edit({ freezeRecorder: { enabled } })} /> },
    { id: "sample-interval", ...fields.sampleInterval, control: (settings) =>
      <NumberField label={units.milliseconds} ariaLabel={fields.sampleInterval.title} value={settings().freezeRecorder.sampleIntervalMs}
        min={1} max={integerMaximum} step={1} onChange={(value) => edit({ freezeRecorder: { sample_interval_ms: value } })} /> },
    { id: "review-window", ...fields.reviewWindow, control: (settings) =>
      <NumberField label={units.seconds} ariaLabel={fields.reviewWindow.title} value={settings().freezeRecorder.reviewWindowMs / 1000}
        min={1} max={Math.floor(integerMaximum / 1000)} step={1}
        onChange={(seconds) => edit({ freezeRecorder: { review_window_ms: Math.round(seconds * 1000) } })} /> },
    { id: "commit-immediate", ...fields.commitImmediate, control: (settings) =>
      <span class="freeze-settings-readonly">{commitImmediateText(settings().freezeRecorder.commitImmediatePercent, fields.commitImmediateNone)}</span> },
    { id: "physical-available", ...fields.physicalAvailable, control: (settings) =>
      percent(fields.physicalAvailable.title, () => settings().freezeRecorder.physicalAvailablePercent, (value) => edit({ freezeRecorder: { physical_available_percent: value } })) },
    { id: "nonpaged-pool", ...fields.nonpagedPool, control: (settings) =>
      percent(fields.nonpagedPool.title, () => settings().freezeRecorder.nonpagedPoolPercent, (value) => edit({ freezeRecorder: { nonpaged_pool_percent: value } })) },
    { id: "storage-consecutive", ...fields.storageConsecutive, control: (settings) =>
      <NumberField label={units.count} ariaLabel={fields.storageConsecutive.title} value={settings().freezeRecorder.storageUnresponsiveConsecutive}
        min={1} max={integerMaximum} step={1} onChange={(value) => edit({ freezeRecorder: { storage_unresponsive_consecutive: value } })} /> },
    { id: "slots", ...fields.slots, lockedNote: () => fileLocked() ? copy.recorderLocked : null, control: (settings) =>
      <NumberField label={units.count} ariaLabel={fields.slots.title} value={settings().freezeRecorder.slots} disabled={fileLocked()}
        min={1} max={integerMaximum} step={1} onChange={(value) => edit({ freezeRecorder: { slots: value } })} /> },
    { id: "ring-path", ...fields.ringPath, lockedNote: () => fileLocked() ? copy.recorderLocked : null, control: (settings) =>
      <input class="settings-text-field" type="text" aria-label={fields.ringPath.title} disabled={fileLocked()}
        placeholder={fields.ringPathDefault} value={settings().ringPath ?? ""}
        onChange={(event) => edit({ ringPath: event.currentTarget.value.trim() || null })} /> }
  ];
}

/** Show the memory-protection threshold as a percentage, or its unavailable explanation. */
function commitImmediateText(percent: number | null, none: string): string {
  return percent === null ? none : `${Number(percent.toFixed(2))}%`;
}

/** Save the entire event-rule table together. */
function RuleTable(props: { readonly rules: readonly SystemEventRule[]; readonly disabled: boolean; readonly onChange: (rules: SystemEventRule[]) => void }) {
  const copy = () => uiText.freezeReports.settings.ruleTable;
  const events = () => uiText.freezeReports.systemEvents;
  const replace = (index: number, next: Partial<SystemEventRule>) =>
    props.onChange(props.rules.map((rule, position) => position === index ? { ...rule, ...next } : rule));
  return (
    <div class="freeze-rule-table" role="table" aria-label={uiText.freezeReports.settings.systemEvents.rules.title}>
      <div class="freeze-rule-row freeze-rule-head" role="row">
        <span role="columnheader">{copy().channel}</span>
        <span role="columnheader">{copy().provider}</span>
        <span role="columnheader">{copy().ids}</span>
        <span role="columnheader">{copy().category}</span>
        <span role="columnheader">{copy().targetKind}</span>
        <span role="columnheader"><span class="sr-only">{copy().remove}</span></span>
      </div>
      <For each={props.rules}>
        {(rule, index) => (
          <div class="freeze-rule-row" role="row" data-freeze-rule={index()}>
            <span role="cell">
              <StandardSelect value={rule.channel} ariaLabel={copy().channel} disabled={props.disabled}
                options={systemEventChannels.map((channel) => ({ value: channel, label: events().channelName[channel] }))}
                onChange={(channel) => replace(index(), { channel })} />
            </span>
            <span role="cell">
              <input class="settings-text-field" type="text" aria-label={copy().provider} disabled={props.disabled} value={rule.provider}
                onChange={(event) => replace(index(), { provider: event.currentTarget.value.trim() })} />
            </span>
            <span role="cell">
              <input class="settings-text-field" type="text" inputMode="numeric" aria-label={copy().ids} disabled={props.disabled}
                value={rule.ids.join(", ")} placeholder={copy().idsHint}
                onChange={(event) => {
                  const ids = parseEventIds(event.currentTarget.value);
                  if (ids) replace(index(), { ids });
                  else event.currentTarget.value = rule.ids.join(", ");
                }} />
            </span>
            <span role="cell">
              <StandardSelect value={rule.category} ariaLabel={copy().category} disabled={props.disabled}
                options={systemEventRuleCategories.map((category) => ({ value: category, label: events().category[category] }))}
                onChange={(category) => replace(index(), { category })} />
            </span>
            <span role="cell">
              <StandardSelect value={rule.targetKind} ariaLabel={copy().targetKind} disabled={props.disabled}
                options={systemEventTargetKinds.map((kind) => ({ value: kind, label: events().targetKind[kind] }))}
                onChange={(targetKind) => replace(index(), { targetKind })} />
            </span>
            <span role="cell">
              <button class="secondary" type="button" disabled={props.disabled}
                onClick={() => props.onChange(props.rules.filter((_, position) => position !== index()))}>{copy().remove}</button>
            </span>
          </div>
        )}
      </For>
      <div class="freeze-rule-actions">
        <button class="secondary" type="button" disabled={props.disabled}
          onClick={() => props.onChange([...props.rules, { channel: "system", provider: "", ids: [], category: "storage_retry_reset", targetKind: "machine" }])}>
          {copy().add}
        </button>
      </div>
    </div>
  );
}

