import { createEffect, createMemo, createSignal, For, Show } from "solid-js";
import { ArrowDown, ArrowUp } from "lucide-solid";
import { useTaskScope } from "../frontendRuntime/task/useTaskScope";
import { pointerReorderProps } from "../interactions/pointerReorder";
import type {
  AppAnimationMode,
  MetricDefinition,
  ResourceBarSettings,
  ResourceBreakdownBar,
  ResourceProcessSegment,
  ResourceSoftwareSegment
} from "../types";
import {
  resourceSegmentLayerStyle,
  type ResourceSegmentLayout,
  resourceSegmentLayout,
  shouldAnimateResourceSegment
} from "../resourceBreakdown/resourceSegmentLayout";
import {
  normalizeResourceBarScaleMode,
  resourceBarSupportsCapacity
} from "../resourceBreakdown/resourceBarScaleCapabilities";
import type {
  ResourceSegmentCssVars as CssVars
} from "../resourceBreakdown/resourceSegmentLayout";
import { ResourceSegmentPaintPlane, type ResourcePaintSegment } from "../resourceBreakdown/ResourceSegmentPaintPlane";
import { createResourceTrackGeometry } from "../resourceBreakdown/useResourceTrackGeometry";
import {
  createResourceLayoutSettleController
} from "../resourceBreakdown/resourceLayoutSettleTransition";
import { uiText } from "../text";
import { formatBytes, formatPercent } from "../utils";
import { StandardSelect } from "./StandardSelect";
import { ContentState } from "../ui/patterns/ContentState.tsx";
import {
  activeDescendantOptionId,
  resolveActiveDescendantTarget
} from "../ui/primitives/activeDescendantListbox.ts";
import { positionResourceTooltip } from "../resourceBreakdown/resourceTooltipPlacement.ts";
import {
  resourceSegmentAtPercent,
  resourceTrackPercentAtClientX
} from "../resourceBreakdown/resourceTrackInteraction.ts";
import { useInlineEditorFocus } from "../interactions/inlineEditorFocus";

export interface ResourcePrecisionSelection {
  metricId: string;
  softwareId: string;
  index: number;
}

interface ResourceBreakdownProps {
  animationMode: Exclude<AppAnimationMode, "auto">;
  catalog: MetricDefinition[];
  editMode: boolean;
  editingAvailable: boolean;
  saveState: "idle" | "saving" | "error";
  bars: ResourceBarSettings[];
  snapshotBars: ResourceBreakdownBar[];
  selection: ResourcePrecisionSelection | null;
  expanded: Record<string, string>;
  onToggleEdit: () => void;
  onCancelEdit: () => void;
  onToggleBar: (metricId: string, enabled: boolean) => void;
  onScaleModeChange: (metricId: string, scaleMode: "capacity" | "active") => void;
  onSelect: (selection: ResourcePrecisionSelection, hideCursor: boolean) => void;
  onReorderBar: (sourceMetricId: string, targetMetricId: string) => void;
}

type ResourceEmptySegment = Omit<ResourceSoftwareSegment, "kind" | "displayKind" | "processes"> & {
  kind: "Empty";
  displayKind: string;
  processes: [];
  className: "resource-segment-empty";
  isEmpty: true;
};

type ResourceSelectableSegment =
  | (ResourceSoftwareSegment & { className: string; isEmpty: false })
  | ResourceEmptySegment;

