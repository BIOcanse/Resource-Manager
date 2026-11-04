import { localizedMetricLabel, metricCardReading, resourceMetricBasis
} from "../../presentation/metricLabels";
import { Plus, Replace, UnfoldVertical, X } from "lucide-solid";
import { createEffect, createMemo, createSignal, For, on, Show, untrack } from "solid-js";
import type { JSX } from "solid-js";
import { createReorderGroup } from "../../interactions/solid/reorderGroup.ts";
import type { ReorderGroup } from "../../interactions/solid/reorderGroup.ts";
import type { Vec } from "../../interactions/protocol/vec.ts";
import type { DashboardCardSettings, DashboardCardSlot, DashboardMetricBinding, DashboardSlotSize, MetricDefinition, MetricSnapshot } from "../../types";
import { uiText } from "../../text.ts";
import { deviceFieldScopeKind } from "./fieldCards.ts";
import { gpuNameForMetric } from "./gpuAdapterNames.ts";
import { cardUnits, remainingUnits, sizesThatFit, slotUnits, usedUnits } from "./cardSlots.ts";
import { cardTrendCapacity, cardTrendPaths, cardTrendRange } from "./cardTrends.ts";
import type { CardTrendPoint, CardTrends } from "./cardTrends.ts";
import { ReadingSourceMark } from "../../components/ReadingSourceMark.tsx";
import { StandardContextMenu } from "../../components/StandardContextMenu.tsx";
import type { StandardContextMenuModel } from "../../components/StandardContextMenu.tsx";
import { useFrontendVisibilityDemand } from "../../frontendWork/useFrontendVisibilityDemand";
import { frontendVisibilitySourceDemand, frontendVisibilitySurface } from "../../frontendWork/frontendVisibilitySurface";
import { frontendVisibilitySourceKinds, frontendWorkIds } from "../../frontendWork/frontendWorkIds";
import { dashboardMetricAvailable } from "../../preferences/dashboardPreferences.ts";
import { exitOnUnmount, reflowNode } from "../../presentation/nodeAnimation.ts";

interface DashboardProps {
  cards: DashboardCardSettings[];
  catalog: MetricDefinition[];
  /** Backend GPU names keyed by their unchanged indices. */
  gpuNames: ReadonlyMap<string, string>;
  snapshot: MetricSnapshot | null;
  /** Metrics awaiting their first reading. */
  pendingMetricIds: ReadonlySet<string>;
  /** Metrics whose source failed; distinguish failure from missing readings. */
  failedMetricIds: ReadonlySet<string>;
  /** Recent readings for large slots. */
  trends: CardTrends;
  editMode: boolean;
  metricSelectionAvailable: boolean;
  /** An index equal to the slot count appends; other indices replace. */
  onOpenMetricModal: (target: { cardId: string; index: number; size: DashboardSlotSize; currentMetricId: string | null }) => void;
  onClearSlot: (cardId: string, index: number) => void;
  onResizeSlot: (cardId: string, index: number, size: DashboardSlotSize) => void;
  onMoveCard: (sourceCardId: string, targetCardId: string) => void;
  onMoveMetricSlot: (source: DashboardSlotRef, target: DashboardSlotRef) => void;
}

export interface DashboardSlotRef {
  cardId: string;
  index: number;
}

/** Keep slot keys stable across resizing and reordering. */
interface SlotPlace {
  key: string;
  index: number;
  top: number;
  slot: DashboardCardSlot;
}

function slotPlaces(slots: readonly DashboardCardSlot[]): SlotPlace[] {
  const seen = new Map<string, number>();
  let top = 0;
  return slots.map((slot, index) => {
    const occurrence = seen.get(slot.metricId) ?? 0;
    seen.set(slot.metricId, occurrence + 1);
    const place = { key: `${slot.metricId}#${occurrence}`, index, top, slot };
    top += slotUnits(slot.size);
    return place;
  });
}

