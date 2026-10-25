import { localizedMetricLabel, metricCardReading, resourceMetricBasis
} from "../../presentation/metricLabels";
import { Plus, Replace, X } from "lucide-solid";
import { For, Show } from "solid-js";
import { createReorderGroup } from "../../interactions/solid/reorderGroup.ts";
import type { ReorderGroup } from "../../interactions/solid/reorderGroup.ts";
import type { DashboardCardSettings, MetricDefinition, MetricSnapshot } from "../../types";
import { dashboardSmallMetricLimit } from "../../stores/monitorConfig";
import { uiText } from "../../text.ts";
import { deviceFieldScopeKind } from "./fieldCards.ts";
import { ReadingSourceMark } from "../../components/ReadingSourceMark.tsx";
import { useFrontendVisibilityDemand } from "../../frontendWork/useFrontendVisibilityDemand";
import { frontendVisibilityDemandId, frontendVisibilitySurface } from "../../frontendWork/frontendVisibilitySurface";
import { dashboardMetricAvailable } from "../../preferences/dashboardPreferences.ts";
import type { DashboardMetricBinding } from "../../types";

interface DashboardProps {
  cards: DashboardCardSettings[];
  catalog: MetricDefinition[];
  snapshot: MetricSnapshot | null;
  editMode: boolean;
  metricSelectionAvailable: boolean;
  onOpenMetricModal: (target: { cardId: string; slot: "main" | "small"; index: number; currentMetricId: string | null }) => void;
  onClearSlot: (cardId: string, slot: "main" | "small", index: number) => void;
  onMoveCard: (sourceCardId: string, targetCardId: string) => void;
  onMoveMetricSlot: (source: DashboardSlotRef, target: DashboardSlotRef) => void;
}

export interface DashboardSlotRef {
  cardId: string;
  slot: "main" | "small";
  index: number;
}

function slotKey(slot: DashboardSlotRef) {
  return `${slot.cardId}|${slot.slot}|${slot.index}`;
}

export function Dashboard(props: DashboardProps) {
  // 编辑时拖动排序：卡片一组，指标槽一组（槽在卡片里，按在有指标的槽上拖的是槽）。
  const cardReorder = createReorderGroup("仪表盘卡片", (source, target) => props.onMoveCard(source, target));
  const slotReorder = createReorderGroup("仪表盘指标槽", (sourceKey, targetKey) => {
    const source = parseSlotKey(sourceKey);
    const target = parseSlotKey(targetKey);
    if (source && target) {
      props.onMoveMetricSlot(source, target);
    }
  });

  return (
    <section class="dashboard" aria-label={uiText.dashboard.panel}>
      <For each={props.cards}>
        {(card, cardIndex) => {
          const demandId = frontendVisibilityDemandId("monitor.card", card.id);
          useFrontendVisibilityDemand(demandId, ["monitor.metrics"]);
          return (
          <article
            {...frontendVisibilitySurface(demandId, [demandId])}
            class="metric-card"
            classList={{ editing: props.editMode }}
            data-card-id={card.id}
            ref={cardReorder.item({ id: () => card.id, enabled: () => props.editMode })}
          >
            <div class="metric-main">
              <MetricSlot
                card={card}
                cardOrdinal={cardIndex() + 1}
                slot="main"
                index={0}
                metricId={card.main ?? null}
                editMode={props.editMode}
                metricSelectionAvailable={props.metricSelectionAvailable}
                catalog={props.catalog}
                snapshot={props.snapshot}
                reorder={slotReorder}
                onOpenMetricModal={props.onOpenMetricModal}
                onClearSlot={props.onClearSlot}
              />
            </div>
            <div class="metric-small-grid">
              <For each={smallSlots(card, props.editMode)}>
                {(metricId, index) => (
                  <MetricSlot
                    card={card}
                    cardOrdinal={cardIndex() + 1}
                    slot="small"
                    index={index()}
                    metricId={metricId}
                    editMode={props.editMode}
                    metricSelectionAvailable={props.metricSelectionAvailable}
                    catalog={props.catalog}
                    snapshot={props.snapshot}
                    reorder={slotReorder}
                    onOpenMetricModal={props.onOpenMetricModal}
                    onClearSlot={props.onClearSlot}
                  />
                )}
              </For>
            </div>
          </article>
          );
        }}
      </For>
    </section>
  );
}

function smallSlots(card: DashboardCardSettings, editMode: boolean) {
  const metrics = card.small ?? [];
  if (!editMode) {
    return metrics;
  }

  // 编辑时在已有指标后面留一个空槽，满了就不留。
  const count = Math.min(metrics.length + 1, dashboardSmallMetricLimit);
  return Array.from({ length: count }, (_, index) => metrics[index] ?? null);
}