export function ResourceBreakdown(props: ResourceBreakdownProps) {
  let editButton: HTMLButtonElement | undefined;
  let editorRegion: HTMLDivElement | undefined;
  const [dragBarId, setDragBarId] = createSignal<string | null>(null);
  const [overBarId, setOverBarId] = createSignal<string | null>(null);
  const snapshotBarIds = createMemo(() =>
    props.snapshotBars.map((bar) => bar.metricId));

  const clearDragState = () => {
    setDragBarId(null);
    setOverBarId(null);
  };
  useInlineEditorFocus({
    active: () => props.editMode,
    opener: () => editButton,
    editor: () => editorRegion
  });

  return (
    <section
      class="resource-breakdown-panel"
      aria-label={uiText.resourceBreakdown.panel}
    >
      <div class="panel-header">
        <h2>{uiText.resourceBreakdown.panel}</h2>
        <div class="panel-header-actions">
          <Show when={props.saveState === "error"}>
            <span class="save-state error">{uiText.common.saveFailed}</span>
          </Show>
          <span id="resourceBreakdownStatus">
            {props.snapshotBars.length > 0 ? `${props.snapshotBars.length} 个项目` : uiText.resourceBreakdown.statusFallback}
          </span>
          <Show when={props.editMode}>
            <button
              class="secondary"
              type="button"
              aria-label="取消资源占用条目编辑"
              disabled={props.saveState === "saving"}
              onClick={props.onCancelEdit}
            >
              取消
            </button>
          </Show>
          <button
            ref={editButton}
            class="secondary panel-refresh-button"
            type="button"
            data-focus-key="resource-breakdown-edit"
            aria-label={props.editMode ? "保存资源占用条目" : "编辑资源占用条目"}
            disabled={!props.editingAvailable || props.saveState === "saving"}
            onClick={props.onToggleEdit}
          >
            {props.editMode
              ? props.saveState === "saving" ? uiText.common.saving : uiText.resourceBreakdown.save
              : uiText.resourceBreakdown.edit}
          </button>
        </div>
      </div>
      <Show when={props.editMode}>
        <ResourceBarEditor
          onElement={(element) => { editorRegion = element; }}
          catalog={props.catalog}
          bars={props.bars}
          onToggleBar={props.onToggleBar}
          onScaleModeChange={props.onScaleModeChange}
        />
      </Show>
      <Show
        when={props.snapshotBars.length > 0}
        fallback={
          <ContentState
            kind="empty"
            title={props.bars.length === 0
              ? "尚未配置资源占用条目"
              : "当前没有可显示的资源占用数据"}
            detail={props.bars.length === 0
              ? "进入编辑模式后可选择需要显示的资源指标。"
              : "当前没有进程或软件占用记录。"}
          />
        }
      >
        <div class="resource-breakdown">
          <For each={snapshotBarIds()}>
            {(metricId, index) => {
              const bar = createMemo(() =>
                props.snapshotBars.find((candidate) => candidate.metricId === metricId));
              return (
                <Show when={bar()}>
                  {(currentBar) => (
                    <ResourceBreakdownItem
                      animationMode={props.animationMode}
                      bar={currentBar()}
                      editMode={props.editMode}
                      dragging={dragBarId() === metricId}
                      dragOver={overBarId() === metricId && dragBarId() !== null && dragBarId() !== metricId}
                      selected={props.selection?.metricId === metricId ? props.selection : null}
                      expandedSoftwareId={props.expanded[metricId]}
                      onSelect={props.onSelect}
                      onBarDragStart={setDragBarId}
                      onBarDragOver={setOverBarId}
                      onBarDragEnd={clearDragState}
                      canMoveBefore={index() > 0}
                      canMoveAfter={index() < snapshotBarIds().length - 1}
                      onMoveBefore={() => {
                        const previous = props.snapshotBars[index() - 1];
                        if (previous) {
                          props.onReorderBar(metricId, previous.metricId);
                        }
                      }}
                      onMoveAfter={() => {
                        const next = props.snapshotBars[index() + 1];
                        if (next) {
                          props.onReorderBar(next.metricId, metricId);
                        }
                      }}
                      onBarDrop={(sourceMetricId, targetMetricId) => {
                        clearDragState();
                        props.onReorderBar(sourceMetricId, targetMetricId);
                      }}
                    />
                  )}
                </Show>
              );
            }}
          </For>
        </div>
      </Show>
    </section>
  );
}

function ResourceBarEditor(props: {
  onElement?: (element: HTMLDivElement) => void;
  catalog: MetricDefinition[];
  bars: ResourceBarSettings[];
  onToggleBar: (metricId: string, enabled: boolean) => void;
  onScaleModeChange: (metricId: string, scaleMode: "capacity" | "active") => void;
}) {
  const barByMetric = () => new Map(props.bars.map((bar) => [bar.metricId, bar]));
  return (
    <div
      ref={props.onElement}
      class="resource-bar-editor"
      role="group"
      aria-label="资源占用条目编辑器"
    >
      <For each={resourceBarOptions(props.catalog)}>
        {(metric) => {
          const bar = () => barByMetric().get(metric.id);
          const supportsCapacity = resourceBarSupportsCapacity(metric.id);
          return (
            <label class="resource-bar-option">
              <input
                type="checkbox"
                checked={Boolean(bar())}
                onChange={(event) => props.onToggleBar(metric.id, event.currentTarget.checked)}
              />
              <span class="resource-bar-option-copy">
                <span>{metric.label}</span>
              </span>
              <StandardSelect<"capacity" | "active">
                value={normalizeResourceBarScaleMode(metric.id, bar()?.scaleMode)}
                disabled={!bar() || !supportsCapacity}
                ariaLabel={`${metric.label} 缩放模式`}
                options={[
                  ...(supportsCapacity ? [{ value: "capacity" as const, label: uiText.resourceBreakdown.scaleCapacity }] : []),
                  { value: "active" as const, label: uiText.resourceBreakdown.scaleActive }
                ]}
                onChange={(value) => props.onScaleModeChange(metric.id, value)}
              />
            </label>
          );
        }}
      </For>
    </div>
  );
}

