import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { HelpHint, OptionsHelpHint } from "../../ui/primitives/HelpHint.tsx";
import { localizedMetricLabel, resourceMetricBasis, resourceProcessDisplayName, softwareDisplayName } from "../../presentation/metricLabels.ts";
import { createEffect, createMemo, createSignal, For, Index, onCleanup, Show } from "solid-js";
import { ArrowDown, ArrowUp } from "lucide-solid";
import { useTaskScope } from "../../frontendRuntime/task/useTaskScope";
import { createReorderGroup } from "../../interactions/solid/reorderGroup.ts";
import { createWheelStepAccumulator, wheelNotches } from "../../interactions/wheelNotches.ts";
import type { ReorderGroup } from "../../interactions/solid/reorderGroup.ts";
import type {
  MetricDefinition,
  ResourceBarSettings,
  ResourceBreakdownBar,
  ResourceProcessSegment,
  ResourceProcessDetailsSnapshot,
  ResourceSoftwareSegment,
  PhysicalMemoryResidualFrame
} from "../../types";
import {
  resourceSegmentLayerStyle,
  type ResourceSegmentLayout,
  resourceSegmentLayout,
  shouldAnimateResourceSegment
} from "./resourceSegmentLayout";
import {
  normalizeResourceBarScaleMode,
  resourceBarSupportsCapacity
} from "./resourceBarScaleCapabilities";
import { ResourceSegmentPaintPlane, type ResourcePaintSegment } from "./ResourceSegmentPaintPlane";
import { createResourceTrackGeometry } from "./useResourceTrackGeometry";
import {
  createResourceLayoutSettleController
} from "./resourceLayoutSettleTransition";
import { softwareDisplayKindLabel, uiText } from "../../text.ts";
import { formatPercent } from "../../utils";
import { StandardSelect } from "../../components/StandardSelect";
import { ContentState } from "../../ui/patterns/ContentState.tsx";
import {
  activeDescendantOptionId,
  resolveActiveDescendantTarget
} from "../../ui/primitives/activeDescendantListbox.ts";
import { ResourceTooltipOverlay } from "./ResourceTooltipOverlay.tsx";
import { isReservedProcess, isReservedSegment, resourceProcessKey } from "./resourceProcessIdentity.ts";
import { resourceBarUnavailableText } from "./resourceBarUnavailableReason.ts";
import { readMotionDurationMs } from "../../presentation/motionTokens.ts";
import type { AnimationStyleId } from "../../presentation/animationStyle.ts";
import { resourceSegmentAtTrackX } from "./resourceTrackInteraction.ts";
import { resourceEmptyCapacityValue, selectableResourceSegmentIds } from "./resourceSelection.ts";
import { useInlineEditorFocus } from "../../interactions/inlineEditorFocus";
import { formatResourceBarValue } from "./resourceBreakdownValue";
import { PhysicalMemoryResidualDetails } from "./PhysicalMemoryResidualDetails.tsx";
import { ObservationStateNotice } from "../../components/ObservationStateNotice";
import type { ObservationState } from "../../observation/observationState";
import type { ProcessMetricField } from "../../data/resourceMonitor/processFieldCatalog.ts";
import { RadioGroupItem, RadioGroupRoot } from "../../ui/primitives/RadioGroup.tsx";
import { moveRovingFocus } from "../../ui/primitives/RovingFocus.ts";
import {
  resourceBarSource,
  resourceBarSourceChoices,
  resourceBarSourceLabel,
  resourceBarText,
  processSourceAdditivity,
  type ResourceBarView
} from "./resourceBarSources.ts";
import { exitOnUnmount } from "../../presentation/nodeAnimation.ts";
import { GpuNameNote } from "../monitor/GpuNameNote.tsx";

export interface ResourcePrecisionSelection {
  metricId: string;
  softwareId: string;
  /** Use displayKind and the owning metric for reserved details; software IDs are opaque. */
  displayKind: string | null;
  index: number;
}

interface ResourceBreakdownProps {
  animationStyle: AnimationStyleId;
  catalog: MetricDefinition[];
  /** Backend GPU names keyed by their unchanged indices. */
  gpuNames: ReadonlyMap<string, string>;
  editMode: boolean;
  editingAvailable: boolean;
  saveState: "idle" | "saving" | "error";
  bars: ResourceBarSettings[];
  /** Backend bars associated with configured items. */
  snapshotBars: ResourceBarView[];
  /** Backend process fields used for memory-source selection. */
  processFields: readonly ProcessMetricField[];
  observation: ObservationState;
  processDetails: ResourceProcessDetailsSnapshot | null;
  processDetailsObservation: ObservationState;
  /** Only one segment may be selected across all bars; its bar owns expanded details. */
  selection: ResourcePrecisionSelection | null;
  onToggleEdit: () => void;
  onCancelEdit: () => void;
  onToggleBar: (metricId: string, enabled: boolean) => void;
  onScaleModeChange: (metricId: string, scaleMode: "capacity" | "active") => void;
  onSourceChange: (metricId: string, source: string | null) => void;
  /** Select a segment through keyboard or wheel navigation. */
  onSelect: (selection: ResourcePrecisionSelection, hideCursor: boolean) => void;
  /** Activating the selected segment clears selection and details. */
  onToggle: (selection: ResourcePrecisionSelection, hideCursor: boolean) => void;
  onReorderBar: (sourceMetricId: string, targetMetricId: string) => void;
}

