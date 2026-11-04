import { usePushSource } from "../../frontendRuntime/push/usePushSource.ts";
import { localizedMetricLabel, metricUnitLabel } from "../../presentation/metricLabels";
import { ChevronDown, ChevronRight, PackagePlus, Search, X } from "lucide-solid";
import { createEffect, createMemo, createSelector, createSignal, For, Index, on, Show, untrack } from "solid-js";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext.tsx";
import { useFrontendReadIntervals } from "../../app/FrontendReadIntervalsContext.tsx";
import {
  componentDisplayName,
  metricDisabledReasonCode,
  userFacingMetricGroup,
  userFacingMetricUnavailableReason,
  userFacingState
} from "../../presentation/userFacingText";
import type { ManagedComponent, MetricDefinition } from "../../types";
import {
  DialogActions,
  DialogBody,
  DialogHeader,
  DialogRoot
} from "../../ui/primitives/Dialog.tsx";
import {
  activeDescendantOptionId,
  resolveActiveDescendantTarget
} from "../../ui/primitives/activeDescendantListbox.ts";
import { isComponentInstalled, textOrEmpty } from "../../utils";
import { uiText } from "../../text.ts";
import { deviceFieldScopeKind, fieldHint } from "./fieldCards.ts";
import { gpuAdapterNames, gpuNameForMetric } from "./gpuAdapterNames.ts";
import {
  groupMetricPickerOptions,
  metricPickerGroupExpanded,
  metricPickerDeviceGroupClass,
  metricPickerNavigationIds,
  metricPickerTextsMatch,
  normalizeMetricPickerQuery,
  type MetricPickerFoldState
} from "./metricPickerList.ts";
import { ReadingSourceMark } from "../../components/ReadingSourceMark.tsx";
import { RadioGroupItem, RadioGroupRoot } from "../../ui/primitives/RadioGroup.tsx";
import { metricSourceChoiceLabel, metricSourceChoices, type MetricSourceChoice } from "./metricSources.ts";
import { HelpHint } from "../../ui/primitives/HelpHint.tsx";

interface MetricModalProps {
  open: boolean;
  catalog: MetricDefinition[];
  components: ManagedComponent[];
  selectedMetricId: string | null;
  selectedMetricIds?: readonly string[];
  multiSelect?: boolean;
  /** Use the complete catalog to offer automatic or explicit sources for single selection. */
  sourceCatalog?: readonly MetricDefinition[];
  onSelect: (metricId: string) => void;
  onClose: () => void;
  onConfirm: () => void;
  onDependencyPrompt: (metric: MetricDefinition, dependency: MetricDependencyState) => void;
}

interface MetricOptionGroup {
  /** Backend grouping key used for default expansion. */
  rawGroup: string;
  group: string;
  metrics: readonly MetricDefinition[];
  detail: string | null;
  missingComponents: string[];
}

export interface MetricDependencyState {
  componentId: string;
  name: string;
  stateLabel: string;
  message: string;
  missing: boolean;
}

/** Tooltips preserve full labels, disabled reasons, raw reason codes, and field or component details. */
function optionTitle(metric: MetricDefinition, dependency: MetricDependencyState | null, unavailable: boolean): string {
  const reason = unavailable ? userFacingMetricUnavailableReason(metric.requiredComponentName, metric.disabledReason) : null;
  const code = unavailable && metric.disabledReason ? metricDisabledReasonCode(metric.disabledReason) : null;
  return [
    localizedMetricLabel(metric.id, metric.label),
    reason,
    // Unknown reasons already contain their code.
    code === reason ? null : code,
    metric.scopeKind === deviceFieldScopeKind ? metric.detail : null,
    dependency && !dependency.missing ? `${dependency.name} · ${dependency.stateLabel}` : null
  ].filter(Boolean).join("\n");
}

/** Append a GPU name only when every item belongs to the same indexed GPU. */
function gpuGroupName(metrics: readonly MetricDefinition[], names: ReadonlyMap<string, string>): string | null {
  const index = /^gpu\.(\d+)$/i.exec(metrics[0]?.group.trim() ?? "")?.[1];
  return index !== undefined && metrics.every((metric) => metric.group.trim() === metrics[0].group.trim())
    ? names.get(index) ?? null : null;
}