function ResourceBreakdownItem(props: {
  animationMode: Exclude<AppAnimationMode, "auto">;
  bar: ResourceBreakdownBar;
  editMode: boolean;
  dragging: boolean;
  dragOver: boolean;
  selected: ResourcePrecisionSelection | null;
  expandedSoftwareId?: string;
  onSelect: (selection: ResourcePrecisionSelection, hideCursor: boolean) => void;
  onBarDragStart: (metricId: string) => void;
  onBarDragOver: (metricId: string | null) => void;
  onBarDragEnd: () => void;
  canMoveBefore: boolean;
  canMoveAfter: boolean;
  onMoveBefore: () => void;
  onMoveAfter: () => void;
  onBarDrop: (sourceMetricId: string, targetMetricId: string) => void;
}) {
  const selectable = () => resourceSelectableSoftwareSegments(props.bar);
  const label = () => resourceBreakdownLabel(props.bar);
  const denominator = () => resourceBarDenominator(props.bar);
  const layout = createMemo(() => resourceSegmentLayout(selectable(), denominator(), resourceBarLayoutOptions(props.bar, selectable())));
  // Position segments by softwareId (stable DOM order) with absolute left/width so both
  // value changes AND reordering animate via CSS transitions — grid could only animate widths.
  const layoutIndex = createMemo(() => {
    const map = new Map<string, ReturnType<typeof layout>[number]>();
    for (const item of layout()) {
      map.set(item.segment.softwareId, item);
    }
    return map;
  });
  const [hoveredSoftwareId, setHoveredSoftwareId] = createSignal<string | null>(null);
  const expanded = () => (props.bar.software ?? []).find((segment) => segment.softwareId === props.expandedSoftwareId);
  const precisionActive = () => props.selected?.metricId === props.bar.metricId;
  const activeSoftwareId = () => {
    const activeId = props.selected?.softwareId ?? props.expandedSoftwareId;
    return activeId && selectable().some((segment) => segment.softwareId === activeId)
      ? activeId
      : null;
  };
  const interactionSoftwareId = () => hoveredSoftwareId() ?? activeSoftwareId();
  const interactionEntry = createMemo(() => {
    const softwareId = interactionSoftwareId();
    return softwareId ? layoutIndex().get(softwareId) ?? null : null;
  });
  const interactionOptionId = activeDescendantOptionId(props.bar.metricId, "active-segment");
  const activeOptionId = () => interactionEntry() ? interactionOptionId : undefined;
  const selectedSoftware = () => {
    const selectedId = props.selected?.softwareId;
    return selectedId
      ? selectable().find((segment) => segment.softwareId === selectedId) ?? null
      : null;
  };
  const trackGeometry = createResourceTrackGeometry();
  let interactionProxy: HTMLDivElement | undefined;

  createEffect(() => {
    const hoveredId = hoveredSoftwareId();
    if (!hoveredId) {
      return;
    }

    queueMicrotask(() => {
      if (hoveredSoftwareId() === hoveredId && interactionProxy) {
        positionResourceTooltip(interactionProxy);
      }
    });
  });

  // While segments slide/scale, flag the track as .moving so split boundaries fade
  // out during motion and back in once settled (CSS gates this to animated modes).
  const [moving, setMoving] = createSignal(false);
  const layoutTaskScope = useTaskScope(
    `monitor.resource-breakdown-item:${props.bar.metricId}`);
  const updateLayoutSettle = createResourceLayoutSettleController(
    layoutTaskScope,
    setMoving);
  createEffect(() => {
    updateLayoutSettle(layout(), props.animationMode);
  });

  const selectAtPoint = (event: MouseEvent) => {
    const track = event.currentTarget as HTMLElement;
    const entry = resourceEntryAtTrackPoint(layout(), track, event.clientX);
    if (entry) {
      props.onSelect({
        metricId: props.bar.metricId,
        softwareId: entry.segment.softwareId,
        index: entry.index
      }, false);
    }
  };

  const updateHoverAtPoint = (event: PointerEvent) => {
    const track = event.currentTarget as HTMLElement;
    const entry = resourceEntryAtTrackPoint(layout(), track, event.clientX);
    setHoveredSoftwareId(entry?.segment.softwareId ?? null);
  };

  const moveSelection = (step: number) => {
    const segments = selectable();
    if (segments.length === 0) {
      return;
    }

    const currentId = props.selected?.softwareId ?? props.expandedSoftwareId;
    const currentIndex = segments.findIndex((segment) => segment.softwareId === currentId);
    const fallbackIndex = props.selected?.index ?? (step > 0 ? -1 : segments.length);
    const resolved = clampNumber((currentIndex >= 0 ? currentIndex : fallbackIndex) + step, 0, segments.length - 1);
    props.onSelect({ metricId: props.bar.metricId, softwareId: segments[resolved].softwareId, index: resolved }, true);
  };

  const selectByKey = (key: string) => {
    const segments = selectable();
    const target = resolveActiveDescendantTarget(
      segments.map((segment) => segment.softwareId),
      activeSoftwareId(),
      key);
    if (!target) {
      return false;
    }
    props.onSelect({
      metricId: props.bar.metricId,
      softwareId: target.id,
      index: target.index
    }, true);
    return true;
  };

  return (
    <article
      class="resource-breakdown-item"
      data-resource-tooltip-boundary
      classList={{
        editing: props.editMode,
        "drag-source": props.dragging,
        "drag-over": props.dragOver
      }}
      {...pointerReorderProps(() => ({
        enabled: props.editMode,
        group: "resource-bar",
        sourceId: props.bar.metricId,
        onStart: props.onBarDragStart,
        onOver: props.onBarDragOver,
        onCommit: props.onBarDrop,
        onEnd: props.onBarDragEnd
      }))}
    >
      <div class="resource-breakdown-header">
        <strong>{label()}</strong>
        <span>
          {props.bar.totalDisplay} · {props.bar.scaleMode === "active" ? uiText.resourceBreakdown.scaleActive : uiText.resourceBreakdown.scaleCapacity}
        </span>
        <Show when={props.editMode}>
          <span class="resource-breakdown-order-actions">
            <button
              class="icon-button secondary"
              type="button"
              disabled={!props.canMoveBefore}
              aria-label={`上移 ${label()}`}
              title="上移"
              onClick={props.onMoveBefore}
            >
              <ArrowUp aria-hidden="true" size={15} />
            </button>
            <button
              class="icon-button secondary"
              type="button"
              disabled={!props.canMoveAfter}
              aria-label={`下移 ${label()}`}
              title="下移"
              onClick={props.onMoveAfter}
            >
              <ArrowDown aria-hidden="true" size={15} />
            </button>
          </span>
        </Show>
      </div>
      <div
        class="resource-bar-track"
        classList={{
          "precision-active": precisionActive(),
          moving: moving()
        }}
        ref={trackGeometry.observeTrack}
        tabIndex={0}
        role="listbox"
        aria-label={uiText.resourceBreakdown.selectionLabel(label())}
        aria-activedescendant={activeOptionId()}
        data-metric-id={props.bar.metricId}
        onClick={selectAtPoint}
        onPointerMove={updateHoverAtPoint}
        onPointerLeave={() => setHoveredSoftwareId(null)}
        onWheel={(event) => {
          if (!precisionActive()) {
            return;
          }

          const delta = Math.abs(event.deltaX) > Math.abs(event.deltaY) ? Math.sign(event.deltaX) : Math.sign(event.deltaY);
          if (delta !== 0) {
            event.preventDefault();
            moveSelection(delta > 0 ? 1 : -1);
          }
        }}
        onKeyDown={(event) => {
          if (selectByKey(event.key)) {
            event.preventDefault();
          }
        }}
      >
          <ResourceSegmentPaintPlane
            segments={resourceSoftwarePaintSegments(layout())}
            logicalSegmentCount={layout().filter((item) => !item.segment.isEmpty).length}
            segmentTop={trackGeometry.segmentTop()}
            segmentHeight={trackGeometry.segmentHeight()}
            contentWidth={trackGeometry.contentWidth()}
            devicePixelRatio={trackGeometry.devicePixelRatio()}
            drawTypeDividers
            moving={moving()}
          />
          <Show when={interactionEntry()}>
            {(item) => (
              <div
                class="resource-segment-layer resource-segment-interaction-layer"
                classList={{
                  "motion-segment": shouldAnimateResourceSegment(item().width)
                }}
                role="presentation"
                style={resourceSegmentLayerStyle(item(), resourceSegmentStyle(item().segment), {
                  contentWidth: trackGeometry.contentWidth(),
                  devicePixelRatio: trackGeometry.devicePixelRatio(),
                  segmentHeight: trackGeometry.segmentHeight(),
                  segmentTop: trackGeometry.segmentTop()
                })}
              >
                <div
                  ref={(element) => { interactionProxy = element; }}
                  id={interactionOptionId}
                  class={`${item().segment.className} resource-segment-interaction-proxy`}
                  classList={{
                    "precision-selected": props.selected?.softwareId === item().segment.softwareId,
                    "pointer-active": hoveredSoftwareId() === item().segment.softwareId
                  }}
                  tabIndex={-1}
                  role="option"
                  aria-label={resourceSegmentTooltip(props.bar, item().segment)}
                  aria-selected={props.selected?.softwareId === item().segment.softwareId ? "true" : "false"}
                  aria-posinset={item().index + 1}
                  aria-setsize={layout().length}
                  data-metric-id={props.bar.metricId}
                  data-software-id={item().segment.softwareId}
                >
                  <span
                    class="resource-tooltip"
                    classList={{ visible: hoveredSoftwareId() === item().segment.softwareId }}
                  >
                    {resourceSegmentTooltip(props.bar, item().segment)}
                  </span>
                </div>
              </div>
            )}
          </Show>
      </div>
      <Show when={selectedSoftware()}>
        {(software) => (
          <p class="resource-selection-detail" aria-hidden="true">
            {resourceSegmentTooltip(props.bar, software())}
          </p>
        )}
      </Show>
      <Show when={props.expandedSoftwareId} keyed>
        {(_expandedId) => (
          <Show when={expanded()}>
            {(software) => <ResourceProcessPanel bar={props.bar} software={software()} />}
          </Show>
        )}
      </Show>
    </article>
  );
}

