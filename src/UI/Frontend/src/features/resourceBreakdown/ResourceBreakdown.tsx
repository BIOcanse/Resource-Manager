import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { localizedMetricLabel, resourceMetricBasis, resourceProcessDisplayName, softwareDisplayName } from "../../presentation/metricLabels.ts";
import { createEffect, createMemo, createSignal, For, Index, onCleanup, Show } from "solid-js";
import { ArrowDown, ArrowUp } from "lucide-solid";
import { useTaskScope } from "../../frontendRuntime/task/useTaskScope";
import { createReorderGroup } from "../../interactions/solid/reorderGroup.ts";
import { createWheelStepAccumulator, wheelNotches } from "../../interactions/wheelNotches.ts";
import type { ReorderGroup } from "../../interactions/solid/reorderGroup.ts";
import type {
  AppAnimationMode,
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
  resourceBarSourceChoices,
  resourceBarSourceLabel,
  resourceBarText,
  processSourceAdditivity,
  type ResourceBarView
} from "./resourceBarSources.ts";

export interface ResourcePrecisionSelection {
  metricId: string;
  softwareId: string;
  /** 选中这一段时它的 displayKind：系统/驱动保留按所属资源条的指标订阅分项，软件 id 不透明。 */
  displayKind: string | null;
  index: number;
}

interface ResourceBreakdownProps {
  animationMode: Exclude<AppAnimationMode, "auto">;
  catalog: MetricDefinition[];
  editMode: boolean;
  editingAvailable: boolean;
  saveState: "idle" | "saving" | "error";
  bars: ResourceBarSettings[];
  /** 按资源条设置接到项目上的后端资源条（resourceBarViews）。 */
  snapshotBars: ResourceBarView[];
  /** 后端进程字段目录：内存资源条的来源选择。 */
  processFields: readonly ProcessMetricField[];
  observation: ObservationState;
  processDetails: ResourceProcessDetailsSnapshot | null;
  processDetailsObservation: ObservationState;
  selection: ResourcePrecisionSelection | null;
  expanded: Record<string, string>;
  onToggleEdit: () => void;
  onCancelEdit: () => void;
  onToggleBar: (metricId: string, enabled: boolean) => void;
  onScaleModeChange: (metricId: string, scaleMode: "capacity" | "active") => void;
  onSourceChange: (metricId: string, source: string | null) => void;
  onSelect: (selection: ResourcePrecisionSelection, hideCursor: boolean) => void;
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
  // 编辑时按住资源条拖到另一条上换位。
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
          {/*
            只在**没数据**的时候说一句。有几条一数就知道，
            在标题旁边挂个"6 个项目"是重复屏幕上已有的信息。
          */}
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
        fallback={
          <ContentState
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
                      animationMode={props.animationMode}
                      bar={currentBar()}
                      processDetails={props.processDetails}
                      processDetailsObservation={props.processDetailsObservation}
                      editMode={props.editMode}
                      selected={props.selection?.metricId === metricId ? props.selection : null}
                      expandedSoftwareId={props.expanded[metricId]}
                      onSelect={props.onSelect}
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

function ResourceBarEditor(props: {
  onElement?: (element: HTMLDivElement) => void;
  catalog: MetricDefinition[];
  bars: ResourceBarSettings[];
  snapshotBars: readonly ResourceBarView[];
  processFields: readonly ProcessMetricField[];
  onToggleBar: (metricId: string, enabled: boolean) => void;
  onScaleModeChange: (metricId: string, scaleMode: "capacity" | "active") => void;
  onSourceChange: (metricId: string, source: string | null) => void;
}) {
  const barByMetric = () => new Map(props.bars.map((bar) => [bar.metricId, bar]));
  const options = createMemo(() => resourceBarOptions(props.catalog));
  // 按指标 id 排芯片：目录每次刷新都是新对象，按对象排会重建芯片，键盘焦点随之丢失。
  const optionIds = createMemo(() => options().map((metric) => metric.id));
  const optionById = createMemo(() => new Map(options().map((metric) => [metric.id, metric])));
  // 先选项目、再选来源：点芯片的方格子只切换这一条显示与否；点芯片其他位置是选中这一条（同时只有一个，进入编辑时没有），
  // 下面只摆选中这一条的“来源”，第一项是自动选择。再点一次选中的芯片取消选中。缩放下拉框是它自己的控件，不选中也不切换。
  const [selectedMetricId, setSelectedMetricId] = createSignal<string | null>(null);
  // 来源行属于哪一条：选中的这一条已勾选、而且有不止一个来源时就是它，否则不摆来源行（来源存在已勾选的那一条上）。
  const sourceMetricId = createMemo(() => {
    const id = selectedMetricId();
    return id !== null && optionById().has(id) && barByMetric().has(id) && resourceBarSourceChoices(id, props.processFields).length > 0 ? id : null;
  });
  const metricLabel = (metricId: string) => localizedMetricLabel(metricId, optionById().get(metricId)?.label ?? metricId);
  // 自动选择这一项写出后端这次实际读的字段：只有这一条现在就是自动选择时，显示中的那条的字段才是它。
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
      // 左右方向键在芯片之间移动焦点，只移动、不选中；Home/End 到头尾。
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
            // 芯片里除了方格子和缩放下拉框都是“选中”：按钮之外的内边距也算，按钮的点击（含键盘 Enter/空格）冒泡到这里处理。
            <div
              class="resource-bar-option"
              classList={{ selected: selected() }}
              onClick={(event) => {
                const target = event.target as Element;
                if (!event.currentTarget.contains(target) || target.closest("input, .standard-select")) return;
                setSelectedMetricId(selected() ? null : metricId);
              }}
            >
              <input
                type="checkbox"
                aria-label={uiText.resourceBreakdownView.showBar(label())}
                checked={Boolean(bar())}
                onChange={(event) => props.onToggleBar(metricId, event.currentTarget.checked)}
              />
              <button type="button" class="resource-bar-select" aria-pressed={selected()} title={resourceMetricBasis(metricId)}>
                {label()}
              </button>
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
              <strong>{label()}</strong>
              <RadioGroupRoot class="segmented resource-bar-source-choices" ariaLabel={label()}
                // 存的来源不在目录里（目录变了）时不勾任何一项，不把它显示成自动选择。
                value={(() => { const index = choices().indexOf(current()); return index < 0 ? "" : `source-${index}`; })()}
                onChange={(value) => {
                  const index = Number(value.slice("source-".length));
                  if (index >= 0 && index < choices().length) props.onSourceChange(metricId, choices()[index]);
                }}>
                <Index each={choices()}>
                  {(source, index) => (
                    <RadioGroupItem value={`source-${index}`} class="segment"
                      title={resourceBarText({ metricId, source: source(), processFieldName: null }, props.processFields,
                        processSourceAdditivity(source(), props.processFields)).basis}>
                      {resourceBarSourceLabel(source(), props.processFields, automaticField(metricId))}
                    </RadioGroupItem>
                  )}
                </Index>
              </RadioGroupRoot>
            </div>
          );
        }}
      </Show>
    </div>
  );
}

