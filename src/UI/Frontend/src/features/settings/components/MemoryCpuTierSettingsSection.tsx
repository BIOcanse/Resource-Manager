import { createEffect, createMemo, For, on, Show } from "solid-js";
import { useFrontendRuntime } from "../../../frontendRuntime/FrontendRuntimeContext";
import { useFrontendWork } from "../../../frontendWork/FrontendWorkContext";
import { useFrontendVisibilityDemand } from "../../../frontendWork/useFrontendVisibilityDemand";
import { frontendVisibilitySurface } from "../../../frontendWork/frontendVisibilitySurface";
import { frontendWorkIds } from "../../../frontendWork/frontendWorkIds";
import { useSource } from "../../../frontendRuntime/source/useSource.ts";
import { sourceCanRender } from "../../../frontendRuntime/source/SourceSnapshot.ts";
import { StandardSelect } from "../../../components/StandardSelect";
import { formatBytes } from "../../../presentation/byteUnits.ts";
import { uiText } from "../../../text.ts";
import { isKnownMemoryCpuTierOptionName } from "../../../data/memoryCpuTier/memoryCpuTierOptionNames.ts";
import {
  memoryCpuTierCount, memoryCpuTierDefault,
  type MemoryCpuTierSettings, type MemoryCpuTierUnit, type MemoryCpuTierValue
} from "../../../data/memoryCpuTier/memoryCpuTierTypes.ts";
import {
  buildMemoryCpuTierView, isMemoryCpuTierValueInRange, memoryCpuTierEditableInteger, normalizeMemoryCpuTierSettings,
  withMemoryCpuTier, withMemoryCpuTierOverride, withoutMemoryCpuTierOverride, type MemoryCpuTierRow
} from "../memoryCpuTierModel.ts";
import { NumberField, SegmentedControl } from "./SettingsControls";

const tierNumbers = Array.from({ length: memoryCpuTierCount }, (_, index) => index + 1);

/**
 * 设置 → 内存与 CPU 取舍。档位和单独设置在设置草稿里，随设置页的保存、重新加载和恢复默认值一起走；
 * 选项列表完全按 GET /api/settings/memory-cpu-tier 的回复显示，前端不写死选项表。
 * 草稿和已保存设置不同时显示按 tiers[] 算出的预览；已保存设置一变（保存成功）就重读后端，显示后端的值。
 */