function ResourceProcessPanel(props: {
  bar: ResourceBreakdownBar;
  software: ResourceSoftwareSegment;
}) {
  const layout = createMemo(() => resourceSegmentLayout(props.software.processes ?? [], props.software.value > 0 ? props.software.value : 1, { fill: true }));
  const layoutIndex = createMemo(() => {
    const map = new Map<string, ReturnType<typeof layout>[number]>();
    for (const item of layout()) {
      map.set(processKey(item.segment), item);
    }
    return map;
  });
  const keys = createMemo(() => layout().map((item) => processKey(item.segment)));
  const [activeProcessKey, setActiveProcessKey] = createSignal<string | null>(null);
  const [hoveredProcessKey, setHoveredProcessKey] = createSignal<string | null>(null);
  const interactionProcessKey = () => hoveredProcessKey() ?? activeProcessKey();
  const interactionEntry = createMemo(() => {
    const key = interactionProcessKey();
    return key ? layoutIndex().get(key) ?? null : null;
  });
  const activeProcess = createMemo(() => {
    const key = activeProcessKey();
    return key ? layoutIndex().get(key)?.segment ?? null : null;
  });
  const processTrackId = () => `${props.bar.metricId}:${props.software.softwareId}`;
  const interactionOptionId = () => activeDescendantOptionId(processTrackId(), "active-segment");
  const activeProcessOptionId = () => interactionEntry() ? interactionOptionId() : undefined;
  const trackGeometry = createResourceTrackGeometry();
  let interactionProxy: HTMLDivElement | undefined;
  createEffect(() => {
    const hoveredKey = hoveredProcessKey();
    if (!hoveredKey) {
      return;
    }

    queueMicrotask(() => {
      if (hoveredProcessKey() === hoveredKey && interactionProxy) {
        positionResourceTooltip(interactionProxy);
      }
    });
  });
  const hasProcesses = () => layout().length > 0;
  const hasResidualCategories = () => {
    if (props.software.softwareId?.startsWith("resource-residual:")) {
      return true;
    }

    const processes = props.software.processes ?? [];
    return processes.length > 0 && processes.every((process) => isSystemResidualProcess(process));
  };
  return (
    <div class="resource-process-panel" data-resource-tooltip-boundary>
      <div class="resource-process-header">
        <strong>{props.software.name}</strong>
        <span>{hasResidualCategories() ? uiText.resourceBreakdown.categoryCount(props.software.processes.length) : uiText.resourceBreakdown.processCount(props.software.processCount)}</span>
      </div>
      <div
        class="resource-process-track"
        classList={{ empty: !hasProcesses() }}
        ref={trackGeometry.observeTrack}
        tabIndex={hasProcesses() ? 0 : -1}
        role={hasProcesses() ? "listbox" : undefined}
        aria-label={hasProcesses() ? `${props.software.name} 进程占用` : undefined}
        aria-activedescendant={activeProcessOptionId()}
        onPointerMove={(event) => {
          const entry = resourceEntryAtTrackPoint(layout(), event.currentTarget, event.clientX);
          setHoveredProcessKey(entry ? processKey(entry.segment) : null);
        }}
        onPointerLeave={() => setHoveredProcessKey(null)}
        onClick={(event) => {
          const entry = resourceEntryAtTrackPoint(layout(), event.currentTarget, event.clientX);
          if (entry) {
            setActiveProcessKey(processKey(entry.segment));
          }
        }}
        onKeyDown={(event) => {
          const target = resolveActiveDescendantTarget(keys(), activeProcessKey(), event.key);
          if (target) {
            event.preventDefault();
            setActiveProcessKey(target.id);
          }
        }}
      >
        <Show
          when={hasProcesses()}
          fallback={<div class="resource-process-empty">{uiText.resourceBreakdown.noAttributableProcess}</div>}
        >
          <ResourceSegmentPaintPlane
            segments={resourceProcessPaintSegments(layout())}
            logicalSegmentCount={layout().length}
            segmentTop={trackGeometry.segmentTop()}
            segmentHeight={trackGeometry.segmentHeight()}
            contentWidth={trackGeometry.contentWidth()}
            devicePixelRatio={trackGeometry.devicePixelRatio()}
          />
          <Show when={interactionEntry()}>
            {(item) => (
              <div
                class="resource-segment-layer resource-segment-interaction-layer"
                classList={{
                  "motion-segment": shouldAnimateResourceSegment(item().width)
                }}
                role="presentation"
                style={resourceSegmentLayerStyle(item(), processSegmentStyle(hashKey(processKey(item().segment))), {
                  contentWidth: trackGeometry.contentWidth(),
                  devicePixelRatio: trackGeometry.devicePixelRatio(),
                  segmentHeight: trackGeometry.segmentHeight(),
                  segmentTop: trackGeometry.segmentTop()
                })}
              >
                <div
                  ref={(element) => { interactionProxy = element; }}
                  id={interactionOptionId()}
                  class="resource-process-segment resource-segment-interaction-proxy"
                  classList={{
                    "process-selected": activeProcessKey() === processKey(item().segment),
                    "pointer-active": hoveredProcessKey() === processKey(item().segment)
                  }}
                  role="option"
                  tabIndex={-1}
                  aria-selected={activeProcessKey() === processKey(item().segment) ? "true" : "false"}
                  aria-label={resourceProcessTooltip(item().segment)}
                  aria-posinset={item().index + 1}
                  aria-setsize={layout().length}
                >
                  <span
                    class="resource-tooltip"
                    classList={{ visible: hoveredProcessKey() === processKey(item().segment) }}
                  >
                    {resourceProcessTooltip(item().segment)}
                  </span>
                </div>
              </div>
            )}
          </Show>
        </Show>
      </div>
      <Show when={activeProcess()}>
        {(process) => (
          <p class="resource-process-selection-detail" aria-hidden="true">
            {resourceProcessTooltip(process())}
          </p>
        )}
      </Show>
    </div>
  );
}

