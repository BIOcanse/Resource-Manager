import { localizedMetricLabel } from "../presentation/metricLabels";
import { Search, X } from "lucide-solid";
import { createEffect, createMemo, createSignal, For, Show } from "solid-js";
import {
  componentDisplayName,
  userFacingMetricGroup,
  userFacingMetricUnavailableReason,
  userFacingState
} from "../presentation/userFacingText";
import type { ManagedComponent, MetricDefinition } from "../types";
import {
  DialogActions,
  DialogBody,
  DialogHeader,
  DialogRoot
} from "../ui/primitives/Dialog.tsx";
import {
  activeDescendantOptionId,
  resolveActiveDescendantTarget
} from "../ui/primitives/activeDescendantListbox.ts";
import { isComponentInstalled, textOrEmpty } from "../utils";
import { uiText } from "../text.ts";

interface MetricModalProps {
  open: boolean;
  catalog: MetricDefinition[];
  components: ManagedComponent[];
  selectedMetricId: string | null;
  onSelect: (metricId: string) => void;
  onClose: () => void;
  onConfirm: () => void;
  onDependencyPrompt: (metric: MetricDefinition, dependency: MetricDependencyState) => void;
}

export interface MetricDependencyState {
  componentId: string;
  name: string;
  stateLabel: string;
  message: string;
  missing: boolean;
}

export function MetricModal(props: MetricModalProps) {
  const listboxId = "metric-modal-options";
  let searchInput: HTMLInputElement | undefined;
  let listbox: HTMLDivElement | undefined;
  const [query, setQuery] = createSignal("");
  const [activeMetricId, setActiveMetricId] = createSignal<string | null>(null);
  const selectedMetric = () => props.catalog.find((metric) => metric.id === props.selectedMetricId) ?? null;
  const metricUnavailable = (metric: MetricDefinition) =>
    metric.selectable === false && !(dependencyFor(metric)?.missing ?? false);
  const selectedMetricUnavailable = () => {
    const metric = selectedMetric();
    return metric ? metricUnavailable(metric) : false;
  };
  const filteredMetrics = createMemo(() => {
    const filter = query().trim().toLocaleLowerCase();
    return props.catalog.filter((metric) => {
      const group = userFacingMetricGroup(metric.group);
      return !filter
        || localizedMetricLabel(metric.id, metric.label).toLocaleLowerCase().includes(filter)
        || metric.id.toLocaleLowerCase().includes(filter)
        || group.toLocaleLowerCase().includes(filter);
    });
  });
  const enabledMetricIds = createMemo(() => filteredMetrics()
    .filter((metric) => !metricUnavailable(metric))
    .map((metric) => metric.id));
  const groupedMetrics = createMemo(() => {
    const groups = new Map<string, MetricDefinition[]>();
    for (const metric of filteredMetrics()) {
      const group = userFacingMetricGroup(metric.group);
      groups.set(group, [...(groups.get(group) ?? []), metric]);
    }
    return Array.from(groups, ([group, metrics]) => ({ group, metrics }));
  });

  function dependencyFor(metric: MetricDefinition): MetricDependencyState | null {
    const componentId = metric.requiredComponentId;
    if (!componentId) {
      return null;
    }

    const component = props.components.find((item) =>
      textOrEmpty(item.definition?.id).toLowerCase() === componentId.toLowerCase());
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
      stateLabel: userFacingState(component.stateLabel ?? component.state),
      message: "",
      missing: !isComponentInstalled(component)
    };
  }

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
      <DialogBody class="metric-picker">
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
              onInput={(event) => setQuery(event.currentTarget.value)}
              onKeyDown={handleSearchKeyDown}
            />
            <Show when={query()}>
              <button
                class="icon-button metric-picker-clear"
                type="button"
                aria-label={uiText.metricPicker.clearSearchLabel}
                title={uiText.resourceTableView.clearSearch}
                onClick={() => {
                  setQuery("");
                  searchInput?.focus();
                }}
              >
                <X size={15} aria-hidden="true" />
              </button>
            </Show>
          </label>
          <span class="metric-picker-count" role="status">{filteredMetrics().length} 个监控项</span>
        </div>
        <div
          ref={listbox}
          id={listboxId}
          class="metric-options"
          role="listbox"
          aria-label={uiText.metricPicker.listLabel}
        >
          <For each={groupedMetrics()} fallback={<div class="metric-picker-empty">{uiText.metricPicker.empty}</div>}>
            {(group, groupIndex) => {
              const headingId = `metric-modal-group-${groupIndex()}`;
              return (
                <section class="metric-option-group-block" role="group" aria-labelledby={headingId}>
                  <h3 id={headingId} class="metric-option-group-title">{group.group}</h3>
                  <div class="metric-option-group-grid">
                    <For each={group.metrics}>
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
                              active: activeMetricId() === metric.id,
                              selected: props.selectedMetricId === metric.id,
                              "requires-dependency": (dependency()?.missing ?? false) && !unavailable(),
                              unavailable: unavailable()
                            }}
                            data-metric-id={metric.id}
                            aria-selected={props.selectedMetricId === metric.id}
                            disabled={unavailable()}
                            onPointerMove={() => {
                              if (!unavailable()) {
                                setActiveMetricId(metric.id);
                              }
                            }}
                            onClick={() => chooseMetric(metric)}
                          >
                            <span class="metric-option-content">
                              <span>{localizedMetricLabel(metric.id, metric.label)}</span>
                              <Show when={dependency()}>
                                {(state) => (
                                  <small class={state().missing ? "metric-dependency missing" : "metric-dependency"}>
                                    {state().missing
                                      ? uiText.metricPicker.needsInstall(state().name)
                                      : `${state().name} · ${state().stateLabel}`}
                                  </small>
                                )}
                              </Show>
                              <Show when={unavailable()}>
                                <small class="metric-unavailable">
                                  {userFacingMetricUnavailableReason(metric.requiredComponentName, metric.disabledReason)}
                                </small>
                              </Show>
                            </span>
                          </button>
                        );
                      }}
                    </For>
                  </div>
                </section>
              );
            }}
          </For>
        </div>
      </DialogBody>
      <DialogActions>
        <button class="secondary" type="button" onClick={props.onClose}>{uiText.metricPicker.cancel}</button>
        <button type="button" disabled={!props.selectedMetricId || selectedMetricUnavailable()} onClick={props.onConfirm}>{uiText.metricPicker.confirm}</button>
      </DialogActions>
    </DialogRoot>
  );
}