export function MemoryCpuTierSettingsSection(props: {
  settings: MemoryCpuTierSettings | undefined;
  committed: MemoryCpuTierSettings | undefined;
  onChange: (memoryCpuTier: MemoryCpuTierSettings) => void;
}) {
  const runtime = useFrontendRuntime();
  const frontendWork = useFrontendWork();
  // 档位和选项随这个分区的可见区域读取：常驻页隐藏时退掉，回来时重读。
  const workId = frontendWorkIds.settingsMemoryCpuTier;
  useFrontendVisibilityDemand(workId, [workId]);
  const source = useSource(runtime.sources.settingsReads.memoryCpuTier,
    () => ({ active: frontendWork.isNeeded(workId), refreshIntervalMs: null }));
  const reading = () => sourceCanRender(source.snapshot()) ? source.snapshot().data : null;
  const loading = () => source.snapshot().status === "idle" || source.snapshot().status === "loading";
  const failed = () => source.snapshot().status === "error" || source.snapshot().status === "unavailable";
  const text = () => uiText.memoryCpuTier;
  const draft = () => normalizeMemoryCpuTierSettings(props.settings);
  const committed = () => normalizeMemoryCpuTierSettings(props.committed);
  // 保存成功后已保存设置变了：重读后端，列表换成后端给出的生效值。
  createEffect(on(() => JSON.stringify(committed()), () => { void source.refresh(); }, { defer: true }));
  const view = createMemo(() => {
    const current = reading();
    return current ? buildMemoryCpuTierView(current, draft(), committed()) : null;
  });
  const names = createMemo(() => view()?.rows.map(row => row.name) ?? [], undefined,
    { equals: (left, right) => left.length === right.length && left.every((name, index) => name === right[index]) });

  const formatValue = (value: MemoryCpuTierValue | undefined, unit: MemoryCpuTierUnit | null) => {
    if (value === null || value === undefined) return text().unset;
    if (typeof value === "string") return text().choices[value] ?? value;
    if (unit === "bytes") return formatBytes(value, "memory");
    if (unit === "milliseconds") return text().milliseconds(String(value));
    if (unit === "seconds") return text().seconds(String(value));
    return String(value);
  };
  const optionTitle = (name: string) => isKnownMemoryCpuTierOptionName(name) ? text().options[name].title : name;
  const optionDescription = (name: string) => isKnownMemoryCpuTierOptionName(name) ? text().options[name].description : null;
  const change = (next: MemoryCpuTierSettings) => props.onChange(next);
  const setOverride = (row: MemoryCpuTierRow, value: MemoryCpuTierValue) => {
    // 范围外的值不进草稿，也就不会发给后端。
    if (isMemoryCpuTierValueInRange(row.range, value)) change(withMemoryCpuTierOverride(draft(), row.name, value));
  };

  return <section class="settings-section-panel memory-cpu-tier-settings" aria-label={uiText.memoryCpuTier.tierAriaLabel}
    {...frontendVisibilitySurface(workId, [workId])}>
    <p>{text().intro}</p>
    <div class="settings-row settings-row-block">
      <div class="settings-row-copy">
        <strong>{text().tierTitle}</strong>
        <span>{text().tierDescription}</span>
        <span data-memory-cpu-tier-saved>{text().savedTier(committed().tier)} · {text().defaultTier(memoryCpuTierDefault)}</span>
      </div>
      <div class="memory-cpu-tier-scale">
        <span>{text().tierLowEnd}</span>
        <SegmentedControl<string>
          value={String(draft().tier)}
          ariaLabel={text().tierAriaLabel}
          options={tierNumbers.map(tier => ({ id: String(tier), label: text().tierLabel(tier) }))}
          onChange={value => change(withMemoryCpuTier(draft(), Number(value)))}
        />
        <span>{text().tierHighEnd}</span>
      </div>
    </div>
    <div class="settings-row settings-row-block">
      <div class="settings-row-copy">
        <strong>{text().measuredTitle}</strong>
        <span>{text().measuredDescription}</span>
      </div>
      <ul class="memory-cpu-tier-measured" role="list" aria-label={text().measuredTitle}>
        <For each={tierNumbers}>{tier => <li data-memory-cpu-tier-measured={tier}>
          <span>{text().tierLabel(tier)}</span>
          <span>{text().notMeasured}</span>
        </li>}</For>
      </ul>
    </div>
    <Show when={loading()}><p role="status">{text().loading}</p></Show>
    <Show when={failed()}>
      <p role="alert">{text().loadFailed}</p>
      <button type="button" class="secondary-button memory-cpu-tier-retry" onClick={() => { void source.refresh(); }}>{text().retry}</button>
    </Show>
    <Show when={view()}>{currentView => <section class="memory-cpu-tier-options" aria-label={text().optionsTitle(currentView().tier)}>
      <h3>{text().optionsTitle(currentView().tier)}</h3>
      <Show when={currentView().preview}><p class="memory-cpu-tier-preview-note" role="status">{text().previewNote}</p></Show>
      <Show when={currentView().rows.length === 0}><p>{text().noOptions}</p></Show>
      <For each={names()}>{name => {
        const row = () => view()!.rows.find(item => item.name === name)!;
        const title = () => optionTitle(name);
        const description = () => optionDescription(name);
        const integerRange = () => { const range = row().range; return range.kind === "integer" ? range : null; };
        const choiceRange = () => { const range = row().range; return range.kind === "choice" ? range : null; };
        return <section class="memory-cpu-tier-option" aria-label={title()} data-memory-cpu-tier-option={name}>
          <div class="memory-cpu-tier-option-head">
            <strong>{title()}</strong>
            <Show when={description() === null}>
              <span class="control-capability-tag" data-tone="warning">{text().unknownOption}</span>
            </Show>
            <span class="control-capability-tag" data-memory-cpu-tier-category={row().category}
              data-tone={row().category === "aggressive" ? "warning" : "neutral"}>{text().categories[row().category]}</span>
            <span class="control-capability-tag" data-memory-cpu-tier-apply={row().apply}
              data-tone={row().apply === "restart" ? "info" : "neutral"}>{text().applyModes[row().apply]}</span>
            <span class="control-capability-tag" data-memory-cpu-tier-source={row().source}
              data-tone={row().source === "user" ? "info" : "neutral"}>{text().sources[row().source]}</span>
          </div>
          <Show when={description()}><span class="memory-cpu-tier-option-description">{description()}</span></Show>
          <div class="memory-cpu-tier-option-value">
            <Show when={row().showRunningValue} fallback={
              <span data-memory-cpu-tier-value>{formatValue(row().value, row().unit)}</span>}>
              <span data-memory-cpu-tier-running>{text().currentlyRunning(formatValue(row().runningValue, row().unit))}</span>
              <span data-memory-cpu-tier-value>{text().afterRestart(formatValue(row().value, row().unit))}</span>
              <span class="memory-cpu-tier-restart-note">{text().restartNote}</span>
            </Show>
          </div>
          <div class="settings-row memory-cpu-tier-option-edit">
            <Show when={integerRange()}>{range => <>
              <NumberField label={text().valueLabel} ariaLabel={`${title()} · ${text().valueLabel}`}
                value={memoryCpuTierEditableInteger(row())} min={range().min} max={range().max} step={1}
                disabled={row().value === null}
                onChange={value => setOverride(row(), value)} />
              <Show when={range().unset}>
                <label class="memory-cpu-tier-unset">
                  <input type="checkbox" aria-label={`${title()} · ${text().unset}`} checked={row().value === null}
                    onChange={event => setOverride(row(), event.currentTarget.checked ? null : memoryCpuTierEditableInteger(row()))} />
                  <span>{text().unset}</span>
                </label>
              </Show>
            </>}</Show>
            <Show when={choiceRange()}>{range => <label class="settings-select settings-inline-select">
              <span>{text().choiceLabel}</span>
              <StandardSelect<string>
                value={typeof row().value === "string" ? row().value as string : ""}
                ariaLabel={`${title()} · ${text().choiceLabel}`}
                options={range().choices.map(choice => ({ value: choice, label: text().choices[choice] ?? choice }))}
                onChange={value => setOverride(row(), value)} />
            </label>}</Show>
            <Show when={row().overridden}>
              <button type="button" class="secondary-button" aria-label={`${title()} · ${text().clear}`}
                onClick={() => change(withoutMemoryCpuTierOverride(draft(), name))}>{text().clear}</button>
            </Show>
          </div>
        </section>;
      }}</For>
    </section>}</Show>
  </section>;
}