function resourceProcessTooltip(process: ResourceProcessSegment) {
  const identity = isSystemResidualProcess(process) || Number(process.processId) <= 0
    ? uiText.resourceBreakdown.noPidCategory
    : `PID ${process.processId}`;
  const extra = isSystemResidualProcess(process)
    ? `\n${uiText.resourceBreakdown.providerNotSplit}`
    : isEtwResidualProcess(process)
      ? `\n${uiText.resourceBreakdown.etwSupplement}`
      : "";
  return `${process.name} (${identity})\n${uiText.resourceBreakdown.systemPercent} ${formatPercent(process.systemPercent)}\n${uiText.resourceBreakdown.softwareInnerPercent} ${formatPercent(process.softwarePercent)}\n${process.displayValue}${extra}`;
}

function isSystemResidualProcess(process: ResourceProcessSegment) {
  return process?.attributionKind === "system-residual";
}

function isEtwResidualProcess(process: ResourceProcessSegment) {
  return process?.attributionKind === "etw-residual-process";
}

function resourceBarOptions(catalog: MetricDefinition[]) {
  return [...catalog, ...resourceOnlyMetricOptions(catalog)]
    .filter((metric) => isResourceBarMetric(metric.id))
    .sort((left, right) => resourceBarSort(left.id) - resourceBarSort(right.id));
}

