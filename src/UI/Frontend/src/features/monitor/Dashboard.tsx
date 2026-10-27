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
  /** 后端编号的显卡各自的名字（monitorStore.gpuNames）：GPU 卡片在标签下用小字写出。 */
  gpuNames: ReadonlyMap<string, string>;
  snapshot: MetricSnapshot | null;
  /** 还在等首个读数的指标（monitorStore.cardPendingMetricIds）。 */
  pendingMetricIds: ReadonlySet<string>;
  /** 大、超大槽位里监控项最近的读数（monitorStore.cardTrends）。 */
  trends: CardTrends;
  editMode: boolean;
  metricSelectionAvailable: boolean;
  /** 序号等于槽位数时是在末尾新加一个这个大小的槽位，否则换掉那个槽位的监控项。 */
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

/** 一个槽位此刻在卡片里的位置：上边在第几个单位；key 是“监控项#它在这张卡片里第几次出现”，改大小、换位置时不变。 */
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
  // 编辑时拖动排序：卡片一组，槽位一组（槽在卡片里，按在槽上拖的是槽）。
  const cardReorder = createReorderGroup("仪表盘卡片", (source, target) => props.onMoveCard(source, target));
  const slotReorder = createReorderGroup("仪表盘指标槽", (sourceKey, targetKey) => {
    const source = parseSlotKey(sourceKey);
    const target = parseSlotKey(targetKey);
    if (source && target) {
      props.onMoveMetricSlot(source, target);
    }
  });
  // 选大小的菜单：“+”和槽位的改大小按钮共用一个。
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
        title: size === current ? uiText.dashboard.currentSize : undefined,
        onSelect: () => choose(size)
      }))
    });
  };
  // 卡片按 id 渲染：改槽位时卡片不重建，槽位的过渡才接得上。
  const cardIds = createMemo(() => props.cards.map((card) => card.id));

  return (
    <section class="dashboard" aria-label={uiText.dashboard.panel}>
      <For each={cardIds()}>
        {(cardId, cardIndex) => {
          // 卡片被移除时 For 先于这里收到新列表的时刻，沿用上一份。
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
                {/* 编辑时剩余的单位是一块“+”：先选大小，再选监控项。没有剩余时不显示。 */}
                <Show when={props.editMode && remaining() >= 1}>
                  <SlotFrame name={`${cardId}|+`} top={used()} units={remaining()} class="card-slot card-slot-add">
                    <button
                      type="button"
                      class="slot-add"
                      aria-label={uiText.dashboard.addSlot(ordinal())}
                      aria-haspopup="menu"
                      disabled={!props.metricSelectionAvailable}
                      title={!props.metricSelectionAvailable ? uiText.dashboard.catalogUnavailable : uiText.dashboard.addSlot(ordinal())}
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

/**
 * 卡片里一块区域（一个槽位，或编辑时的“+”）：上边和高度写成 CSS 变量 --slot-top、--slot-units（styles/monitor.css 按单位步长换算）。
 * 增删、换监控项、改大小、拖动排序放下以后，区域当即在终态的上边和高度、可命中：这是稍大一些的变化，按进场退场一类
 * （docs/animation_style_2026-10-26.md「总原则」），滑过去的画面由呈现层画在不可命中的副本上（presentation/nodeAnimation.ts 的 reflowNode）。
 * 新挂上的区域第一帧就在终态，编辑槽和“+”由样式表淡入。
 */
function SlotFrame(props: { name: string; top: number; units: number; class: string; children: JSX.Element }) {
  let element!: HTMLDivElement;
  const top = createMemo(() => props.top);
  const units = createMemo(() => props.units);
  const write = (target: HTMLElement, value: Vec) => {
    target.style.setProperty("--slot-top", value.x.toFixed(4));
    target.style.setProperty("--slot-units", value.y.toFixed(4));
  };
  // 第一次运行是挂上（ref 里已经画好），之后每次变化是一次重排。
  createEffect(on(() => ({ x: top(), y: units() }), (next, previous) => {
    if (!previous || (previous.x === next.x && previous.y === next.y)) return;
    reflowNode(element, { name: `卡片槽位「${props.name}」`, from: previous, to: next, draw: write });
  }));
  return (
    <div
      ref={(node) => {
        element = node;
        // 挂上时就在终态的上边和高度。
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
      gpuNames={props.gpuNames} snapshot={props.snapshot} pending={props.pending} trend={props.trend} />
  );
  return (
    <Show when={props.editMode} fallback={view()}>
      <div
        class="edit-slot has-metric"
        ref={(element: HTMLElement) => { props.reorder.item({ id: key, enabled: () => props.editMode })(element); exitOnUnmount(element); }}
      >
        {view()}
        {/* 槽内操作：悬停或聚焦这一槽时显示。 */}
        <span class="slot-actions">
          <button
            type="button"
            class="slot-change"
            aria-label={uiText.dashboard.replaceMetric(slotLabel(), metricLabel())}
            disabled={!props.metricSelectionAvailable}
            title={!props.metricSelectionAvailable
              ? uiText.dashboard.catalogUnavailable
              : uiText.dashboard.replaceMetric(slotLabel(), metricLabel())}
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
            title={uiText.dashboard.resizeSlot(slotLabel(), metricLabel())}
            onClick={(event) => props.onOpenResize(event.currentTarget)}
          >
            <UnfoldVertical size={13} aria-hidden="true" />
          </button>
          <button
            type="button"
            class="slot-delete"
            aria-label={uiText.dashboard.removeMetric(slotLabel(), metricLabel())}
            title={uiText.dashboard.removeMetric(slotLabel(), metricLabel())}
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
  trend: readonly CardTrendPoint[];
}) {
  const absent = () => props.catalog.length > 0 && !dashboardMetricAvailable(props.metricId, props.binding, props.catalog);
  const metric = () => !absent() ? props.snapshot?.items?.[props.metricId] : null;
  const definition = () => props.catalog.find((item) => item.id === props.metricId);
  const snapshotLabel = () => {
    const label = metric()?.label?.trim();
    return label && label !== props.metricId ? label : null;
  };
  // 目录和文案都认不出名字时写「指标不可用」，原 id 放悬停说明。
  const knownLabel = () => {
    const label = localizedMetricLabel(props.metricId, definition()?.label ?? snapshotLabel());
    return label && label !== props.metricId ? label : null;
  };
  const label = () => knownLabel() ?? uiText.dashboard.metricUnavailable;
  // 设备字段：标签下面写是哪台设备，悬停看字段说明。按编号的显卡指标（gpu.N.*）同样写这块显卡的名字，编号原样。
  const fieldDevice = () => definition()?.scopeKind === deviceFieldScopeKind;
  const deviceName = () => fieldDevice() ? definition()!.group : gpuNameForMetric(props.metricId, props.gpuNames);
  // 标签可能被截断：悬停总能看到全名，再加设备与字段说明，或量纲说明。
  const labelTitle = () => {
    if (!knownLabel()) {
      return props.metricId;
    }
    const extra = fieldDevice()
      ? [deviceName(), definition()?.detail]
      : [deviceName(), resourceMetricBasis(props.metricId)];
    return [label(), ...extra].filter(Boolean).join("\n");
  };
  const reading = createMemo(() => absent()
    ? { value: uiText.dashboard.deviceAbsent, gap: "", unit: "", missing: true, title: uiText.dashboard.deviceAbsentHint }
    : metricCardReading(definition(), metric()));
  // 首个读数还没到：画一条占位，不先写「无读数」再跳成数值。设备不在照常直接写。
  const waiting = () => props.pending && !absent();
  const withTrend = () => props.size === "large" || props.size === "full";
  return (
    <div class={`${props.size === "small" ? "metric-small-row" : `metric-main-content metric-size-${props.size}`}${deviceName() ? " with-device" : ""}`}>
      <div class="metric-label" title={labelTitle()}>
        <span class="metric-label-text">{label()}</span>
        {/* 设备名前面是读数来源标记（首选／待确认），设备名过长时只截设备名。 */}
        <Show when={deviceName()}>
          {(name) => <span class="metric-device"><ReadingSourceMark mark={definition()?.readingSource?.mark} />{name()}</span>}
        </Show>
      </div>
      <Show
        when={!waiting()}
        fallback={
          <div class="metric-value pending" title={uiText.dashboard.waitingReading}>
            <span class="metric-pending-bar" aria-hidden="true" />
            <span class="sr-only">{uiText.dashboard.waitingReading}</span>
          </div>
        }
      >
        <div class="metric-value" classList={{ missing: reading().missing }} title={reading().title}>
          <Show when={reading().unit} fallback={reading().value}>
            {/* 数值和单位之间的空格放在单位里，按单位的字号排：终端风格的等宽大字号下空格不会宽出一截。 */}
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

/** 大、超大槽位的趋势：最近 cardTrendCapacity 次读数的折线和线下的面；超大槽位再写最低、最高。 */
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