type ResourceEmptySegment = Omit<ResourceSoftwareSegment, "kind" | "displayKind" | "processes" | "baseScore"> & {
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
  const snapshotBarIds = createMemo(() =>
    props.snapshotBars.map((bar) => bar.metricId));
  // Drag bars to reorder while editing.
  const barReorder = createReorderGroup("资源条", (source, target) => props.onReorderBar(source, target));
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
          {/* Show empty-state text only when there is no data. */}
          <Show when={props.snapshotBars.length === 0}>
            <span>{props.observation.status === "loading" ? "--" : uiText.resourceBreakdown.statusFallback}</span>
          </Show>
          <Show when={props.editMode}>
            <button
              class="secondary"
              type="button"
              aria-label={uiText.resourceBreakdownView.cancelEditLabel}
              disabled={props.saveState === "saving"}
              onClick={props.onCancelEdit}
            >
              {uiText.resourceBreakdownView.cancel}
            </button>
          </Show>
          <button
            ref={editButton}
            class="secondary panel-refresh-button"
            type="button"
            data-focus-key="resource-breakdown-edit"
            aria-label={props.editMode ? uiText.resourceBreakdownView.saveLabel : uiText.resourceBreakdownView.editLabel}
            disabled={!props.editingAvailable || props.saveState === "saving"}
            onClick={props.onToggleEdit}
          >
            <StableLabel
              labels={{ edit: uiText.resourceBreakdown.edit, save: uiText.resourceBreakdown.save, saving: uiText.common.saving }}
              current={props.editMode ? props.saveState === "saving" ? "saving" : "save" : "edit"}
            />
          </button>
        </div>
      </div>
      <Show when={props.editMode}>
        <ResourceBarEditor
          onElement={(element) => { editorRegion = element; }}
          catalog={props.catalog}
          gpuNames={props.gpuNames}
          bars={props.bars}
          snapshotBars={props.snapshotBars}
          processFields={props.processFields}
          onToggleBar={props.onToggleBar}
          onScaleModeChange={props.onScaleModeChange}
          onSourceChange={props.onSourceChange}
        />
      </Show>
      <Show
        when={props.snapshotBars.length > 0}
        fallback={props.observation.status === "loading" && props.bars.length > 0
          ? <ResourceBreakdownPlaceholder bars={props.bars} gpuNames={props.gpuNames} processFields={props.processFields} />
          : <ContentState
            kind={props.observation.status === "loading" ? "waiting"
              : props.observation.status === "error" || props.observation.status === "profile-disabled" ? "unavailable" : "empty"}
            title={props.observation.status === "loading"
              ? uiText.observationNotice.loading(uiText.monitorPage.resourceUsage)
              : props.observation.status === "error" || props.observation.status === "profile-disabled"
                ? uiText.observationNotice.unavailable(uiText.monitorPage.resourceUsage)
                : props.bars.length === 0
              ? uiText.resourceBreakdownView.emptyTitle
              : uiText.resourceBreakdownView.noDataTitle}
            detail={props.observation.status === "loading" || props.observation.status === "error" || props.observation.status === "profile-disabled"
              ? undefined : props.bars.length === 0
              ? uiText.resourceBreakdownView.emptyDetail
              : uiText.resourceBreakdownView.noDataDetail}
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
                      animationStyle={props.animationStyle}
                      bar={currentBar()}
                      gpuNames={props.gpuNames}
                      processDetails={props.processDetails}
                      processDetailsObservation={props.processDetailsObservation}
                      editMode={props.editMode}
                      selected={props.selection?.metricId === metricId ? props.selection : null}
                      onSelect={props.onSelect}
                      onToggle={props.onToggle}
                      reorder={barReorder}
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

/** Reserve each configured bar's populated geometry before its first visible-demand result to prevent lower content shifting. */
function ResourceBreakdownPlaceholder(props: {
  bars: ResourceBarSettings[];
  gpuNames: ReadonlyMap<string, string>;
  processFields: readonly ProcessMetricField[];
}) {
  return (
    <div class="resource-breakdown" aria-busy="true">
      <p class="sr-only" role="status">{uiText.observationNotice.loading(uiText.monitorPage.resourceUsage)}</p>
      <For each={props.bars}>
        {(setting) => {
          const source = resourceBarSource(setting);
          const text = () => resourceBarText({ metricId: setting.metricId, source, processFieldName: null }, props.processFields, null);
          const active = () => normalizeResourceBarScaleMode(setting.metricId, setting.scaleMode) === "active";
          return (
            <article class="resource-breakdown-slot">
              <div class="resource-breakdown-header">
                <strong>{text().label}<GpuNameNote metricId={setting.metricId} names={props.gpuNames} /></strong>
                <span class="with-help-hint">
                  -- · {active() ? uiText.resourceBreakdown.scaleActive : uiText.resourceBreakdown.scaleCapacity}
                  <HelpHint>
                    <Show when={text().basis}>{(basis) => <p>{basis()}</p>}</Show>
                    <p>{active() ? uiText.resourceBreakdown.scaleActiveHint : uiText.resourceBreakdown.scaleCapacityHint}</p>
                  </HelpHint>
                </span>
              </div>
              <div class="resource-bar-track pending" aria-hidden="true" />
            </article>
          );
        }}
      </For>
    </div>
  );
}

function ResourceBarEditor(props: {
  onElement?: (element: HTMLDivElement) => void;
  catalog: MetricDefinition[];
  gpuNames: ReadonlyMap<string, string>;
  bars: ResourceBarSettings[];
  snapshotBars: readonly ResourceBarView[];
  processFields: readonly ProcessMetricField[];
  onToggleBar: (metricId: string, enabled: boolean) => void;
  onScaleModeChange: (metricId: string, scaleMode: "capacity" | "active") => void;
  onSourceChange: (metricId: string, source: string | null) => void;
}) {
  const barByMetric = () => new Map(props.bars.map((bar) => [bar.metricId, bar]));
  const options = createMemo(() => resourceBarOptions(props.catalog));
  // Key chips by metric ID to preserve focus across catalog refreshes.
  const optionIds = createMemo(() => options().map((metric) => metric.id));
  const optionById = createMemo(() => new Map(options().map((metric) => [metric.id, metric])));
  // The checkbox toggles visibility; other chip content selects one bar.
  // Show the selected bar's sources; its scale control acts independently.
  const [selectedMetricId, setSelectedMetricId] = createSignal<string | null>(null);
  // Offer sources only for a checked selected bar with multiple choices.
  const sourceMetricId = createMemo(() => {
    const id = selectedMetricId();
    return id !== null && optionById().has(id) && barByMetric().has(id) && resourceBarSourceChoices(id, props.processFields).length > 0 ? id : null;
  });
  const metricLabel = (metricId: string) => localizedMetricLabel(metricId, optionById().get(metricId)?.label ?? metricId);
  // Use the actual automatic field only while this bar is currently automatic.
  const automaticField = (metricId: string) => {
    const shown = props.snapshotBars.find((bar) => bar.metricId === metricId);
    return shown && shown.source === null ? shown.processFieldName ?? null : null;
  };
  return (
    <div
      ref={props.onElement}
      class="resource-bar-editor"
      role="group"
      aria-label={uiText.resourceBreakdownView.editorLabel}
      // Arrow keys and Home/End move focus without changing selection.
      onKeyDown={(event) => {
        if ((event.target as Element).classList.contains("resource-bar-select")) {
          moveRovingFocus(event, { selector: ".resource-bar-select", previousKeys: ["ArrowLeft"], nextKeys: ["ArrowRight"] });
        }
      }}
    >
      <For each={optionIds()}>
        {(metricId) => {
          const bar = () => barByMetric().get(metricId);
          const supportsCapacity = resourceBarSupportsCapacity(metricId);
          const label = () => metricLabel(metricId);
          const selected = () => selectedMetricId() === metricId;
          return (
            // Treat chip padding as selection, excluding checkbox and scale controls.
            <div
              class="resource-bar-option"
              classList={{ selected: selected() }}
              onClick={(event) => {
                const target = event.target as Element;
                if (!event.currentTarget.contains(target) || target.closest("input, .standard-select, .help-hint")) return;
                setSelectedMetricId(selected() ? null : metricId);
              }}
            >
              <input
                type="checkbox"
                aria-label={uiText.resourceBreakdownView.showBar(label())}
                checked={Boolean(bar())}
                onChange={(event) => props.onToggleBar(metricId, event.currentTarget.checked)}
              />
              <button type="button" class="resource-bar-select" aria-pressed={selected()}>
                {label()}
                <GpuNameNote metricId={metricId} names={props.gpuNames} />
              </button>
              <Show when={resourceMetricBasis(metricId)}>
                {(basis) => <HelpHint>{basis()}</HelpHint>}
              </Show>
              <StandardSelect<"capacity" | "active">
                value={normalizeResourceBarScaleMode(metricId, bar()?.scaleMode)}
                disabled={!bar() || !supportsCapacity}
                ariaLabel={uiText.resourceBreakdownView.scaleModeLabel(label())}
                options={[
                  ...(supportsCapacity ? [{ value: "capacity" as const, label: uiText.resourceBreakdown.scaleCapacity }] : []),
                  { value: "active" as const, label: uiText.resourceBreakdown.scaleActive }
                ]}
                onChange={(value) => props.onScaleModeChange(metricId, value)}
              />
            </div>
          );
        }}
      </For>
      <Show when={sourceMetricId()} keyed>
        {(metricId) => {
          const choices = () => resourceBarSourceChoices(metricId, props.processFields);
          const label = () => uiText.resourceTable.columnSourceLabel(metricLabel(metricId));
          const current = () => barByMetric().get(metricId)?.source ?? null;
          return (
            <div class="resource-bar-source">
              <strong>{label()}<GpuNameNote metricId={metricId} names={props.gpuNames} /></strong>
              <RadioGroupRoot class="segmented resource-bar-source-choices" ariaLabel={label()}
                // Leave choices unchecked when a saved source disappears from the catalog.
                value={(() => { const index = choices().indexOf(current()); return index < 0 ? "" : `source-${index}`; })()}
                onChange={(value) => {
                  const index = Number(value.slice("source-".length));
                  if (index >= 0 && index < choices().length) props.onSourceChange(metricId, choices()[index]);
                }}>
                <Index each={choices()}>
                  {(source, index) => (
                    <RadioGroupItem value={`source-${index}`} class="segment">
                      {resourceBarSourceLabel(source(), props.processFields, automaticField(metricId))}
                    </RadioGroupItem>
                  )}
                </Index>
              </RadioGroupRoot>
              <OptionsHelpHint entries={choices().flatMap((source) => {
                const basis = resourceBarText({ metricId, source, processFieldName: null }, props.processFields,
                  processSourceAdditivity(source, props.processFields)).basis;
                return basis ? [{ label: resourceBarSourceLabel(source, props.processFields, automaticField(metricId)), text: basis }] : [];
              })} />
            </div>
          );
        }}
      </Show>
    </div>
  );
}

function ResourceBreakdownItem(props: {
  animationStyle: AnimationStyleId;
  bar: ResourceBarView;
  gpuNames: ReadonlyMap<string, string>;
  processDetails: ResourceProcessDetailsSnapshot | null;
  processDetailsObservation: ObservationState;
  editMode: boolean;
  selected: ResourcePrecisionSelection | null;
  onSelect: (selection: ResourcePrecisionSelection, hideCursor: boolean) => void;
  onToggle: (selection: ResourcePrecisionSelection, hideCursor: boolean) => void;
  reorder: ReorderGroup;
  canMoveBefore: boolean;
  canMoveAfter: boolean;
  onMoveBefore: () => void;
  onMoveAfter: () => void;
}) {
  // Compute segment layout once per update for rendering, navigation, and selection.
  const selectable = createMemo(() => resourceSelectableSoftwareSegments(props.bar));
  const label = () => props.bar.label;
  const denominator = () => resourceBarDenominator(props.bar);
  const layout = createMemo(() => resourceSegmentLayout(selectable(), denominator(), resourceBarLayoutOptions(props.bar)));
  // Keep softwareId-based DOM order and absolute segment geometry
  // so value and order changes can both transition.
  const layoutIndex = createMemo(() => {
    const map = new Map<string, ReturnType<typeof layout>[number]>();
    for (const item of layout()) {
      map.set(item.segment.softwareId, item);
    }
    return map;
  });
  const [hoveredSoftwareId, setHoveredSoftwareId] = createSignal<string | null>(null);
  // Only the selected segment expands; free space has no software details.
  const expanded = () => (props.bar.software ?? []).find((segment) => segment.softwareId === props.selected?.softwareId);
  const precisionActive = () => props.selected?.metricId === props.bar.metricId;
  const activeSoftwareId = () => {
    const activeId = props.selected?.softwareId;
    return activeId && selectable().some((segment) => segment.softwareId === activeId)
      ? activeId
      : null;
  };
  // Retain keyboard position after deselection without creating another selection owner.
  let keyboardSoftwareId: string | null = null;
  const emit = (selection: ResourcePrecisionSelection, mode: "select" | "toggle", hideCursor: boolean) => {
    keyboardSoftwareId = selection.softwareId;
    (mode === "toggle" ? props.onToggle : props.onSelect)(selection, hideCursor);
  };
  const interactionSoftwareId = () => hoveredSoftwareId() ?? activeSoftwareId();
  const interactionEntry = createMemo(() => {
    const softwareId = interactionSoftwareId();
    return softwareId ? layoutIndex().get(softwareId) ?? null : null;
  });
  const interactionOptionId = activeDescendantOptionId(props.bar.metricId, "active-segment");
  const interactionFollowsData = createSegmentChangeJump(() => interactionEntry()?.segment.softwareId ?? null);
  const activeOptionId = () => interactionEntry() ? interactionOptionId : undefined;
  const selectedSoftware = () => {
    const selectedId = props.selected?.softwareId;
    return selectedId
      ? selectable().find((segment) => segment.softwareId === selectedId) ?? null
      : null;
  };
  const trackGeometry = createResourceTrackGeometry();
  let interactionProxy: HTMLDivElement | undefined;

  // Anchor tooltips to the hovered segment's interaction layer.
  const hoveredEntry = () => hoveredSoftwareId() ? interactionEntry() : null;

  // Mark active segment transitions for task diagnostics.
  // Draw separators on every transition frame without restarting the transition.
  const [moving, setMoving] = createSignal(false);
  const layoutTaskScope = useTaskScope(
    `monitor.resource-breakdown-item:${props.bar.metricId}`);
  const updateLayoutSettle = createResourceLayoutSettleController(
    layoutTaskScope,
    setMoving);
  createEffect(() => {
    // Read body motion tokens so style and resize behavior match the UI.
    // Skip animation for initial, unchanged, and reduced-motion layouts without forcing unused style reads.
    updateLayoutSettle(layout(), props.animationStyle, () => readMotionDurationMs(document.body, "--motion-bar"));
  });

  const selectAtPoint = (event: MouseEvent) => {
    const track = event.currentTarget as HTMLElement;
    const entry = resourceEntryAtTrackPoint(layout(), track, event.clientX);
    if (entry) {
      emit({
        metricId: props.bar.metricId,
        softwareId: entry.segment.softwareId,
        displayKind: entry.segment.displayKind ?? null,
        index: entry.index
      }, "toggle", false);
    }
  };

  const updateHoverAtPoint = (event: PointerEvent) => {
    const track = event.currentTarget as HTMLElement;
    const entry = resourceEntryAtTrackPoint(layout(), track, event.clientX);
    setHoveredSoftwareId(entry?.segment.softwareId ?? null);
  };

  const wheelSteps = createWheelStepAccumulator();

  const moveSelection = (step: number) => {
    const segments = selectable();
    if (segments.length === 0) {
      return;
    }

    const currentId = props.selected?.softwareId;
    const currentIndex = segments.findIndex((segment) => segment.softwareId === currentId);
    const fallbackIndex = props.selected?.index ?? (step > 0 ? -1 : segments.length);
    const resolved = clampNumber((currentIndex >= 0 ? currentIndex : fallbackIndex) + step, 0, segments.length - 1);
    emit({ metricId: props.bar.metricId, softwareId: segments[resolved].softwareId, displayKind: segments[resolved].displayKind ?? null, index: resolved }, "select", true);
  };

  // Arrow keys and Home/End select relative to the selected segment.
  // Enter and Space toggle the selected or last-focused segment.
  const selectByKey = (key: string) => {
    const segments = selectable();
    const ids = segments.map((segment) => segment.softwareId);
    const toggle = key === "Enter" || key === " ";
    const current = activeSoftwareId()
      ?? (toggle && keyboardSoftwareId !== null && ids.includes(keyboardSoftwareId) ? keyboardSoftwareId : null);
    const target = resolveActiveDescendantTarget(ids, current, key);
    if (!target) {
      return false;
    }
    emit({
      metricId: props.bar.metricId,
      softwareId: target.id,
      displayKind: segments[target.index].displayKind ?? null,
      index: target.index
    }, toggle ? "toggle" : "select", true);
    return true;
  };

  return (
    <article
      class="resource-breakdown-item"
      classList={{ editing: props.editMode }}
      ref={props.reorder.item({ id: () => props.bar.metricId, enabled: () => props.editMode })}
    >
      <div class="resource-breakdown-header">
        <strong>{label()}<GpuNameNote metricId={props.bar.metricId} names={props.gpuNames} /></strong>
        <span class="with-help-hint">
          {resourceBarValueText(props.bar)} · {props.bar.scaleMode === "active" ? uiText.resourceBreakdown.scaleActive : uiText.resourceBreakdown.scaleCapacity}
          {/* Explain the bar measure and scale beside its reading. */}
          <HelpHint>
            <Show when={props.bar.basis}>{(basis) => <p>{basis()}</p>}</Show>
            <p>{props.bar.scaleMode === "active" ? uiText.resourceBreakdown.scaleActiveHint : uiText.resourceBreakdown.scaleCapacityHint}</p>
          </HelpHint>
        </span>
        <Show when={props.editMode}>
          <span class="resource-breakdown-order-actions">
            <button
              class="icon-button secondary"
              type="button"
              disabled={!props.canMoveBefore}
              aria-label={uiText.resourceBreakdownView.moveUp(label())}
              data-name-tooltip=""
              onClick={props.onMoveBefore}
            >
              <ArrowUp aria-hidden="true" size={15} />
            </button>
            <button
              class="icon-button secondary"
              type="button"
              disabled={!props.canMoveAfter}
              aria-label={uiText.resourceBreakdownView.moveDown(label())}
              data-name-tooltip=""
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
        // Expose measure semantics once in the bar label and accessible description.
        aria-description={props.bar.basis || undefined}
        aria-activedescendant={activeOptionId()}
        data-metric-id={props.bar.metricId}
        onClick={selectAtPoint}
        onPointerMove={updateHoverAtPoint}
        onPointerLeave={() => setHoveredSoftwareId(null)}
        onWheel={(event) => {
          if (!precisionActive()) {
            return;
          }

          // Accumulate the dominant wheel axis before advancing a segment.
          const delta = Math.abs(event.deltaX) > Math.abs(event.deltaY) ? event.deltaX : event.deltaY;
          if (delta !== 0) {
            event.preventDefault();
            const steps = wheelSteps(wheelNotches(delta, event.deltaMode));
            if (steps !== 0) {
              moveSelection(steps);
            }
          }
        }}
        onKeyDown={(event) => {
          if (selectByKey(event.key)) {
            event.preventDefault();
          }
        }}
      >
          <ResourceSegmentPaintPlane
            segments={resourceSoftwarePaintSegments(layout(), props.bar.metricId)}
            logicalSegmentCount={layout().filter((item) => !item.segment.isEmpty).length}
            segmentTop={trackGeometry.segmentTop()}
            segmentHeight={trackGeometry.segmentHeight()}
            contentWidth={trackGeometry.contentWidth()}
            devicePixelRatio={trackGeometry.devicePixelRatio()}
            drawDividers
          />
          <Show when={interactionEntry()}>
            {(item) => (
              <div
                class="resource-segment-layer resource-segment-interaction-layer"
                classList={{
                  "motion-segment": interactionFollowsData() && shouldAnimateResourceSegment(item().width)
                }}
                role="presentation"
                style={resourceSegmentLayerStyle(item(), {
                  contentWidth: trackGeometry.contentWidth(),
                  devicePixelRatio: trackGeometry.devicePixelRatio(),
                  segmentHeight: trackGeometry.segmentHeight(),
                  segmentTop: trackGeometry.segmentTop()
                })}
              >
                <div
                  ref={(element) => { interactionProxy = element; }}
                  id={interactionOptionId}
                  // Write all classes together so dynamic group classes retain selection styling.
                  class={resourceProxyClass(item().segment.className, {
                    "precision-selected": props.selected?.softwareId === item().segment.softwareId,
                    "pointer-active": hoveredSoftwareId() === item().segment.softwareId
                  })}
                  tabIndex={-1}
                  role="option"
                  aria-label={resourceSegmentTooltip(props.bar, item().segment)}
                  aria-selected={props.selected?.softwareId === item().segment.softwareId ? "true" : "false"}
                  aria-posinset={item().index + 1}
                  aria-setsize={layout().length}
                  data-metric-id={props.bar.metricId}
                  data-software-id={item().segment.softwareId}
                />
              </div>
            )}
          </Show>
          <Show when={hoveredEntry()}>
            {(item) => (
              <ResourceTooltipOverlay anchor={() => interactionProxy}>
                <ResourceTooltipContent parts={resourceSegmentTooltipParts(props.bar, item().segment)} />
              </ResourceTooltipOverlay>
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
      <Show when={props.selected?.softwareId} keyed>
        {(_expandedId) => (
          <Show when={expanded()}>
            {(software) => <ResourceProcessPanel bar={props.bar} software={software()}
              // Explicit-source detail subscriptions use the field name as metricId.
              processes={props.processDetails === null ? null : props.processDetails.metrics
                .find(metric => metric.metricId === (props.bar.source ?? props.bar.metricId))?.software
                .find(item => item.softwareId === software().softwareId)?.processes ?? []}
              observation={props.processDetailsObservation}
              physicalMemoryResidual={props.processDetails?.physicalMemoryResidual} />}
          </Show>
        )}
      </Show>
    </article>
  );
}

function ResourceProcessPanel(props: {
  bar: ResourceBarView;
  software: ResourceSoftwareSegment;
  processes: ResourceProcessSegment[] | null;
  observation: ObservationState;
  physicalMemoryResidual: PhysicalMemoryResidualFrame | undefined;
}) {
  const layout = createMemo(() => resourceSegmentLayout(props.processes ?? [], props.software.value > 0 ? props.software.value : 1, { fill: true }));
  const layoutIndex = createMemo(() => {
    const map = new Map<string, ReturnType<typeof layout>[number]>();
    for (const item of layout()) {
      map.set(resourceProcessKey(item.segment), item);
    }
    return map;
  });
  const keys = createMemo(() => layout().map((item) => resourceProcessKey(item.segment)));
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
  const interactionFollowsData = createSegmentChangeJump(() => {
    const entry = interactionEntry();
    return entry ? resourceProcessKey(entry.segment) : null;
  });
  const activeProcessOptionId = () => interactionEntry() ? interactionOptionId() : undefined;
  const trackGeometry = createResourceTrackGeometry();
  let interactionProxy: HTMLDivElement | undefined;
  const hoveredEntry = () => hoveredProcessKey() ? interactionEntry() : null;
  const hasProcesses = () => layout().length > 0;
  const hasResidualCategories = () => {
    if (isReservedSegment(props.software)) {
      return true;
    }

    const processes = props.processes ?? [];
    return processes.length > 0 && processes.every((process) => isSystemResidualProcess(process));
  };
  return (
    <div ref={(element) => exitOnUnmount(element)} class="resource-process-panel" aria-busy={props.observation.status === "loading"}>
      <div class="resource-process-header">
        <strong>{softwareDisplayName(props.software, props.bar.metricId)}</strong>
        <span>{hasResidualCategories() ? uiText.resourceBreakdown.categoryCount(props.processes?.length ?? 0) : uiText.resourceBreakdown.processCount(props.software.processCount)}</span>
      </div>
      <div
        class="resource-process-track"
        classList={{ empty: !hasProcesses(), pending: props.processes === null && props.observation.status === "loading" }}
        ref={trackGeometry.observeTrack}
        tabIndex={hasProcesses() ? 0 : -1}
        role={hasProcesses() ? "listbox" : undefined}
        aria-label={hasProcesses() ? uiText.resourceBreakdownView.processUsage(softwareDisplayName(props.software, props.bar.metricId)) : undefined}
        aria-description={hasProcesses() && props.bar.basis ? props.bar.basis : undefined}
        aria-activedescendant={activeProcessOptionId()}
        onPointerMove={(event) => {
          const entry = resourceEntryAtTrackPoint(layout(), event.currentTarget, event.clientX);
          setHoveredProcessKey(entry ? resourceProcessKey(entry.segment) : null);
        }}
        onPointerLeave={() => setHoveredProcessKey(null)}
        onClick={(event) => {
          const entry = resourceEntryAtTrackPoint(layout(), event.currentTarget, event.clientX);
          if (entry) {
            // Activating the selected detail item clears selection.
            const key = resourceProcessKey(entry.segment);
            setActiveProcessKey((current) => current === key ? null : key);
          }
        }}
        onKeyDown={(event) => {
          const target = resolveActiveDescendantTarget(keys(), activeProcessKey(), event.key);
          if (target) {
            event.preventDefault();
            const toggle = event.key === "Enter" || event.key === " ";
            setActiveProcessKey((current) => toggle && current === target.id ? null : target.id);
          }
        }}
      >
        <Show
          when={hasProcesses()}
          // Keep a fixed-height empty track while loading and announce progress accessibly.
          fallback={<Show when={props.processes !== null} fallback={<ObservationStateNotice presentation="announce-loading"
            state={props.observation} label={uiText.resourceBreakdownView.processUsage(softwareDisplayName(props.software, props.bar.metricId))} />}>
            <div class="resource-process-empty">{uiText.resourceBreakdown.noAttributableProcess}</div>
          </Show>}
        >
          <ResourceSegmentPaintPlane
            segments={resourceProcessPaintSegments(layout(), props.bar.metricId, props.software)}
            logicalSegmentCount={layout().length}
            segmentTop={trackGeometry.segmentTop()}
            segmentHeight={trackGeometry.segmentHeight()}
            contentWidth={trackGeometry.contentWidth()}
            devicePixelRatio={trackGeometry.devicePixelRatio()}
            drawDividers
          />
          <Show when={interactionEntry()}>
            {(item) => (
              <div
                class="resource-segment-layer resource-segment-interaction-layer"
                classList={{
                  "motion-segment": interactionFollowsData() && shouldAnimateResourceSegment(item().width)
                }}
                role="presentation"
                style={resourceSegmentLayerStyle(item(), {
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
                    "process-selected": activeProcessKey() === resourceProcessKey(item().segment),
                    "pointer-active": hoveredProcessKey() === resourceProcessKey(item().segment)
                  }}
                  role="option"
                  tabIndex={-1}
                  aria-selected={activeProcessKey() === resourceProcessKey(item().segment) ? "true" : "false"}
                  aria-label={resourceProcessTooltip(props.bar, props.software, item().segment)}
                  aria-posinset={item().index + 1}
                  aria-setsize={layout().length}
                />
              </div>
            )}
          </Show>
          <Show when={hoveredEntry()}>
            {(item) => (
              <ResourceTooltipOverlay anchor={() => interactionProxy}>
                <ResourceTooltipContent parts={resourceProcessTooltipParts(props.bar, props.software, item().segment)} />
              </ResourceTooltipOverlay>
            )}
          </Show>
        </Show>
      </div>
      <Show when={activeProcess()}>
        {(process) => (
          <p class="resource-process-selection-detail" aria-hidden="true">
            {resourceProcessTooltip(props.bar, props.software, process())}
          </p>
        )}
      </Show>
      {/* Select residualDetails for physical-memory page-frame detail subscriptions. */}
      <Show when={isReservedSegment(props.software) && props.bar.residualSource === "counters"}>
        <PhysicalMemoryResidualDetails frame={props.physicalMemoryResidual} />
      </Show>
    </div>
  );
}

/** Share segment-specific tooltip, accessible-label, and selected-description content; keep bar-wide semantics on the bar. */
interface ResourceTooltipParts {
  title: string;
  /** Match the label swatch to the segment color. */
  swatch: ResourceTooltipSwatch;
  /** Only software segments have a display group for type coloring. */
  kind: string | null;
  lines: string[];
  notes: string[];
}

interface ResourceTooltipSwatch {
  color: string;
  distinctColor: string;
  hatched: boolean;
}

function ResourceTooltipContent(props: { parts: ResourceTooltipParts }) {
  return (
    <>
      <span class="resource-tooltip-title">
        <span
          class="resource-tooltip-swatch"
          classList={{ hatched: props.parts.swatch.hatched }}
          aria-hidden="true"
          style={{ "--swatch-color": props.parts.swatch.color, "--swatch-distinct-color": props.parts.swatch.distinctColor }}
        />
        <span>{props.parts.title}</span>
      </span>
      <Show when={props.parts.kind}>
        {(kind) => <span class="resource-tooltip-kind">{kind()}</span>}
      </Show>
      <For each={props.parts.lines}>{(line) => <span class="resource-tooltip-line">{line}</span>}</For>
      <Show when={props.parts.notes.length > 0}>
        <span class="resource-tooltip-basis">
          <For each={props.parts.notes}>{(note) => <span>{note}</span>}</For>
        </span>
      </Show>
    </>
  );
}

function resourceTooltipText(parts: ResourceTooltipParts) {
  return [parts.title, ...(parts.kind ? [parts.kind] : []), ...parts.lines, ...parts.notes].join("\n");
}

function resourceProcessTooltip(bar: ResourceBarView, software: ResourceSoftwareSegment, process: ResourceProcessSegment) {
  return resourceTooltipText(resourceProcessTooltipParts(bar, software, process));
}

function resourceProcessTooltipParts(bar: ResourceBarView, software: ResourceSoftwareSegment, process: ResourceProcessSegment): ResourceTooltipParts {
  const identity = isSystemResidualProcess(process) || Number(process.processId) <= 0
    ? uiText.resourceBreakdown.noPidCategory
    : `PID ${process.processId}`;
  const extra = isSystemResidualProcess(process)
    ? uiText.resourceBreakdown.providerNotSplit
    : isEtwResidualProcess(process)
      ? uiText.resourceBreakdown.etwSupplement
      : null;
  const color = distinctPalette[hashKey(resourceProcessKey(process)) % distinctPalette.length];
  return {
    title: `${resourceProcessDisplayName(process, bar.metricId)} (${identity})`,
    swatch: { color, distinctColor: color, hatched: isReservedProcess(software, process) },
    kind: null,
    lines: [
      `${resourceSharePercentLabel(bar)} ${formatPercent(process.systemPercent)}`,
      `${uiText.resourceBreakdown.softwareInnerPercent} ${formatPercent(process.softwarePercent)}`,
      resourceValueLine(bar, process)
    ],
    notes: extra ? [extra] : []
  };
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
    resourceMetric("virtualMemory.usage", uiText.resourceBreakdownView.metric.virtualMemoryUsage, "Memory", "B"),
    resourceMetric("disk.io", uiText.resourceBreakdownView.metric.diskIo, "Disk", "B/s"),
    resourceMetric("disk.read", uiText.resourceBreakdownView.metric.diskRead, "Disk", "B/s"),
    resourceMetric("disk.write", uiText.resourceBreakdownView.metric.diskWrite, "Disk", "B/s"),
    resourceMetric("network.traffic", uiText.resourceBreakdownView.metric.networkTraffic, "Network", "B/s"),
    resourceMetric("network.receive", uiText.resourceBreakdownView.metric.networkReceive, "Network", "B/s"),
    resourceMetric("network.send", uiText.resourceBreakdownView.metric.networkSend, "Network", "B/s"),
    resourceMetric("network.raw.traffic", uiText.resourceBreakdownView.metric.rawNetworkTraffic, "Network", "B/s"),
    resourceMetric("network.raw.receive", uiText.resourceBreakdownView.metric.rawNetworkReceive, "Network", "B/s"),
    resourceMetric("network.raw.send", uiText.resourceBreakdownView.metric.rawNetworkSend, "Network", "B/s")
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

// Use the shared selectable-segment calculation and add presentation fields here.
function resourceSelectableSoftwareSegments(bar: ResourceBreakdownBar): ResourceSelectableSegment[] {
  const software = new Map((bar.software ?? []).map((segment) => [segment.softwareId, segment]));
  const denominator = resourceBarDenominator(bar);
  return selectableResourceSegmentIds(bar).map((softwareId): ResourceSelectableSegment => {
    const segment = software.get(softwareId);
    if (segment) {
      return { ...segment, className: `resource-segment ${resourceSoftwareBucket(segment.displayKind)}`, isEmpty: false };
    }
    const emptyValue = resourceEmptyCapacityValue(bar);
    return {
      softwareId,
      name: uiText.resourceBreakdown.empty,
      kind: "Empty",
      displayKind: uiText.resourceBreakdown.empty,
      value: emptyValue,
      systemPercent: resourceSegmentPercent(emptyValue, denominator),
      processCount: 0,
      processes: [],
      className: "resource-segment-empty",
      isEmpty: true
    };
  });
}

// Use the frontend free-space label and shared software naming rules.
function segmentLabel(segment: ResourceSelectableSegment, metricId: string) {
  return segment.kind === "Empty" ? segment.name : softwareDisplayName(segment, metricId);
}

function resourceSegmentTooltip(bar: ResourceBarView, segment: ResourceSelectableSegment) {
  return resourceTooltipText(resourceSegmentTooltipParts(bar, segment));
}

function resourceSegmentTooltipParts(bar: ResourceBarView, segment: ResourceSelectableSegment): ResourceTooltipParts {
  const percentLabel = segment.isEmpty ? uiText.resourceBreakdown.emptyPercent : resourceSharePercentLabel(bar);
  return {
    title: segmentLabel(segment, bar.metricId),
    swatch: {
      color: resourceSegmentPaintColor(segment, "type"),
      distinctColor: resourceSegmentPaintColor(segment, "distinct"),
      hatched: isReservedSegment(segment)
    },
    kind: segment.isEmpty || isReservedSegment(segment) ? null : softwareDisplayKindLabel(segment.kind, segment.displayKind),
    lines: [`${percentLabel} ${formatPercent(segment.systemPercent)}`, resourceValueLine(bar, segment), ...residualLeafLines(bar, segment)],
    notes: []
  };
}

// Keep backend order for reserved counter leaves; these leaves are not processes.
function residualLeafLines(bar: ResourceBarView, segment: ResourceSelectableSegment) {
  if (segment.isEmpty || bar.residualSource !== "counters" || !isReservedSegment(segment)) return [];
  return (segment.processes ?? []).map((leaf) => `${resourceProcessDisplayName(leaf, bar.metricId)} ${formatResourceBarValue(bar, leaf.value)}`);
}

/** Capacity shares compare against the system; throughput shares compare against this frame's total. */
function resourceSharePercentLabel(bar: Pick<ResourceBarView, "metricId">) {
  return resourceBarSupportsCapacity(bar.metricId) ? uiText.resourceBreakdown.systemPercent : uiText.resourceBreakdown.currentTotalPercent;
}

function resourceValueLine(bar: ResourceBarView, segment: { value: number; sharedValue?: number | null }) {
  return bar.unit === "B" && segment.sharedValue != null
    ? uiText.resourceTableView.memoryBreakdown(bar.label,
      formatResourceBarValue(bar, segment.value),
      formatResourceBarValue(bar, segment.value - segment.sharedValue),
      formatResourceBarValue(bar, segment.sharedValue))
    : `${bar.label}: ${formatResourceBarValue(bar, segment.value)}`;
}

// Replace missing totals with their reason; append attribution failures when a total remains available.
// Localize known reason classes and preserve unknown codes.
function resourceBarValueText(bar: ResourceBarView) {
  const reason = bar.unavailableReason ? resourceBarUnavailableText(bar.unavailableReason) : null;
  if (reason === null) return resourceBarTotalText(bar);
  return bar.totalValue === null ? reason : `${resourceBarTotalText(bar)} · ${reason}`;
}

// Capacity mode appends capacity to the total; omit redundant 100% capacity.
function resourceBarTotalText(bar: ResourceBarView) {
  const total = formatResourceBarValue(bar, bar.totalValue);
  const capacity = Number(bar.capacityValue);
  return bar.scaleMode !== "active" && bar.unit !== "%" && Number.isFinite(capacity) && capacity > 0
    ? `${total} / ${formatResourceBarValue(bar, capacity)}`
    : total;
}

function resourceBarDenominator(bar: ResourceBreakdownBar) {
  return bar.scaleMode === "active"
    ? Math.max(0, Number(bar.totalValue) || 0)
    : Math.max(0, Number(bar.capacityValue) || 0);
}

function resourceSegmentPercent(value: number, denominator: number) {
  return denominator > 0 ? value * 100 / denominator : 0;
}

function resourceBarLayoutOptions(bar: ResourceBreakdownBar) {
  return {
    fill: bar.scaleMode === "active"
  };
}

function resourceSoftwarePaintSegments(items: ResourceSegmentLayout<ResourceSelectableSegment>[], metricId: string): ResourcePaintSegment[] {
  return items.map((item) => ({
    key: item.segment.softwareId,
    sharedFraction: !item.segment.isEmpty && item.segment.value > 0 ? (item.segment.sharedValue ?? 0) / item.segment.value : 0,
    left: item.left,
    right: item.right,
    color: resourceSegmentPaintColor(item.segment, "type"),
    distinctColor: resourceSegmentPaintColor(item.segment, "distinct"),
    hatched: isReservedSegment(item.segment),
    // The canvas decides whether labels fit by pixel width.
    label: segmentLabel(item.segment, metricId),
    labelColor: item.segment.isEmpty ? "var(--text)" : segmentTextColor
  }));
}

function resourceProcessPaintSegments(
  items: ResourceSegmentLayout<ResourceProcessSegment>[],
  metricId: string,
  software: ResourceSoftwareSegment
): ResourcePaintSegment[] {
  return items.map((item) => ({
    key: resourceProcessKey(item.segment),
    sharedFraction: item.segment.value > 0 ? (item.segment.sharedValue ?? 0) / item.segment.value : 0,
    left: item.left,
    right: item.right,
    color: distinctPalette[hashKey(resourceProcessKey(item.segment)) % distinctPalette.length],
    hatched: isReservedProcess(software, item.segment),
    label: resourceProcessDisplayName(item.segment, metricId),
    labelColor: segmentTextColor
  }));
}

function resourceSegmentPaintColor(segment: ResourceSelectableSegment, mode: "type" | "distinct") {
  if (segment.isEmpty) {
    return "var(--resource-empty)";
  }
  if (isReservedSegment(segment)) {
    return reservedColor;
  }
  return mode === "distinct"
    ? distinctPalette[hashKey(segment.softwareId) % distinctPalette.length]
    : bucketColors[resourceSoftwareBucket(segment.displayKind)];
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
  return resourceSegmentAtTrackX(ranges, clientX - left, width);
}

/* Use backend displayKind for grouping; this layer owns only group colors. */
const systemDisplayKinds = new Set(["windows_system", "windows_component", "windows_service"]);

type ResourceSoftwareBucket = "system" | "adapted" | "other";

function resourceSoftwareBucket(displayKind: string): ResourceSoftwareBucket {
  if (systemDisplayKinds.has(displayKind)) {
    return "system";
  }

  return displayKind === "adapted" ? "adapted" : "other";
}

// Resolve segment colors through theme CSS variables.
// Choose stable software colors by hashing softwareId.
const distinctPalette = Array.from({ length: 8 }, (_, index) => `var(--resource-segment-${index + 1})`);
const segmentTextColor = "var(--resource-segment-text)";
const reservedColor = "var(--resource-reserved)";

// Reuse the shared display grouping for type colors.
const bucketColors: Record<ResourceSoftwareBucket, string> = {
  system: "var(--resource-kind-system)",
  adapted: "var(--resource-kind-adapted)",
  other: "var(--resource-kind-other)"
};



function resourceProxyClass(base: string, states: Record<string, boolean>) {
  return [base, "resource-segment-interaction-proxy", ...Object.keys(states).filter((name) => states[name])].join(" ");
}

// Keep process colors stable across sort changes.
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

/** Jump interaction geometry when segment identity changes; transition only data updates for the same segment. */
function createSegmentChangeJump(id: () => string | null) {
  const [settledId, setSettledId] = createSignal<string | null>(null);
  createEffect(() => {
    const current = id();
    const frame = requestAnimationFrame(() => setSettledId(current));
    onCleanup(() => cancelAnimationFrame(frame));
  });
  return () => id() !== null && id() === settledId();
}