function resourceBreakdownLabel(bar: ResourceBreakdownBar) {
  const match = /^gpu\.(\d+)\.(usage|vram)$/i.exec(bar.metricId);
  if (!match) {
    return bar.label;
  }

  return match[2].toLowerCase() === "vram"
    ? uiText.resourceBreakdown.gpuVramLabel(match[1])
    : uiText.resourceBreakdown.gpuUsageLabel(match[1]);
}

function isResourceBarMetric(metricId: string) {
  return metricId === "cpu.usage"
    || metricId === "memory.usage"
    || metricId === "virtualMemory.usage"
    || metricId === "disk.io"
    || metricId === "disk.read"
    || metricId === "disk.write"
    || metricId === "network.traffic"
    || metricId === "network.receive"
    || metricId === "network.send"
    || metricId === "network.raw.traffic"
    || metricId === "network.raw.receive"
    || metricId === "network.raw.send"
    || /^gpu\.\d+\.(usage|vram)$/i.test(metricId);
}

function resourceBarSort(metricId: string) {
  if (metricId === "cpu.usage") return 0;
  if (metricId === "memory.usage") return 1;
  if (metricId === "virtualMemory.usage") return 2;
  if (metricId === "disk.io") return 30;
  if (metricId === "disk.read") return 31;
  if (metricId === "disk.write") return 32;
  if (metricId === "network.traffic") return 40;
  if (metricId === "network.receive") return 41;
  if (metricId === "network.send") return 42;
  if (metricId === "network.raw.traffic") return 43;
  if (metricId === "network.raw.receive") return 44;
  if (metricId === "network.raw.send") return 45;
  const match = /^gpu\.(\d+)\.(usage|vram)$/i.exec(metricId);
  if (!match) return 99;
  return 10 + Number(match[1]) * 2 + (match[2] === "vram" ? 1 : 0);
}

