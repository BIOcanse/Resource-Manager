import { localizedMetricLabel, metricDisplayValue
} from "../../presentation/metricLabels";
import { createSignal, For, Show } from "solid-js";
import { pointerReorderProps } from "../../interactions/pointerReorder";
import type { DashboardCardSettings, MetricDefinition, MetricSnapshot } from "../../types";
import { uiText } from "../../text.ts";

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
  const [dragCardId, setDragCardId] = createSignal<string | null>(null);
  const [overCardId, setOverCardId] = createSignal<string | null>(null);
  const [dragSlotKey, setDragSlotKey] = createSignal<string | null>(null);
  const [overSlotKey, setOverSlotKey] = createSignal<string | null>(null);

  const clearDragState = () => {
    setDragCardId(null);
    setOverCardId(null);
    setDragSlotKey(null);
    setOverSlotKey(null);
  };

  return (
    <section class="dashboard" aria-label={uiText.dashboard.panel}>
      <For each={props.cards}>
        {(card, cardIndex) => {
          return (
          <article
            class="metric-card"
            classList={{
              editing: props.editMode,
              "drag-source": dragCardId() === card.id,
              "drag-over": overCardId() === card.id && dragCardId() !== null && dragCardId() !== card.id
            }}
            {...pointerReorderProps(() => ({
              enabled: props.editMode,
              group: "dashboard-card",
              sourceId: card.id,
              onStart: setDragCardId,
              onOver: setOverCardId,
              onCommit: props.onMoveCard,
              onEnd: clearDragState
            }))}
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
                dragSlotKey={dragSlotKey()}
                overSlotKey={overSlotKey()}
                onSlotDragStart={setDragSlotKey}
                onSlotDragOver={setOverSlotKey}
                onSlotDragEnd={clearDragState}
                onOpenMetricModal={props.onOpenMetricModal}
                onClearSlot={props.onClearSlot}
                onMoveMetricSlot={props.onMoveMetricSlot}
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
                    dragSlotKey={dragSlotKey()}
                    overSlotKey={overSlotKey()}
                    onSlotDragStart={setDragSlotKey}
                    onSlotDragOver={setOverSlotKey}
                    onSlotDragEnd={clearDragState}
                    onOpenMetricModal={props.onOpenMetricModal}
                    onClearSlot={props.onClearSlot}
                    onMoveMetricSlot={props.onMoveMetricSlot}
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

  const count = Math.min(Math.max(metrics.length + 1, 3), 3);
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
  dragSlotKey: string | null;
  overSlotKey: string | null;
  onSlotDragStart: (key: string | null) => void;
  onSlotDragOver: (key: string | null) => void;
  onSlotDragEnd: () => void;
  onOpenMetricModal: DashboardProps["onOpenMetricModal"];
  onClearSlot: DashboardProps["onClearSlot"];
  onMoveMetricSlot: DashboardProps["onMoveMetricSlot"];
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
        classList={{
          "has-metric": Boolean(props.metricId),
          "drag-source": props.dragSlotKey === key(),
          "drag-over": props.overSlotKey === key() && props.dragSlotKey !== null && props.dragSlotKey !== key()
        }}
        {...pointerReorderProps(() => ({
          enabled: props.editMode && Boolean(props.metricId),
          group: "dashboard-slot",
          sourceId: key(),
          onStart: props.onSlotDragStart,
          onOver: props.onSlotDragOver,
          onCommit: (sourceKey, targetKey) => {
            const source = parseSlotKey(sourceKey);
            const target = parseSlotKey(targetKey);
            if (source && target) {
              props.onMoveMetricSlot(source, target);
            }
          },
          onEnd: props.onSlotDragEnd
        }))}
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
              +
            </button>
          }
        >
          <MetricView metricId={props.metricId} isMain={isMain()} catalog={props.catalog} snapshot={props.snapshot} />
          <button
            type="button"
            class="slot-change"
            aria-label={uiText.dashboard.replaceMetric(slotLabel(), metricLabel())}
            disabled={!props.metricSelectionAvailable}
            title={!props.metricSelectionAvailable ? uiText.dashboard.catalogUnavailable : undefined}
            onClick={() => props.onOpenMetricModal({ cardId: props.card.id, slot: props.slot, index: props.index, currentMetricId: props.metricId })}
          >
            +
          </button>
          <button
            type="button"
            class="slot-delete"
            aria-label={uiText.dashboard.removeMetric(slotLabel(), metricLabel())}
            onClick={() => props.onClearSlot(props.card.id, props.slot, props.index)}
          >
            ×
          </button>
        </Show>
      </div>
    </Show>
  );
}

function MetricView(props: {
  metricId: string | null;
  isMain: boolean;
  catalog: MetricDefinition[];
  snapshot: MetricSnapshot | null;
}) {
  const metric = () => props.metricId ? props.snapshot?.items?.[props.metricId] : null;
  const definition = () => props.catalog.find((item) => item.id === props.metricId);
  const snapshotLabel = () => {
    const label = metric()?.label?.trim();
    return label && label !== props.metricId ? label : null;
  };
  const label = () => (props.metricId
    ? localizedMetricLabel(props.metricId, definition()?.label ?? snapshotLabel())
    : null)
    ?? (props.metricId ? uiText.dashboard.metricUnavailable : "--");
  const value = () => metricDisplayValue(metric(), definition() ? "N/A" : "--");
  return (
    <div class={props.isMain ? "metric-main-content" : "metric-small-row"}>
      <div class="metric-label">{label()}</div>
      <div class="metric-value">{value()}</div>
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