function MetricSlot(props: {
  card: DashboardCardSettings;
  cardOrdinal: number;
  slot: "main" | "small";
  index: number;
  metricId: string | null;
  editMode: boolean;
  metricSelectionAvailable: boolean;
  catalog: MetricDefinition[];
  snapshot: MetricSnapshot | null;
  reorder: ReorderGroup;
  onOpenMetricModal: DashboardProps["onOpenMetricModal"];
  onClearSlot: DashboardProps["onClearSlot"];
}) {
  const isMain = () => props.slot === "main";
  const slotRef = (): DashboardSlotRef => ({ cardId: props.card.id, slot: props.slot, index: props.index });
  const key = () => slotKey(slotRef());
  const metricLabel = () => localizedMetricLabel(
    props.metricId,
    props.catalog.find((metric) => metric.id === props.metricId)?.label)
    ?? props.metricId
    ?? uiText.dashboard.emptyMetric;
  const slotLabel = () => props.slot === "main"
    ? uiText.dashboard.primarySlot(props.cardOrdinal)
    : uiText.dashboard.detailSlot(props.cardOrdinal, props.index + 1);
  return (
    <Show
      when={props.editMode}
      fallback={<MetricView metricId={props.metricId} isMain={isMain()} catalog={props.catalog} snapshot={props.snapshot} />}
    >
      <div
        class="edit-slot"
        classList={{ "has-metric": Boolean(props.metricId) }}
        ref={props.reorder.item({ id: key, enabled: () => props.editMode && Boolean(props.metricId) })}
      >
        <Show
          when={props.metricId}
          fallback={
            <button
              type="button"
              class="slot-add"
              aria-label={uiText.dashboard.addMetric(slotLabel())}
              disabled={!props.metricSelectionAvailable}
              title={!props.metricSelectionAvailable ? uiText.dashboard.catalogUnavailable : undefined}
              onClick={() => props.onOpenMetricModal({ cardId: props.card.id, slot: props.slot, index: props.index, currentMetricId: null })}
            >
              <Plus size={16} aria-hidden="true" />
            </button>
          }
        >
          <MetricView metricId={props.metricId} binding={props.slot === "main" ? props.card.mainBinding : props.card.smallBindings?.[props.index]}
            isMain={isMain()} catalog={props.catalog} snapshot={props.snapshot} />
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
              onClick={() => props.onOpenMetricModal({ cardId: props.card.id, slot: props.slot, index: props.index, currentMetricId: props.metricId })}
            >
              <Replace size={13} aria-hidden="true" />
            </button>
            <button
              type="button"
              class="slot-delete"
              aria-label={uiText.dashboard.removeMetric(slotLabel(), metricLabel())}
              title={uiText.dashboard.removeMetric(slotLabel(), metricLabel())}
              onClick={() => props.onClearSlot(props.card.id, props.slot, props.index)}
            >
              <X size={13} aria-hidden="true" />
            </button>
          </span>
        </Show>
      </div>
    </Show>
  );
}

function MetricView(props: {
  binding?: DashboardMetricBinding | null;
  metricId: string | null;
  isMain: boolean;
  catalog: MetricDefinition[];
  snapshot: MetricSnapshot | null;
}) {
  const absent = () => props.metricId && props.catalog.length > 0 && !dashboardMetricAvailable(props.metricId, props.binding, props.catalog);
  const metric = () => props.metricId && !absent() ? props.snapshot?.items?.[props.metricId] : null;
  const definition = () => props.catalog.find((item) => item.id === props.metricId);
  const snapshotLabel = () => {
    const label = metric()?.label?.trim();
    return label && label !== props.metricId ? label : null;
  };
  // 目录和文案都认不出名字时写「指标不可用」，原 id 放悬停说明。
  const knownLabel = () => {
    if (!props.metricId) {
      return null;
    }
    const label = localizedMetricLabel(props.metricId, definition()?.label ?? snapshotLabel());
    return label && label !== props.metricId ? label : null;
  };
  const label = () => props.metricId ? knownLabel() ?? uiText.dashboard.metricUnavailable : "--";
  // 设备字段：标签下面写是哪台设备，悬停看字段说明。
  const deviceName = () => definition()?.scopeKind === deviceFieldScopeKind ? definition()!.group : null;
  // 标签可能被截断：悬停总能看到全名，再加设备与字段说明，或量纲说明。
  const labelTitle = () => {
    if (!props.metricId) {
      return undefined;
    }
    if (!knownLabel()) {
      return props.metricId;
    }
    const extra = deviceName()
      ? [deviceName(), definition()?.detail]
      : [resourceMetricBasis(props.metricId)];
    return [label(), ...extra].filter(Boolean).join("\n");
  };
  const reading = () => absent() ? { value: uiText.dashboard.deviceAbsent, gap: "", unit: "", missing: true, title: uiText.dashboard.deviceAbsentHint } : props.metricId
    ? metricCardReading(definition(), metric())
    : { value: "--", gap: "", unit: "", missing: true, title: undefined };
  return (
    <div class={props.isMain ? "metric-main-content" : "metric-small-row"}>
      <div class="metric-label" title={labelTitle()}>
        <span class="metric-label-text">{label()}</span>
        {/* 设备名前面是读数来源标记（首选／待确认），设备名过长时只截设备名。 */}
        <Show when={deviceName()}>
          {(name) => <span class="metric-device"><ReadingSourceMark mark={definition()?.readingSource?.mark} />{name()}</span>}
        </Show>
      </div>
      <div class="metric-value" classList={{ missing: reading().missing }} title={reading().title}>
        <Show when={reading().unit} fallback={reading().value}>
          <span class="metric-number">{reading().value}</span>{reading().gap}<span class="metric-unit">{reading().unit}</span>
        </Show>
      </div>
    </div>
  );
}

function parseSlotKey(value: string): DashboardSlotRef | null {
  const [cardId, slot, indexText] = value.split("|");
  const index = Number(indexText);
  return cardId && (slot === "main" || slot === "small") && Number.isInteger(index)
    ? { cardId, slot, index }
    : null;
}