function resourceOnlyMetricOptions(catalog: MetricDefinition[]): MetricDefinition[] {
  const existing = new Set(catalog.map((metric) => metric.id));
  return [
    resourceMetric("virtualMemory.usage", "虚拟内存占用", "Memory", "B"),
    resourceMetric("disk.io", "磁盘 I/O", "Disk", "B/s"),
    resourceMetric("disk.read", "磁盘读取", "Disk", "B/s"),
    resourceMetric("disk.write", "磁盘写入", "Disk", "B/s"),
    resourceMetric("network.traffic", "外部网络流量", "Network", "B/s"),
    resourceMetric("network.receive", "外部网络接收", "Network", "B/s"),
    resourceMetric("network.send", "外部网络发送", "Network", "B/s"),
    resourceMetric("network.raw.traffic", "普通网络流量", "Network", "B/s"),
    resourceMetric("network.raw.receive", "普通网络接收", "Network", "B/s"),
    resourceMetric("network.raw.send", "普通网络发送", "Network", "B/s")
  ].filter((metric) => !existing.has(metric.id));
}

function resourceMetric(id: string, label: string, group: string, unit: string): MetricDefinition {
  return {
    id,
    label,
    group,
    unit,
    preferredSlot: "small",
    selectable: true
  };
}

function resourceSelectableSoftwareSegments(bar: ResourceBreakdownBar): ResourceSelectableSegment[] {
  const segments: ResourceSelectableSegment[] = (bar.software ?? [])
    .filter((segment) => segment?.softwareId && Number(segment.value) > 0)
    .map((segment) => ({
      ...segment,
      className: `resource-segment ${resourceSoftwareClass(segment.kind)}`,
      isEmpty: false as const
    }));

  const empty = resourceEmptyCapacitySegment(bar);
  if (empty) {
    segments.push(empty);
  }

  return segments;
}

function resourceEmptyCapacitySegment(bar: ResourceBreakdownBar): ResourceEmptySegment | null {
  const denominator = resourceBarDenominator(bar);
  const emptyValue = bar.scaleMode !== "active" && denominator > 0
    ? Math.max(0, Number(bar.capacityValue) - Number(bar.totalValue))
    : 0;
  if (!Number.isFinite(emptyValue) || emptyValue <= 0) {
    return null;
  }

  return {
    softwareId: `__resource-empty:${bar.metricId}`,
    name: uiText.resourceBreakdown.empty,
    kind: "Empty",
    displayKind: uiText.resourceBreakdown.empty,
    value: emptyValue,
    systemPercent: resourceSegmentPercent(emptyValue, denominator),
    displayValue: formatResourceBarValue(bar, emptyValue),
    processCount: 0,
    processes: [],
    className: "resource-segment-empty",
    isEmpty: true
  };
}

function resourceSegmentTooltip(bar: ResourceBreakdownBar, segment: ResourceSelectableSegment) {
  const percentLabel = segment.isEmpty ? uiText.resourceBreakdown.emptyPercent : uiText.resourceBreakdown.systemPercent;
  return `${segment.name}\n${percentLabel} ${formatPercent(segment.systemPercent)}\n${formatResourceBarValue(bar, segment.value)}`;
}

function formatResourceBarValue(bar: ResourceBreakdownBar, value: number) {
  if (bar.unit === "B") {
    return formatBytes(value);
  }
  if (bar.unit === "%") {
    return formatPercent(value);
  }

  const digits = Math.abs(value) >= 10 ? 1 : 2;
  return `${value.toFixed(digits)}${bar.unit ? ` ${bar.unit}` : ""}`;
}

function resourceBarDenominator(bar: ResourceBreakdownBar) {
  return bar.scaleMode === "active"
    ? Math.max(0, Number(bar.totalValue) || 0)
    : Math.max(0, Number(bar.capacityValue) || 0);
}

function resourceSegmentPercent(value: number, denominator: number) {
  return denominator > 0 ? value * 100 / denominator : 0;
}

function resourceBarLayoutOptions(bar: ResourceBreakdownBar, segments: ResourceSelectableSegment[]) {
  return {
    fill: bar.scaleMode === "active",
    emptyId: segments.find((segment) => segment.isEmpty)?.softwareId
  };
}

function resourceSoftwarePaintSegments(items: ResourceSegmentLayout<ResourceSelectableSegment>[]): ResourcePaintSegment[] {
  return items.map((item) => ({
    key: item.segment.softwareId,
    left: item.left,
    right: item.right,
    color: resourceSegmentPaintColor(item.segment, "type"),
    distinctColor: resourceSegmentPaintColor(item.segment, "distinct"),
    label: item.width >= 8 ? item.segment.name : undefined,
    labelColor: resourceSegmentPaintTextColor(item.segment, "type"),
    distinctLabelColor: resourceSegmentPaintTextColor(item.segment, "distinct")
  }));
}