export function Dashboard(props: DashboardProps) {
  // Reorder cards and nested slots independently.
  const cardReorder = createReorderGroup("仪表盘卡片", (source, target) => props.onMoveCard(source, target));
  const slotReorder = createReorderGroup("仪表盘指标槽", (sourceKey, targetKey) => {
    const source = parseSlotKey(sourceKey);
    const target = parseSlotKey(targetKey);
    if (source && target) {
      props.onMoveMetricSlot(source, target);
    }
  });
  // Share the size menu between adding and resizing.
  const [sizeMenu, setSizeMenu] = createSignal<StandardContextMenuModel | null>(null);
  const openSizeMenu = (button: HTMLElement, id: string, sizes: readonly DashboardSlotSize[], current: DashboardSlotSize | null,
    choose: (size: DashboardSlotSize) => void) => {
    const rect = button.getBoundingClientRect();
    setSizeMenu({
      id,
      x: rect.left,
      y: rect.bottom + 4,
      returnFocusTarget: button,
      items: sizes.map((size) => ({
        id: size,
        label: `${uiText.dashboard.slotSize[size]} · ${uiText.dashboard.slotSizeHint[size]}`,
        disabled: size === current,
        disabledReason: size === current ? uiText.dashboard.currentSize : undefined,
        onSelect: () => choose(size)
      }))
    });
  };
  // Key cards by ID so slot changes retain their transition nodes.
  const cardIds = createMemo(() => props.cards.map((card) => card.id));

  return (
    <section class="dashboard" aria-label={uiText.dashboard.panel}>
      <For each={cardIds()}>
        {(cardId, cardIndex) => {
          // Retain the previous list until removed cards finish unmounting.
          const card = createMemo<DashboardCardSettings>((previous) => props.cards.find((item) => item.id === cardId) ?? previous!);
          const demand = frontendVisibilitySourceDemand(frontendVisibilitySourceKinds.monitorCard, cardId);
          useFrontendVisibilityDemand(demand, [frontendWorkIds.monitorMetrics]);
          const places = createMemo(() => slotPlaces(card().slots));
          const keys = createMemo(() => places().map((place) => place.key));
          const used = createMemo(() => usedUnits(card().slots));
          const remaining = createMemo(() => remainingUnits(card().slots));
          const ordinal = () => cardIndex() + 1;
          const addSlot = (button: HTMLElement) => openSizeMenu(button, `add|${cardId}|${card().slots.length}`, sizesThatFit(card().slots), null,
            (size) => props.onOpenMetricModal({ cardId, index: card().slots.length, size, currentMetricId: null }));
          return (
            <article
              {...frontendVisibilitySurface(demand.demandId, [demand.demandId])}
              class="metric-card"
              classList={{ editing: props.editMode }}
              data-card-id={cardId}
              ref={cardReorder.item({ id: () => cardId, enabled: () => props.editMode })}
            >
              <div class="metric-slots" style={{ "--card-units": String(Math.max(cardUnits, used())) }}>
                <For each={keys()}>
                  {(key) => {
                    const place = createMemo<SlotPlace>((previous) => places().find((item) => item.key === key) ?? previous!);
                    const slot = () => place().slot;
                    return (
                      <SlotFrame name={`${cardId}|${key}`} top={place().top} units={slotUnits(slot().size)}
                        class={`card-slot size-${slot().size}`}>
                        <MetricSlot
                          cardId={cardId}
                          cardOrdinal={ordinal()}
                          index={place().index}
                          slot={slot()}
                          canResize={sizesThatFit(card().slots, place().index).length > 1}
                          editMode={props.editMode}
                          metricSelectionAvailable={props.metricSelectionAvailable}
                          catalog={props.catalog}
                          gpuNames={props.gpuNames}
                          snapshot={props.snapshot}
                          pending={props.pendingMetricIds.has(slot().metricId)}
                          failed={props.failedMetricIds.has(slot().metricId)}
                          trend={props.trends.get(slot().metricId) ?? []}
                          reorder={slotReorder}
                          onOpenMetricModal={props.onOpenMetricModal}
                          onClearSlot={props.onClearSlot}
                          onOpenResize={(button) => openSizeMenu(button, `resize|${cardId}|${place().index}`,
                            sizesThatFit(card().slots, place().index), slot().size,
                            (size) => props.onResizeSlot(cardId, place().index, size))}
                        />
                      </SlotFrame>
                    );
                  }}
                </For>
                {/* Offer sizes that fit the remaining card space. */}
                <Show when={props.editMode && remaining() >= 1}>
                  <SlotFrame name={`${cardId}|+`} top={used()} units={remaining()} class="card-slot card-slot-add">
                    <button
                      type="button"
                      class="slot-add"
                      aria-label={uiText.dashboard.addSlot(ordinal())}
                      aria-haspopup="menu"
                      disabled={!props.metricSelectionAvailable}
                      data-name-tooltip=""
                      onClick={(event) => addSlot(event.currentTarget)}
                    >
                      <Plus size={16} aria-hidden="true" />
                    </button>
                  </SlotFrame>
                </Show>
              </div>
            </article>
          );
        }}
      </For>
      <StandardContextMenu model={sizeMenu()} onClose={() => setSizeMenu(null)} />
    </section>
  );
}