function ResourceBreakdownItem(props: {
  animationMode: Exclude<AppAnimationMode, "auto">;
  bar: ResourceBarView;
  processDetails: ResourceProcessDetailsSnapshot | null;
  processDetailsObservation: ObservationState;
  editMode: boolean;
  selected: ResourcePrecisionSelection | null;
  expandedSoftwareId?: string;
  onSelect: (selection: ResourcePrecisionSelection, hideCursor: boolean) => void;
  reorder: ReorderGroup;
  canMoveBefore: boolean;
  canMoveAfter: boolean;
  onMoveBefore: () => void;
  onMoveAfter: () => void;
}) {
  const selectable = () => resourceSelectableSoftwareSegments(props.bar);
  const label = () => props.bar.label;
  const denominator = () => resourceBarDenominator(props.bar);
  const layout = createMemo(() => resourceSegmentLayout(selectable(), denominator(), resourceBarLayoutOptions(props.bar)));
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

  // 提示只跟悬停走：悬停时 interactionEntry 就是悬停的那一段，交互层停在它上面，提示对着交互层摆。
  const hoveredEntry = () => hoveredSoftwareId() ? interactionEntry() : null;

  // 分段过渡期间轨道带 .moving，任务中心诊断里显示为“资源布局过渡”。
  // 画布不看它：分隔线在过渡的每一帧都画，它翻转时也不会打断正在走的过渡。
  const [moving, setMoving] = createSignal(false);
  const layoutTaskScope = useTaskScope(
    `monitor.resource-breakdown-item:${props.bar.metricId}`);
  const updateLayoutSettle = createResourceLayoutSettleController(
    layoutTaskScope,
    setMoving);
  createEffect(() => {
    // 时长读 body 上的令牌：手感在根元素上，窗口缩放时 body 把它置 0，画布读到的是同一个值。
    // 首次布局、不变的布局和减弱档不启动动画，不为未使用的时长强制刷新整页样式。
    updateLayoutSettle(layout(), props.animationMode, () => readMotionDurationMs(document.body, "--motion-bar"));
  });

  const selectAtPoint = (event: MouseEvent) => {
    const track = event.currentTarget as HTMLElement;
    const entry = resourceEntryAtTrackPoint(layout(), track, event.clientX);
    if (entry) {
      props.onSelect({
        metricId: props.bar.metricId,
        softwareId: entry.segment.softwareId,
        displayKind: entry.segment.displayKind ?? null,
        index: entry.index
      }, false);
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

    const currentId = props.selected?.softwareId ?? props.expandedSoftwareId;
    const currentIndex = segments.findIndex((segment) => segment.softwareId === currentId);
    const fallbackIndex = props.selected?.index ?? (step > 0 ? -1 : segments.length);
    const resolved = clampNumber((currentIndex >= 0 ? currentIndex : fallbackIndex) + step, 0, segments.length - 1);
    props.onSelect({ metricId: props.bar.metricId, softwareId: segments[resolved].softwareId, displayKind: segments[resolved].displayKind ?? null, index: resolved }, true);
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
      displayKind: segments[target.index].displayKind ?? null,
      index: target.index
    }, true);
    return true;
  };

  return (
    <article
      class="resource-breakdown-item"
      classList={{ editing: props.editMode }}
      ref={props.reorder.item({ id: () => props.bar.metricId, enabled: () => props.editMode })}
    >
      <div class="resource-breakdown-header">
        <strong title={props.bar.basis}>{label()}</strong>
        <span title={props.bar.scaleMode === "active" ? uiText.resourceBreakdown.scaleActiveHint : uiText.resourceBreakdown.scaleCapacityHint}>
          {resourceBarValueText(props.bar)} · {props.bar.scaleMode === "active" ? uiText.resourceBreakdown.scaleActive : uiText.resourceBreakdown.scaleCapacity}
        </span>
        <Show when={props.editMode}>
          <span class="resource-breakdown-order-actions">
            <button
              class="icon-button secondary"
              type="button"
              disabled={!props.canMoveBefore}
              aria-label={uiText.resourceBreakdownView.moveUp(label())}
              title={uiText.resourceBreakdownView.moveUpTitle}
              onClick={props.onMoveBefore}
            >
              <ArrowUp aria-hidden="true" size={15} />
            </button>
            <button
              class="icon-button secondary"
              type="button"
              disabled={!props.canMoveAfter}
              aria-label={uiText.resourceBreakdownView.moveDown(label())}
              title={uiText.resourceBreakdownView.moveDownTitle}
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
        // 整条的口径说一次：读屏在这里读到，条名上悬停也能看到；各段的提示和名称里不再重复。
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

          // 横竖取量大的那个方向；按格累积，触控板的零碎滚动攒够一格才换一个分段，不再每个事件都跳一格。
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
                  // 类名一次写全：展示分组的类会随段变，和 classList 混用时改写 class 会抹掉 classList 加的选中类。
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
      <Show when={props.expandedSoftwareId} keyed>
        {(_expandedId) => (
          <Show when={expanded()}>
            {(software) => <ResourceProcessPanel bar={props.bar} software={software()}
              // 选了来源的资源条按这个字段订阅明细，后端回的明细 metricId 是字段名。
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
    <div class="resource-process-panel" aria-busy={props.observation.status === "loading"}>
      <div class="resource-process-header">
        <strong>{softwareDisplayName(props.software, props.bar.metricId)}</strong>
        <span>{hasResidualCategories() ? uiText.resourceBreakdown.categoryCount(props.processes?.length ?? 0) : uiText.resourceBreakdown.processCount(props.software.processCount)}</span>
      </div>
      <div
        class="resource-process-track"
        classList={{ empty: !hasProcesses() }}
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
            setActiveProcessKey(resourceProcessKey(entry.segment));
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
          fallback={<Show when={props.processes !== null} fallback={<ObservationStateNotice
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
      {/* 物理内存类的条（residualSource 为 counters）展开保留量时，明细订阅用 residualDetails 选它，后端另给页帧明细帧。 */}
      <Show when={isReservedSegment(props.software) && props.bar.residualSource === "counters"}>
        <PhysicalMemoryResidualDetails frame={props.physicalMemoryResidual} />
      </Show>
    </div>
  );
}

/**
 * 提示的内容：名字、读数各一行，以及只属于这一段的归因说明（整条的口径不在这里，见资源条的条名和 aria-description）。
 * aria-label 和选中后的说明行用同样的内容连成一段文字。
 */
interface ResourceTooltipParts {
  title: string;
  /** 名字前的小色块，和这一段在条上的颜色一样：没有图例时靠它把颜色和名字对上。 */
  swatch: ResourceTooltipSwatch;
  /** 软件的展示分组（类型固定色按它上色）；空余、系统/驱动保留和进程没有。 */
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

// 能选的段由 resourceSelection.ts 的 selectableResourceSegmentIds 定（数据更新后保留选择也按它），这里只补上呈现要的字段。
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

// 空白段的名字是前端自己造的，直接用；软件段按共用规则出名。
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

// 条上说明保留量已拆成分项（residualSource 为 counters）时，保留量下面的分项按后端顺序各占一行；分项不是进程。
function residualLeafLines(bar: ResourceBarView, segment: ResourceSelectableSegment) {
  if (segment.isEmpty || bar.residualSource !== "counters" || !isReservedSegment(segment)) return [];
  return (segment.processes ?? []).map((leaf) => `${resourceProcessDisplayName(leaf, bar.metricId)} ${formatResourceBarValue(bar, leaf.value)}`);
}

/** 容量类分项写“系统占比”；吞吐类分项按本帧总量比较，写“占当前总量”。 */
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

// 条头的读数。后端读不到时带原因（无读数合同）：没有读数就用原因代替数值；总量有但拆不到软件时写在数值后面。
// 原因按类翻译，不认识的代码原样显示。
function resourceBarValueText(bar: ResourceBarView) {
  const reason = bar.unavailableReason ? resourceBarUnavailableText(bar.unavailableReason) : null;
  if (reason === null) return resourceBarTotalText(bar);
  return bar.totalValue === null ? reason : `${resourceBarTotalText(bar)} · ${reason}`;
}

// 含空余时整条是容量：总量后面写上容量，一眼看出条有多长。百分比的容量就是 100%，不重复写。
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
    // 放不放得下由画布按像素宽度决定。
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

/*
  展示分组里哪些算"系统"。

  **这里认的是 displayKind，不是 kind。** 哪些原始类别该并成一类是后端的事
  （SoftwareDisplayKinds.Project：RuntimeProduct 和 RuntimeRoot 并进 General、
  RuntimePackage 并成 WindowsApp……）。前端再照着 kind 自己并一遍，
  就是同一条规则两个属主 —— 后端加一个类别，前端不改就悄悄把它落进"其它"。

  前端这一层只决定**展示分组用什么颜色**，那才是画面该管的事。
*/
const systemDisplayKinds = new Set(["windows_system", "windows_component", "windows_service"]);

type ResourceSoftwareBucket = "system" | "adapted" | "other";

function resourceSoftwareBucket(displayKind: string): ResourceSoftwareBucket {
  if (systemDisplayKinds.has(displayKind)) {
    return "system";
  }

  return displayKind === "adapted" ? "adapted" : "other";
}

// 分段颜色全是 CSS 变量（styles/theme.css 的 --resource-*），画布按当前风格和配色解析，风格文件可以改；
// 这里只决定哪一段用哪个变量。区分色按 softwareId 散列，同一个软件始终同一个颜色。
const distinctPalette = Array.from({ length: 8 }, (_, index) => `var(--resource-segment-${index + 1})`);
const segmentTextColor = "var(--resource-segment-text)";
const reservedColor = "var(--resource-reserved)";

// 按展示分组配色（系统 / 已适配 / 其它）。分组本身由上面那一个函数定，不在这里再判一遍。
const bucketColors: Record<ResourceSoftwareBucket, string> = {
  system: "var(--resource-kind-system)",
  adapted: "var(--resource-kind-adapted)",
  other: "var(--resource-kind-other)"
};



function resourceProxyClass(base: string, states: Record<string, boolean>) {
  return [base, "resource-segment-interaction-proxy", ...Object.keys(states).filter((name) => states[name])].join(" ");
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

/**
 * 悬停框、选中框和提示所在的交互层：换到另一段时直接跳过去（不跟在指针后面滑 --motion-bar 那么久，
 * 提示也按新位置摆）；还是同一段、只是数据变了时才和画布一起过渡。
 * 标识变化的那一帧不带过渡，下一帧起才带。
 */
function createSegmentChangeJump(id: () => string | null) {
  const [settledId, setSettledId] = createSignal<string | null>(null);
  createEffect(() => {
    const current = id();
    const frame = requestAnimationFrame(() => setSettledId(current));
    onCleanup(() => cancelAnimationFrame(frame));
  });
  return () => id() !== null && id() === settledId();
}