function resourceProcessPaintSegments(items: ResourceSegmentLayout<ResourceProcessSegment>[]): ResourcePaintSegment[] {
  return items.map((item) => ({
    key: processKey(item.segment),
    left: item.left,
    right: item.right,
    color: distinctPalette[hashKey(processKey(item.segment)) % distinctPalette.length].bg,
    label: item.width >= 10 ? item.segment.name : undefined,
    labelColor: distinctPalette[hashKey(processKey(item.segment)) % distinctPalette.length].text
  }));
}

function resourceSegmentPaintColor(segment: ResourceSelectableSegment, mode: "type" | "distinct") {
  if (segment.isEmpty) {
    return "var(--resource-empty)";
  }

  if (mode === "distinct") {
    return distinctPalette[hashKey(segment.softwareId) % distinctPalette.length].bg;
  }

  return resourceTypeColor(segment.kind).bg;
}

function resourceSegmentPaintTextColor(segment: ResourceSelectableSegment, mode: "type" | "distinct") {
  if (segment.isEmpty) {
    return "var(--text)";
  }

  if (mode === "distinct") {
    return distinctPalette[hashKey(segment.softwareId) % distinctPalette.length].text;
  }

  return resourceTypeColor(segment.kind).text;
}

function resourceEntryAtTrackPoint<T extends { value: number }>(
  ranges: ResourceSegmentLayout<T>[],
  track: HTMLElement,
  clientX: number)
{
  const rect = track.getBoundingClientRect();
  const style = getComputedStyle(track);
  const left = rect.left + Number.parseFloat(style.borderLeftWidth || "0");
  const width = rect.width
    - Number.parseFloat(style.borderLeftWidth || "0")
    - Number.parseFloat(style.borderRightWidth || "0");
  return resourceSegmentAtPercent(
    ranges,
    resourceTrackPercentAtClientX(clientX, left, width));
}

function resourceSoftwareClass(kind: string) {
  if (kind === "WindowsSystem" || kind === "WindowsComponent" || kind === "WindowsService") {
    return "system";
  }

  return kind === "Adapted" ? "adapted" : "other";
}

// Wide-gamut (display-p3) segment palette. Each entry carries its own foreground
// so we never need a runtime contrast calculation. Shared by the distinct
// software mode and the process breakdown.
interface SegmentColor {
  bg: string;
  text: string;
}

const white = "color(display-p3 1 1 1)";
const ink = "color(display-p3 0.110 0.083 0.031)";

const distinctPalette: SegmentColor[] = [
  { bg: "color(display-p3 0.310 0.498 0.847)", text: white },
  { bg: "color(display-p3 0.184 0.620 0.435)", text: white },
  { bg: "color(display-p3 0.816 0.581 0.161)", text: ink },
  { bg: "color(display-p3 0.541 0.388 0.820)", text: white },
  { bg: "color(display-p3 0.784 0.365 0.478)", text: white },
  { bg: "color(display-p3 0.290 0.627 0.659)", text: white },
  { bg: "color(display-p3 0.902 0.494 0.246)", text: ink },
  { bg: "color(display-p3 0.447 0.553 0.267)", text: white }
];

// Fixed colour by software kind (system / adapted / other) for the type mode.
function resourceTypeColor(kind: string): SegmentColor {
  if (kind === "WindowsSystem" || kind === "WindowsComponent" || kind === "WindowsService") {
    return { bg: "color(display-p3 0.541 0.388 0.820)", text: white };
  }

  return kind === "Adapted"
    ? { bg: "color(display-p3 0.184 0.620 0.435)", text: white }
    : { bg: "color(display-p3 0.816 0.651 0.220)", text: ink };
}

function resourceSegmentStyle(segment: ResourceSelectableSegment): CssVars {
  if (segment.isEmpty) {
    return { "--segment-bg": "var(--resource-empty)", "--segment-text": "var(--text)" };
  }

  const type = resourceTypeColor(segment.kind);
  const distinct = distinctPalette[hashKey(segment.softwareId) % distinctPalette.length];
  return {
    "--segment-bg": type.bg,
    "--segment-text": type.text,
    "--segment-distinct": distinct.bg,
    "--segment-distinct-text": distinct.text
  };
}

function processSegmentStyle(index: number): CssVars {
  const color = distinctPalette[index % distinctPalette.length];
  return {
    "--process-color": color.bg,
    "--process-text": color.text
  };
}

function processKey(process: ResourceProcessSegment) {
  return `${process.processId}|${process.name}`;
}

// Stable per-process hash so a process keeps its color regardless of sort position.
function hashKey(key: string) {
  let hash = 0;
  for (let i = 0; i < key.length; i++) {
    hash = (hash * 31 + key.charCodeAt(i)) >>> 0;
  }
  return hash;
}

function clampNumber(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value));
}