/** Commit hit geometry immediately; animate reflow using noninteractive presentation copies. */
function SlotFrame(props: { name: string; top: number; units: number; class: string; children: JSX.Element }) {
  let element!: HTMLDivElement;
  const top = createMemo(() => props.top);
  const units = createMemo(() => props.units);
  const write = (target: HTMLElement, value: Vec) => {
    target.style.setProperty("--slot-top", value.x.toFixed(4));
    target.style.setProperty("--slot-units", value.y.toFixed(4));
  };
  // Initial geometry is already painted; later changes reflow.
  createEffect(on(() => ({ x: top(), y: units() }), (next, previous) => {
    if (!previous || (previous.x === next.x && previous.y === next.y)) return;
    reflowNode(element, { name: `卡片槽位「${props.name}」`, from: previous, to: next, draw: write });
  }));
  return (
    <div
      ref={(node) => {
        element = node;
        // Mount directly at the final position and height.
        write(node, untrack(() => ({ x: top(), y: units() })));
      }}
      class={props.class}
    >
      {props.children}
    </div>
  );
}

function MetricSlot(props: {
  cardId: string;
  cardOrdinal: number;
  index: number;
  slot: DashboardCardSlot;
  canResize: boolean;
  editMode: boolean;
  metricSelectionAvailable: boolean;
  catalog: MetricDefinition[];
  gpuNames: ReadonlyMap<string, string>;
  snapshot: MetricSnapshot | null;
  pending: boolean;
  failed: boolean;
  trend: readonly CardTrendPoint[];
  reorder: ReorderGroup;
  onOpenMetricModal: DashboardProps["onOpenMetricModal"];
  onClearSlot: DashboardProps["onClearSlot"];
  onOpenResize: (button: HTMLElement) => void;
}) {
  const key = () => `${props.cardId}|${props.index}`;
  const metricLabel = () => localizedMetricLabel(
    props.slot.metricId,
    props.catalog.find((metric) => metric.id === props.slot.metricId)?.label)
    ?? props.slot.metricId;
  const slotLabel = () => uiText.dashboard.slotLabel(props.cardOrdinal, props.index + 1);
  const view = () => (
    <MetricView metricId={props.slot.metricId} binding={props.slot.binding} size={props.slot.size} catalog={props.catalog}
      gpuNames={props.gpuNames} snapshot={props.snapshot} pending={props.pending} failed={props.failed} trend={props.trend} />
  );
  return (
    <Show when={props.editMode} fallback={view()}>
      <div
        class="edit-slot has-metric"
        ref={(element: HTMLElement) => { props.reorder.item({ id: key, enabled: () => props.editMode })(element); exitOnUnmount(element); }}
      >
        {view()}
        {/* Show slot actions on hover or focus. */}
        <span class="slot-actions">
          <button
            type="button"
            class="slot-change"
            aria-label={uiText.dashboard.replaceMetric(slotLabel(), metricLabel())}
            disabled={!props.metricSelectionAvailable}
            data-name-tooltip=""
            onClick={() => props.onOpenMetricModal({ cardId: props.cardId, index: props.index, size: props.slot.size, currentMetricId: props.slot.metricId })}
          >
            <Replace size={13} aria-hidden="true" />
          </button>
          <button
            type="button"
            class="slot-resize"
            aria-label={uiText.dashboard.resizeSlot(slotLabel(), metricLabel())}
            aria-haspopup="menu"
            disabled={!props.canResize}
            data-name-tooltip=""
            onClick={(event) => props.onOpenResize(event.currentTarget)}
          >
            <UnfoldVertical size={13} aria-hidden="true" />
          </button>
          <button
            type="button"
            class="slot-delete"
            aria-label={uiText.dashboard.removeMetric(slotLabel(), metricLabel())}
            data-name-tooltip=""
            onClick={() => props.onClearSlot(props.cardId, props.index)}
          >
            <X size={13} aria-hidden="true" />
          </button>
        </span>
      </div>
    </Show>
  );
}