export function MetricModal(props: MetricModalProps) {
  const runtime = useFrontendRuntime();
  const readIntervals = useFrontendReadIntervals();
  const catalogSource = usePushSource(runtime.sources.metricFieldCatalog, () => ({
    active: props.open, intervalMs: readIntervals.current()?.monitor
  }));
  const devices = () => catalogSource.snapshot().data?.fields ?? [];
  // Use backend indices and catalog names for GPU group labels.
  const gpuNames = createMemo(() => gpuAdapterNames(props.catalog, devices()));
  const listboxId = "metric-modal-options";
  let searchInput: HTMLInputElement | undefined;
  let listbox: HTMLDivElement | undefined;
  const [query, setQuery] = createSignal("");
  const [activeMetricId, setActiveMetricId] = createSignal<string | null>(null);
  // Selectors update only changed selections in large lists.
  const isActiveMetric = createSelector(activeMetricId);
  const isSelectedMetric = createSelector(() => props.selectedMetricId);
  const selectedMetric = () => props.catalog.find((metric) => metric.id === props.selectedMetricId) ?? null;
  const isSelected = (id: string) => props.multiSelect
    ? props.selectedMetricIds?.includes(id) ?? false
    : isSelectedMetric(id);
  const metricUnavailable = (metric: MetricDefinition) =>
    metric.selectable === false && !(dependencyFor(metric)?.missing ?? false);
  const selectedMetricUnavailable = () => {
    const metric = selectedMetric();
    return metric ? metricUnavailable(metric) : false;
  };
  // Index components once by lowercase ID for dependency lookup.
  const componentsById = createMemo(() => {
    const byId = new Map<string, ManagedComponent>();
    for (const component of props.components) {
      const id = textOrEmpty(component.definition?.id).toLowerCase();
      if (!byId.has(id)) {
        byId.set(id, component);
      }
    }
    return byId;
  });
  // Resolve device names by backend identity, falling back to ScopeKey.
  const deviceNames = createMemo(() => {
    const names = new Map<string, string>();
    for (const device of devices()) {
      const name = device.displayName?.trim();
      if (name) names.set(device.deviceIdentity, name);
    }
    return names;
  });
  const deviceNameFor = (metric: MetricDefinition) => gpuNameForMetric(metric.id, gpuNames())
    ?? deviceNames().get(metric.deviceIdentity ?? metric.scopeKey ?? "") ?? null;
  // Use localized field details for device fields and bound catalog items.
  const hintFor = (metric: MetricDefinition) => metric.scopeKind === deviceFieldScopeKind
    ? metric.detail ?? null
    : metric.fieldName ? fieldHint(metric.fieldName) : null;
  const filter = createMemo(() => normalizeMetricPickerQuery(query()));
  // Organize the list only while the dialog is open.
  const filteredMetrics = createMemo(() => {
    if (!props.open) {
      return [];
    }
    const current = filter();
    if (!current) {
      return props.catalog;
    }
    return props.catalog.filter((metric) => metricPickerTextsMatch([
      localizedMetricLabel(metric.id, metric.label),
      metric.id,
      userFacingMetricGroup(metric.group),
      deviceNameFor(metric),
      hintFor(metric)
    ], current));
  });
  // Reset expansion overrides when the query changes or the dialog closes.
  const [foldOverrides, setFoldOverrides] = createSignal<ReadonlyMap<string, boolean>>(new Map());
  const foldState = createMemo<MetricPickerFoldState>(() => ({ filter: filter(), overrides: foldOverrides() }));
  const changeQuery = (value: string) => {
    setQuery(value);
    setFoldOverrides(new Map());
  };
  const toggleGroup = (key: string, expanded: boolean) =>
    setFoldOverrides((previous) => new Map(previous).set(key, !expanded));
  const groupKeyOf = (metric: MetricDefinition) =>
    metric.scopeKind === deviceFieldScopeKind ? `device:${metric.scopeKey}` : userFacingMetricGroup(metric.group);
  // Selecting a source also selects items for the same reading.
  const sourceChoices = createMemo<readonly MetricSourceChoice[]>(() =>
    props.open && !props.multiSelect && props.sourceCatalog
      ? metricSourceChoices(props.selectedMetricId, props.sourceCatalog) : []);
  const sourceChoiceIds = createMemo(() => new Set(sourceChoices().map((choice) => choice.metricId)));
  const sourceValue = () => {
    const index = sourceChoices().findIndex((choice) => choice.metricId === props.selectedMetricId);
    return index < 0 ? "" : `source-${index}`;
  };
  // Key groups and retain option objects so search and refresh preserve nodes.
  // Group fields by device identity, independently of display names.
  const deviceKinds = createMemo(() => new Map(devices().map((device) => [device.deviceIdentity, device.kind])));
  const metricGroups = createMemo(() => groupMetricPickerOptions(filteredMetrics(), groupKeyOf, (metric) =>
    metric.scopeKind === deviceFieldScopeKind
      ? metricPickerDeviceGroupClass(deviceKinds().get(metric.deviceIdentity ?? metric.scopeKey ?? "")) : metric.group));
  // Keyboard navigation skips collapsed groups.
  const enabledMetricIds = createMemo(() =>
    metricPickerNavigationIds(metricGroups(), foldState(), (metric) => !metricUnavailable(metric)));
  const groupedMetrics = createMemo(() => {
    const byKey = new Map<string, MetricOptionGroup>();
    for (const { key, group: rawGroup, metrics } of metricGroups()) {
      byKey.set(key, {
        rawGroup,
        group: userFacingMetricGroup(metrics[0].group),
        metrics,
        detail: metrics[0].scopeKind === deviceFieldScopeKind ? uiText.metricPicker.deviceFields : gpuGroupName(metrics, gpuNames()),
        // Show missing components once in the group heading.
        missingComponents: [...new Set(metrics
          .filter((metric) => !metricUnavailable(metric))
          .map((metric) => dependencyFor(metric))
          .filter((dependency) => dependency?.missing)
          .map((dependency) => dependency!.name))]
      });
    }
    return { keys: [...byKey.keys()], byKey };
  });

  function dependencyFor(metric: MetricDefinition): MetricDependencyState | null {
    const componentId = metric.requiredComponentId;
    if (!componentId) {
      return null;
    }

    const component = componentsById().get(componentId.toLowerCase());
    const name = componentDisplayName(
      componentId,
      metric.requiredComponentName ?? component?.definition?.name);
    if (!component) {
      return {
        componentId,
        name,
        stateLabel: uiText.metricPicker.notRead,
        message: "",
        missing: true
      };
    }

    return {
      componentId,
      name,
      stateLabel: userFacingState(component.state),
      message: "",
      missing: !isComponentInstalled(component)
    };
  }

  // Initially expand selected groups so the keyboard starting item is visible; reset on query changes.
  createEffect(on(() => props.open, (open) => {
    if (!open) {
      setFoldOverrides(new Map());
      return;
    }
    untrack(() => {
      const selected = new Set([props.selectedMetricId, ...(props.selectedMetricIds ?? [])].filter(Boolean));
      const reveal = new Map<string, boolean>();
      for (const group of metricGroups()) {
        if (!metricPickerGroupExpanded(group, foldState()) && group.metrics.some((metric) => selected.has(metric.id))) {
          reveal.set(group.key, true);
        }
      }
      setFoldOverrides(reveal);
    });
  }));

  createEffect(() => {
    if (!props.open) {
      setQuery("");
      setActiveMetricId(null);
      return;
    }

    const ids = enabledMetricIds();
    const selectedId = props.selectedMetricId;
    const next = selectedId && ids.includes(selectedId) ? selectedId : ids[0] ?? null;
    if (!activeMetricId() || !ids.includes(activeMetricId()!)) {
      setActiveMetricId(next);
    }
  });

  const chooseMetric = (metric: MetricDefinition) => {
    if (metricUnavailable(metric)) {
      return;
    }

    const dependency = dependencyFor(metric);
    if (dependency?.missing) {
      props.onDependencyPrompt(metric, dependency);
      return;
    }

    props.onSelect(metric.id);
  };

  const moveActiveMetric = (key: string) => {
    const target = resolveActiveDescendantTarget(enabledMetricIds(), activeMetricId(), key);
    if (!target) {
      return false;
    }

    setActiveMetricId(target.id);
    queueMicrotask(() => listbox
      ?.querySelector<HTMLElement>(`#${CSS.escape(activeDescendantOptionId(listboxId, target.id))}`)
      ?.scrollIntoView({ block: "nearest" }));
    return true;
  };

  const handleSearchKeyDown = (event: KeyboardEvent) => {
    if (event.key === " ") {
      return;
    }

    if (!resolveActiveDescendantTarget(enabledMetricIds(), activeMetricId(), event.key)) {
      return;
    }

    event.preventDefault();
    if (event.key === "Enter" || event.key === " ") {
      const metric = props.catalog.find((candidate) => candidate.id === activeMetricId());
      if (metric) {
        chooseMetric(metric);
      }
      return;
    }

    moveActiveMetric(event.key);
  };

  return (
    <DialogRoot
      id="metricModal"
      open={props.open}
      labelledBy="metricModalTitle"
      class="metric-modal"
      initialFocus={() => searchInput}
      onDismiss={props.onClose}
    >
      <DialogHeader
        title={uiText.metricPicker.title}
        titleId="metricModalTitle"
        onDismiss={props.onClose}
      />
      <DialogBody class={sourceChoices().length > 0 ? "metric-picker with-source" : "metric-picker"}>
        <div class="metric-picker-toolbar">
          <label class="metric-picker-search">
            <Search size={16} aria-hidden="true" />
            <input
              ref={searchInput}
              type="search"
              role="combobox"
              value={query()}
              placeholder={uiText.metricPicker.searchPlaceholder}
              aria-label={uiText.metricPicker.searchLabel}
              aria-expanded="true"
              aria-autocomplete="list"
              aria-controls={listboxId}
              aria-activedescendant={activeMetricId()
                ? activeDescendantOptionId(listboxId, activeMetricId()!)
                : undefined}
              onInput={(event) => changeQuery(event.currentTarget.value)}
              onKeyDown={handleSearchKeyDown}
            />
            <Show when={query()}>
              <button
                class="icon-button metric-picker-clear"
                type="button"
                aria-label={uiText.metricPicker.clearSearchLabel}
                data-name-tooltip=""
                onClick={() => {
                  changeQuery("");
                  searchInput?.focus();
                }}
              >
                <X size={15} aria-hidden="true" />
              </button>
            </Show>
          </label>
          <span class="metric-picker-count" role="status">{uiText.metricPicker.count(filteredMetrics().length)}</span>
        </div>
        <div
          ref={listbox}
          id={listboxId}
          class="metric-options"
          role="listbox"
          aria-multiselectable={props.multiSelect || undefined}
          aria-label={uiText.metricPicker.listLabel}
        >
          <For each={groupedMetrics().keys}>
            {(key, groupIndex) => {
              // Retain group nodes while their keys remain present.
              const group = createMemo<MetricOptionGroup>(
                (previous) => groupedMetrics().byKey.get(key) ?? previous,
                groupedMetrics().byKey.get(key)!);
              const headingId = () => `metric-modal-group-${groupIndex()}`;
              const gridId = () => `metric-modal-group-options-${groupIndex()}`;
              const expanded = () => metricPickerGroupExpanded({ key, group: group().rawGroup }, foldState());
              return (
                <section class="metric-option-group-block" classList={{ folded: !expanded() }} role="group" aria-labelledby={headingId()}>
                  <div class="metric-option-group-heading">
                    {/* Use labelled groups within the listbox. Group toggles are keyboard reachable; collapsed options are unmounted and skipped. */}
                    <button
                      id={headingId()}
                      type="button"
                      class="metric-option-group-title metric-option-group-toggle"
                      aria-expanded={expanded()}
                      aria-controls={expanded() ? gridId() : undefined}
                      onClick={() => toggleGroup(key, expanded())}
                    >
                      <Show when={expanded()} fallback={<ChevronRight size={14} aria-hidden="true" />}>
                        <ChevronDown size={14} aria-hidden="true" />
                      </Show>
                      <span>
                        {group().group}
                        <Show when={group().detail}>
                          {(detail) => <span class="metric-option-group-detail"> · {detail()}</span>}
                        </Show>
                      </span>
                    </button>
                    <For each={group().missingComponents}>
                      {(name) => (
                        <small class="metric-option-group-note">
                          <PackagePlus size={13} aria-hidden="true" />
                          {uiText.metricPicker.needsInstall(name)}
                        </small>
                      )}
                    </For>
                  </div>
                  <Show when={expanded()}>
                  <div id={gridId()} class="metric-option-group-grid">
                    <For each={group().metrics}>
                      {(metric) => {
                        const dependency = () => dependencyFor(metric);
                        const unavailable = () => metricUnavailable(metric);
                        return (
                          <button
                            id={activeDescendantOptionId(listboxId, metric.id)}
                            type="button"
                            role="option"
                            tabIndex={-1}
                            class="metric-option"
                            classList={{
                              active: isActiveMetric(metric.id),
                              selected: isSelected(metric.id) || sourceChoiceIds().has(metric.id),
                              "requires-dependency": (dependency()?.missing ?? false) && !unavailable(),
                              unavailable: unavailable()
                            }}
                            data-metric-id={metric.id}
                            data-full-content={optionTitle(metric, dependency(), unavailable())}
                            aria-selected={isSelected(metric.id)}
                            disabled={unavailable()}
                            onPointerMove={() => {
                              if (!unavailable()) {
                                setActiveMetricId(metric.id);
                              }
                            }}
                            onClick={() => chooseMetric(metric)}
                          >
                            <span class="metric-option-label">
                              {localizedMetricLabel(metric.id, metric.label)}
                            </span>
                            {/* Show a disabled reason or unit; keep component warnings in the heading and source marks before units. */}
                            <span class="metric-option-aside">
                              <Show when={dependency()?.missing && !unavailable()}>
                                <span
                                  class="metric-option-dependency"
                                  role="img"
                                  aria-label={uiText.metricPicker.needsInstall(dependency()!.name)}
                                  data-name-tooltip=""
                                >
                                  <PackagePlus size={13} aria-hidden="true" />
                                </span>
                              </Show>
                              <ReadingSourceMark mark={metric.readingSource?.mark} />
                              <Show when={unavailable()} fallback={metricUnitLabel(metric.unit)}>
                                <span class="metric-unavailable">
                                  {userFacingMetricUnavailableReason(metric.requiredComponentName, metric.disabledReason)}
                                </span>
                              </Show>
                            </span>
                          </button>
                        );
                      }}
                    </For>
                  </div>
                  </Show>
                </section>
              );
            }}
          </For>
        </div>
        {/* Keep the overlapping empty state outside the listbox. */}
        <Show when={groupedMetrics().keys.length === 0}>
          <div class="metric-picker-empty">{uiText.metricPicker.empty}</div>
        </Show>
        <Show when={sourceChoices().length > 0}>
          <div class="metric-picker-source">
            <div class="metric-picker-source-heading">
              <strong>{uiText.metricPicker.sourceLabel}</strong>
              <HelpHint label={uiText.metricPicker.sourceLabel}>{uiText.metricPicker.sourceHint}</HelpHint>
            </div>
            <RadioGroupRoot class="metric-source-choices" orientation="vertical" ariaLabel={uiText.metricPicker.sourceLabel}
              value={sourceValue()} onChange={(value) => {
                const choice = sourceChoices()[Number(value.slice("source-".length))];
                if (choice) props.onSelect(choice.metricId);
              }}>
              <Index each={sourceChoices()}>
                {(choice, index) => (
                  <RadioGroupItem value={`source-${index}`} class="metric-source-choice">
                    <span class="metric-source-choice-label" data-full-content={metricSourceChoiceLabel(choice())}>{metricSourceChoiceLabel(choice())}</span>
                    <Show when={choice().preferred}>
                      <small class="metric-source-choice-mark">{uiText.metricPicker.sourcePreferred}</small>
                    </Show>
                  </RadioGroupItem>
                )}
              </Index>
            </RadioGroupRoot>
          </div>
        </Show>
      </DialogBody>
      <DialogActions>
        <button class="secondary" type="button" onClick={props.onClose}>{uiText.metricPicker.cancel}</button>
        <button type="button" disabled={!props.multiSelect && (!props.selectedMetricId || selectedMetricUnavailable())} onClick={props.onConfirm}>{uiText.metricPicker.confirm}</button>
      </DialogActions>
    </DialogRoot>
  );
}
