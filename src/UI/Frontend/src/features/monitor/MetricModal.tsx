import { localizedMetricLabel, metricUnitLabel } from "../../presentation/metricLabels";
import { PackagePlus, Search, X } from "lucide-solid";
import { createEffect, createMemo, createSelector, createSignal, For, onCleanup, Show } from "solid-js";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext.tsx";
import { useFrontendReadIntervals } from "../../app/FrontendReadIntervalsContext.tsx";
import type { MetricFieldCatalogDevice } from "../../data/metricFields/metricFieldSource.ts";
import {
  componentDisplayName,
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
import { deviceFieldScopeKind } from "./fieldCards.ts";
import { ReadingSourceMark } from "../../components/ReadingSourceMark.tsx";

interface MetricModalProps {
  open: boolean;
  catalog: MetricDefinition[];
  components: ManagedComponent[];
  selectedMetricId: string | null;
  selectedMetricIds?: readonly string[];
  multiSelect?: boolean;
  onSelect: (metricId: string) => void;
  onClose: () => void;
  onConfirm: () => void;
  onDependencyPrompt: (metric: MetricDefinition, dependency: MetricDependencyState) => void;
}

interface MetricOptionGroup {
  group: string;
  metrics: MetricDefinition[];
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

/** 显卡组用指标携带的设备身份查设备事实，不解析身份或按序号猜设备。 */
function gpuGroupName(metrics: readonly MetricDefinition[], devices: readonly MetricFieldCatalogDevice[]): string | null {
  if (!metrics.every((metric) => /^gpu\.\d+$/i.test(metric.group.trim()))) {
    return null;
  }
  const identities = new Set(metrics.map((metric) => metric.deviceIdentity));
  const [identity] = identities;
  return identities.size === 1 && identity
    ? devices.find((device) => device.deviceIdentity === identity && device.kind === "gpu_adapter")?.displayName?.trim() || null
    : null;
}

/** 选项名和右侧原因都可能被截断：悬停看全名、不可选的原因；设备字段附字段说明，已装的组件附组件状态。 */
function optionTitle(metric: MetricDefinition, dependency: MetricDependencyState | null, unavailable: boolean): string {
  return [
    localizedMetricLabel(metric.id, metric.label),
    unavailable ? userFacingMetricUnavailableReason(metric.requiredComponentName, metric.disabledReason) : null,
    metric.scopeKind === deviceFieldScopeKind ? metric.detail : null,
    dependency && !dependency.missing ? `${dependency.name} · ${dependency.stateLabel}` : null
  ].filter(Boolean).join("\n");
}

export function MetricModal(props: MetricModalProps) {
  const runtime = useFrontendRuntime();
  const readIntervals = useFrontendReadIntervals();
  const [devices, setDevices] = createSignal<readonly MetricFieldCatalogDevice[]>([]);
  createEffect(() => {
    const intervalMs = readIntervals.current()?.monitor;
    if (!props.open || intervalMs === undefined) return;
    const unsubscribe = runtime.sources.metricFieldCatalog.subscribeSnapshot(intervalMs,
      (snapshot) => setDevices(snapshot.data ?? []));
    onCleanup(unsubscribe);
  });
  const listboxId = "metric-modal-options";
  let searchInput: HTMLInputElement | undefined;
  let listbox: HTMLDivElement | undefined;
  const [query, setQuery] = createSignal("");
  const [activeMetricId, setActiveMetricId] = createSignal<string | null>(null);
  // 一千多个选项：当前项、已选项用 selector，切换时只更新变化的两项，不逐项重算。
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
  // 组件按小写 id 建一次表，选项查依赖时不再各自遍历组件列表。
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
  // 列表只在弹窗打开时整理；关着时目录推送不触发任何整理。
  const filteredMetrics = createMemo(() => {
    if (!props.open) {
      return [];
    }
    const filter = query().trim().toLocaleLowerCase();
    if (!filter) {
      return props.catalog;
    }
    return props.catalog.filter((metric) =>
      localizedMetricLabel(metric.id, metric.label).toLocaleLowerCase().includes(filter)
      || metric.id.toLocaleLowerCase().includes(filter)
      || userFacingMetricGroup(metric.group).toLocaleLowerCase().includes(filter));
  });
  const enabledMetricIds = createMemo(() => filteredMetrics()
    .filter((metric) => !metricUnavailable(metric))
    .map((metric) => metric.id));
  // 分组按键给出；渲染按键保留每组的节点，组内按选项对象增删，搜索和目录刷新不重建整张列表。
  const groupedMetrics = createMemo(() => {
    // 设备字段按设备分组，不按显示名合并：单例设备（处理器、内存等）和原有同名资源组是两组。
    const groups = new Map<string, MetricDefinition[]>();
    for (const metric of filteredMetrics()) {
      const key = metric.scopeKind === deviceFieldScopeKind ? `device:${metric.scopeKey}` : userFacingMetricGroup(metric.group);
      const metrics = groups.get(key);
      if (metrics) {
        metrics.push(metric);
      } else {
        groups.set(key, [metric]);
      }
    }
    const byKey = new Map<string, MetricOptionGroup>();
    for (const [key, metrics] of groups) {
      byKey.set(key, {
        group: userFacingMetricGroup(metrics[0].group),
        metrics,
        detail: metrics[0].scopeKind === deviceFieldScopeKind ? uiText.metricPicker.deviceFields : gpuGroupName(metrics, devices()),
        // 这一组缺哪些组件，在组标题上说一次。
        missingComponents: [...new Set(metrics
          .filter((metric) => !metricUnavailable(metric))
          .map((metric) => dependencyFor(metric))
          .filter((dependency) => dependency?.missing)
          .map((dependency) => dependency!.name))]
      });
    }
    return { keys: [...groups.keys()], byKey };
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
          <For each={groupedMetrics().keys} fallback={<div class="metric-picker-empty">{uiText.metricPicker.empty}</div>}>
            {(key, groupIndex) => {
              // 这一组还在列表里时节点保留，内容跟着分组结果更新。
              const group = createMemo<MetricOptionGroup>(
                (previous) => groupedMetrics().byKey.get(key) ?? previous,
                groupedMetrics().byKey.get(key)!);
              const headingId = () => `metric-modal-group-${groupIndex()}`;
              return (
                <section class="metric-option-group-block" role="group" aria-labelledby={headingId()}>
                  <div class="metric-option-group-heading">
                    <h3 id={headingId()} class="metric-option-group-title" title={group().detail ?? undefined}>
                      {group().group}
                      <Show when={group().detail}>
                        {(detail) => <span class="metric-option-group-detail"> · {detail()}</span>}
                      </Show>
                    </h3>
                    <For each={group().missingComponents}>
                      {(name) => (
                        <small class="metric-option-group-note">
                          <PackagePlus size={13} aria-hidden="true" />
                          {uiText.metricPicker.needsInstall(name)}
                        </small>
                      )}
                    </For>
                  </div>
                  <div class="metric-option-group-grid">
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
                              selected: isSelected(metric.id),
                              "requires-dependency": (dependency()?.missing ?? false) && !unavailable(),
                              unavailable: unavailable()
                            }}
                            data-metric-id={metric.id}
                            title={optionTitle(metric, dependency(), unavailable())}
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
                            {/* 右侧小字：不可选时写原因，否则写单位；缺组件只留图标，提示在组标题。
                                设备字段的来源标记（首选／待确认）排在单位前面。 */}
                            <span class="metric-option-aside">
                              <Show when={dependency()?.missing && !unavailable()}>
                                <span
                                  class="metric-option-dependency"
                                  role="img"
                                  aria-label={uiText.metricPicker.needsInstall(dependency()!.name)}
                                  title={uiText.metricPicker.needsInstall(dependency()!.name)}
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
                </section>
              );
            }}
          </For>
        </div>
      </DialogBody>
      <DialogActions>
        <button class="secondary" type="button" onClick={props.onClose}>{uiText.metricPicker.cancel}</button>
        <button type="button" disabled={!props.multiSelect && (!props.selectedMetricId || selectedMetricUnavailable())} onClick={props.onConfirm}>{uiText.metricPicker.confirm}</button>
      </DialogActions>
    </DialogRoot>
  );
}