function MetricView(props: {
  binding?: DashboardMetricBinding | null;
  metricId: string;
  size: DashboardSlotSize;
  catalog: MetricDefinition[];
  gpuNames: ReadonlyMap<string, string>;
  snapshot: MetricSnapshot | null;
  pending: boolean;
  failed: boolean;
  trend: readonly CardTrendPoint[];
}) {
  const absent = () => props.catalog.length > 0 && !dashboardMetricAvailable(props.metricId, props.binding, props.catalog);
  const metric = () => !absent() ? props.snapshot?.items?.[props.metricId] : null;
  const definition = () => props.catalog.find((item) => item.id === props.metricId);
  const snapshotLabel = () => {
    const label = metric()?.label?.trim();
    return label && label !== props.metricId ? label : null;
  };
  // Use an unavailable label for unknown metrics and preserve the ID in the tooltip.
  const knownLabel = () => {
    const label = localizedMetricLabel(props.metricId, definition()?.label ?? snapshotLabel());
    return label && label !== props.metricId ? label : null;
  };
  const label = () => knownLabel() ?? uiText.dashboard.metricUnavailable;
  // Show device names for fields and indexed GPUs without changing backend indices.
  const fieldDevice = () => definition()?.scopeKind === deviceFieldScopeKind;
  const deviceName = () => fieldDevice() ? definition()!.group : gpuNameForMetric(props.metricId, props.gpuNames);
  // Preserve the full label and field or unit explanation in the tooltip.
  const labelTitle = () => {
    if (!knownLabel()) {
      return props.metricId;
    }
    const extra = fieldDevice()
      ? [deviceName(), definition()?.detail]
      : [deviceName(), resourceMetricBasis(props.metricId)];
    return [label(), ...extra].filter(Boolean).join("\n");
  };
  // Distinguish source failure from missing readings; missing-device state takes precedence.
  const reading = createMemo(() => absent()
    ? { value: uiText.dashboard.deviceAbsent, gap: "", unit: "", missing: true, title: uiText.dashboard.deviceAbsentHint }
    : props.failed
      ? { value: uiText.dashboard.readingFailed, gap: "", unit: "", missing: true, title: uiText.dashboard.readingFailedHint }
      : metricCardReading(definition(), metric()));
  // Keep a placeholder until the first reading; missing-device state takes precedence.
  const waiting = () => props.pending && !absent();
  const withTrend = () => props.size === "large" || props.size === "full";
  return (
    <div class={`${props.size === "small" ? "metric-small-row" : `metric-main-content metric-size-${props.size}`}${deviceName() ? " with-device" : ""}`}>
      <div class="metric-label" data-full-content={labelTitle()}>
        <span class="metric-label-text">{label()}</span>
        {/* Truncate only the device name, retaining the source marker. */}
        <Show when={deviceName()}>
          {(name) => <span class="metric-device"><ReadingSourceMark mark={definition()?.readingSource?.mark} />{name()}</span>}
        </Show>
      </div>
      <Show
        when={!waiting()}
        fallback={
          <div class="metric-value pending" data-full-content={uiText.dashboard.waitingReading}>
            <span class="metric-pending-bar" aria-hidden="true" />
            <span class="sr-only">{uiText.dashboard.waitingReading}</span>
          </div>
        }
      >
        <div class="metric-value" classList={{ missing: reading().missing, failed: props.failed && !absent() }} data-full-content={reading().title || undefined}>
          <Show when={reading().unit} fallback={reading().value}>
            {/* Include spacing in the unit text so its font determines the gap. */}
            <span class="metric-number">{reading().value}</span><span class="metric-unit">{reading().gap}{reading().unit}</span>
          </Show>
        </div>
      </Show>
      <Show when={withTrend() && !absent()}>
        <CardTrend label={label()} points={props.trend} percent={reading().unit.trim() === "%"} showRange={props.size === "full"} />
      </Show>
    </div>
  );
}

/** Keep bounded trends for large slots; extra-large slots also show extrema. */
function CardTrend(props: { label: string; points: readonly CardTrendPoint[]; percent: boolean; showRange: boolean }) {
  const paths = createMemo(() => cardTrendPaths(props.points, props.percent));
  const range = createMemo(() => cardTrendRange(props.points));
  const count = () => props.points.filter((point) => point.value !== null).length;
  return (
    <div class="metric-trend">
      <svg class="metric-trend-chart" viewBox="0 0 100 100" preserveAspectRatio="none" role="img"
        aria-label={uiText.dashboard.trendLabel(props.label, Math.min(count(), cardTrendCapacity))}>
        <path class="metric-trend-area" d={paths().area} />
        <path class="metric-trend-line" d={paths().line} />
      </svg>
      <Show when={props.showRange}>
        <dl class="metric-trend-range">
          <div>
            <dt>{uiText.dashboard.trendLow}</dt>
            <dd>{range()?.low.display ?? "--"}</dd>
          </div>
          <div>
            <dt>{uiText.dashboard.trendHigh}</dt>
            <dd>{range()?.high.display ?? "--"}</dd>
          </div>
        </dl>
      </Show>
    </div>
  );
}

function parseSlotKey(value: string): DashboardSlotRef | null {
  const separator = value.lastIndexOf("|");
  const cardId = value.slice(0, separator);
  const index = Number(value.slice(separator + 1));
  return separator > 0 && Number.isInteger(index) ? { cardId, index } : null;
}
