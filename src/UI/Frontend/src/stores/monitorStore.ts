import { usePushSource } from "../frontendRuntime/push/usePushSource.ts";
import { dashboardMetricAvailable, resolveDashboardBindings } from "../preferences/dashboardPreferences.ts";
import { subscribeFrontendPreferencesReset } from "../preferences/frontendPreferences.ts";
import { createEffect, createMemo, createSignal, on, onCleanup, untrack } from "solid-js";
import type { Accessor, Setter } from "solid-js";
import { saveDashboardSettings } from "../api";
import type { DashboardSlotRef } from "../features/monitor/Dashboard";
import { defaultPerformanceMetricIds } from "../features/resourceTable/ResourcePerformancePanel";
import { createResourceFactsProjection } from "../features/resourceTable/resourceFacts.ts";
import { defaultResourceTableSort, nextResourceTableSort, type ResourceTableSort } from "../features/resourceTable/resourceTableSort.ts";
import { gpuAdapterNames } from "../features/monitor/gpuAdapterNames.ts";
import type { ProcessMetricField } from "../data/resourceMonitor/processFieldCatalog.ts";
import { addTableColumn, removeTableColumn, tableSampleItems } from "../features/resourceTable/resourceTableColumns.ts";
import type { ResourcePrecisionSelection } from "../features/resourceBreakdown/ResourceBreakdown";
import { reconcileResourceSelection, toggledResourceSelection } from "../features/resourceBreakdown/resourceSelection";
import { isReservedSegment } from "../features/resourceBreakdown/resourceProcessIdentity.ts";
import type {
  DashboardCardSettings,
  DashboardSlotSize,
  DashboardSettings,
  DashboardSettingsResult,
  MetricDefinition,
  MetricSnapshot,
  ResourceBarSettings,
  ResourceBreakdownBar,
  ResourceBreakdownSnapshot,
  ResourceMonitorSnapshot,
  ResourceProcessDetailsSnapshot,
  ResourceTableColumnSettings,
  ResourceTableSnapshot,
  ResourceTableViewMode
} from "../types";
import { normalizeResourceBarScaleMode } from "../features/resourceBreakdown/resourceBarScaleCapabilities";
import { resourceBarQuery, resourceBarViews, type ResourceBarView } from "../features/resourceBreakdown/resourceBarSources.ts";
import {
  failedObservation,
  loadingObservation,
  observationCanRender,
  readyObservation,
  refreshingObservation,
  type ObservationState
} from "../observation/observationState";
import { userFacingErrorMessage } from "../presentation/userFacingText";
import { metricOfferedInPicker } from "../presentation/metricPresentation.ts";
import {
  normalizeResourceMonitorQuery,
  type ResourceBreakdownQuery,
  type ResourceMonitorQuery
} from "../data/resourceMonitor/resourceMonitorApi";
import {
  normalizeMetricSnapshotQuery,
  type MetricSnapshotQuery
} from "../data/monitor/monitorSourcesApi";
import type { SourceHandle } from
  "../frontendRuntime/source/SourceDescriptor";
import {
  sourceCanRender,
  type SourceSnapshot
} from "../frontendRuntime/source/SourceSnapshot";
import type { PushValueSource, PushValueSourceFamily } from
  "../frontendRuntime/push/PushValueSourceFamily";
import type {
  MetricFieldCatalogDevice,
  MetricFieldDevice,
  MetricFieldQuery
} from "../data/metricFields/metricFieldSource.ts";
import type { MetricCatalogPush } from "../data/monitor/metricCatalogPush.ts";
import {
  fieldCardDefinitions,
  fieldCardSlot,
  fieldCardSlots,
  fieldCardValues
} from "../features/monitor/fieldCards.ts";
import { appendCardTrends, type CardTrends } from "../features/monitor/cardTrends.ts";
import {
  addCardSlot,
  clampResourceTableColumnWidth,
  dashboardSettingsVersion,
  isProcessDetailResourceTableColumn,
  moveCardSlot,
  normalizeCards,
  normalizeResourceBars,
  normalizeResourceTableColumns,
  metricBindingForDefinition,
  removeCardSlot,
  replaceCardSlotMetric,
  resizeCardSlot
} from "./monitorConfig";
import { currentLanguage, uiText } from "../text.ts";
import { createResourceBreakdownBindings } from "./resourceBreakdownBindings.ts";

/** A slot index at the card length appends a slot; other indices replace its metric. */
export interface MetricModalTarget {
  cardId: string;
  index: number;
  size: DashboardSlotSize;
}

export interface OpenMetricModalTarget extends MetricModalTarget {
  currentMetricId: string | null;
}

export interface MonitorStoreOptions {
  onMetricDependenciesRequested: () => void;
  metricCatalogSource: SourceHandle<MetricDefinition[]>;
  /** Process field catalog for table source selection and header definitions. */
  processFieldCatalogSource: SourceHandle<readonly ProcessMetricField[]>;
  dashboardSettingsSource: SourceHandle<DashboardSettingsResult>;
  metricSnapshotSource: PushValueSourceFamily<MetricSnapshotQuery, MetricSnapshot>;
  /** Atomic revision, device and metric catalogs. Once push data arrives, only push frames may update the metric catalog. */
  metricFieldCatalogSource: PushValueSource<MetricCatalogPush>;
  /** Card device values subscribed by field set. */
  metricFieldsSource: PushValueSourceFamily<MetricFieldQuery, readonly MetricFieldDevice[]>;
  resourceMonitorSource: PushValueSourceFamily<
    ResourceMonitorQuery,
    ResourceMonitorSnapshot
  >;
  resourceProcessDetailsSource: PushValueSourceFamily<ResourceBreakdownQuery, ResourceProcessDetailsSnapshot>;
  metricSnapshotActive: Accessor<boolean>;
  visibleDashboardCardIds: Accessor<readonly string[]>;
  resourceBarsActive: Accessor<boolean>;
  resourceTableActive: Accessor<boolean>;
  metricSnapshotIntervalMs: Accessor<number | null>;
  resourceBarsIntervalMs: Accessor<number | null>;
  resourceTableIntervalMs: Accessor<number | null>;
}

export type DashboardSettingsLoadState = "loading" | "ready" | "error";

export interface MonitorStore {
  dashboardSettingsState: Accessor<DashboardSettingsLoadState>;
  dashboardSaveState: Accessor<"idle" | "saving" | "error">;
  resourceBarSaveState: Accessor<"idle" | "saving" | "error">;
  resourceTableSaveState: Accessor<"idle" | "saving" | "error">;
  editMode: Accessor<boolean>;
  catalog: Accessor<MetricDefinition[]>;
  catalogObservation: Accessor<ObservationState>;
  snapshot: Accessor<MetricSnapshot | null>;
  snapshotObservation: Accessor<ObservationState>;
  /** All card metrics, including canonical fields from each device. */
  cardCatalog: Accessor<MetricDefinition[]>;
  /** Selectable metrics filtered by the current reading source settings. */
  pickerCatalog: Accessor<MetricDefinition[]>;
  /** Card values combining metric snapshots and selected device fields. */
  cardSnapshot: Accessor<MetricSnapshot | null>;
  /** Metrics absent from the snapshot whose current query has neither answered nor failed; displayed as loading. */
  cardPendingMetricIds: Accessor<ReadonlySet<string>>;
  /** Missing device values whose source failed; metric source failures use the shared status indicator. */
  cardFailedMetricIds: Accessor<ReadonlySet<string>>;
  /** Device field status; ready without a subscription when no visible card needs device fields. */
  fieldObservation: Accessor<ObservationState>;
  /** Recent readings for large and extra-large slots. */
  cardTrends: Accessor<CardTrends>;
  activeCards: Accessor<DashboardCardSettings[]>;
  resourceBarEditing: Accessor<boolean>;
  activeResourceBars: Accessor<ResourceBarSettings[]>;
  resourceTableEditing: Accessor<boolean>;
  resourceTableMode: Accessor<ResourceTableViewMode>;
  activeResourceTableColumns: Accessor<ResourceTableColumnSettings[]>;
  /** Backend process field catalog; empty before loading. */
  processFields: Accessor<readonly ProcessMetricField[]>;
  resourceBreakdownSnapshot: Accessor<ResourceBreakdownSnapshot | null>;
  resourceProcessDetails: Accessor<ResourceProcessDetailsSnapshot | null>;
  resourceProcessDetailsObservation: Accessor<ObservationState>;
  resourceBarsObservation: Accessor<ObservationState>;
  resourceTableSnapshot: Accessor<ResourceTableSnapshot | null>;
  resourceTableObservation: Accessor<ObservationState>;
  resourceTableSort: Accessor<ResourceTableSort>;
  /** GPU names keyed by backend index; unnamed adapters are omitted. */
  gpuNames: Accessor<ReadonlyMap<string, string>>;
  expandedResourceTableRows: Accessor<Record<string, boolean>>;
  resourceSelection: Accessor<ResourcePrecisionSelection | null>;
  metricModalTarget: Accessor<MetricModalTarget | null>;
  selectedMetricId: Accessor<string | null>;
  setSelectedMetricId: (metricId: string | null) => void;
  /** Backend bars in layout order, using item IDs for explicitly selected sources. */
  orderedSnapshotBars: Accessor<ResourceBarView[]>;
  refreshCatalog: () => Promise<boolean>;
  refreshSettings: () => Promise<boolean>;
  addCard: () => void;
  enterEditMode: () => void;
  saveDraftAndLeaveEditMode: () => Promise<void>;
  cancelDashboardEdit: () => void;
  openMetricModal: (target: OpenMetricModalTarget) => void;
  closeMetricModal: () => void;
  confirmMetricSelection: () => void;
  clearMetricSlot: (cardId: string, index: number) => void;
  resizeMetricSlot: (cardId: string, index: number, size: DashboardSlotSize) => void;
  moveDashboardCard: (sourceCardId: string, targetCardId: string) => void;
  moveDashboardMetricSlot: (source: DashboardSlotRef, target: DashboardSlotRef) => void;
  toggleResourceBarEditMode: () => void;
  cancelResourceBarEdit: () => void;
  toggleResourceBar: (metricId: string, enabled: boolean) => void;
  updateResourceBarScale: (metricId: string, scaleMode: "capacity" | "active") => void;
  /** Stage a bar source change; null selects automatically. Saving persists the frontend layout. */
  changeResourceBarSource: (metricId: string, source: string | null) => void;
  reorderResourceBars: (sourceMetricId: string, targetMetricId: string) => void;
  changeResourceTableMode: (mode: ResourceTableViewMode) => void;
  toggleResourceTableEditMode: () => void;
  cancelResourceTableEdit: () => void;
  /** Cycle a column through descending, ascending and default combined ordering. */
  cycleResourceTableSort: (columnId: string) => void;
  toggleResourceTableColumn: (columnId: string, visible: boolean) => void;
  addResourceTableColumn: (columnId: string) => void;
  removeResourceTableColumn: (columnId: string) => void;
  /** Stage a column source change; null selects automatically. Saving persists the frontend layout. */
  changeResourceTableColumnSource: (columnId: string, source: string | null) => void;
  updateResourceTableColumnWidth: (columnId: string, width: number, commit: boolean) => void;
  toggleResourceTableExpand: (softwareId: string) => void;
  reorderResourceTableColumns: (sourceColumnId: string, targetColumnId: string) => void;
  selectResourceSegment: (selection: ResourcePrecisionSelection, hideCursor: boolean) => void;
  toggleResourceSegment: (selection: ResourcePrecisionSelection, hideCursor: boolean) => void;
  clearResourceSelection: () => void;
  cancelAllMonitorEdits: () => void;
}

export function createMonitorStore(options: MonitorStoreOptions): MonitorStore {
  const [dashboardSettingsState, setDashboardSettingsState] = createSignal<DashboardSettingsLoadState>("loading");
  const [dashboardSaveState, setDashboardSaveState] = createSignal<"idle" | "saving" | "error">("idle");
  const [resourceBarSaveState, setResourceBarSaveState] = createSignal<"idle" | "saving" | "error">("idle");
  const [resourceTableSaveState, setResourceTableSaveState] = createSignal<"idle" | "saving" | "error">("idle");
  const [editMode, setEditMode] = createSignal(false);
  const [catalog, setCatalog] = createSignal<MetricDefinition[]>([]);
  const [catalogObservation, setCatalogObservation] =
    createSignal<ObservationState>(loadingObservation());
  // Track the metrics answered by the displayed frame; an unrestricted query answers all.
  // Wait for cached or declared layout values rather than inventing placeholder defaults.
  const [cards, setCards] = createSignal<DashboardCardSettings[]>([]);
  const [resourceBars, setResourceBars] = createSignal<ResourceBarSettings[]>([]);
  const [resourceTableColumns, setResourceTableColumns] = createSignal<ResourceTableColumnSettings[]>([]);
  const [resourceTableProcessColumns, setResourceTableProcessColumns] = createSignal<ResourceTableColumnSettings[]>([]);
  const [resourceTableMode, setResourceTableMode] = createSignal<ResourceTableViewMode>("software");
  const [resourceTableEditMode, setResourceTableEditMode] = createSignal(false);
  const [resourceBarEditMode, setResourceBarEditMode] = createSignal(false);
  const [resourceTableSort, setResourceTableSort] = createSignal<ResourceTableSort>(defaultResourceTableSort);
  const [expandedResourceTableRows, setExpandedResourceTableRows] = createSignal<Record<string, boolean>>({});
  // One typed selection across all bars also owns the expanded details.
  // Deselecting closes details; selecting another segment replaces them.
  const [resourceSelection, setResourceSelection] = createSignal<ResourcePrecisionSelection | null>(null);
  const [metricModalTarget, setMetricModalTarget] = createSignal<MetricModalTarget | null>(null);
  const [selectedMetricId, setSelectedMetricId] = createSignal<string | null>(null);
  const metricCatalogLease = options.metricCatalogSource.acquire({
    active: true,
    refreshIntervalMs: null
  });
  const dashboardSettingsLease = options.dashboardSettingsSource.acquire({
    active: true,
    refreshIntervalMs: null
  });
  // The backend process field catalog is constant and read once.
  const processFieldCatalogLease = options.processFieldCatalogSource.acquire({
    active: true,
    refreshIntervalMs: null
  });
  const [processFields, setProcessFields] = createSignal<readonly ProcessMetricField[]>([]);
  const applyProcessFieldCatalog = (source: SourceSnapshot<readonly ProcessMetricField[]>) => {
    if (sourceCanRender(source) && source.data) setProcessFields(source.data);
  };
  const unsubscribeProcessFieldCatalog = processFieldCatalogLease.subscribe(applyProcessFieldCatalog);
  applyProcessFieldCatalog(processFieldCatalogLease.snapshot);
  const unsubscribePreferenceReset = subscribeFrontendPreferencesReset(() => { void refreshSettings(); });
  let dashboardRollbackSnapshot: DashboardCardSettings[] | null = null;
  let resourceBarRollbackSnapshot: ResourceBarSettings[] | null = null;
  let resourceTableRollbackSnapshot: {
    software: ResourceTableColumnSettings[];
    process: ResourceTableColumnSettings[];
  } | null = null;
  let dashboardEditGeneration = 0;
  let resourceBarEditGeneration = 0;
  let resourceTableEditGeneration = 0;
  let lastCatalogRevision = -1;
  let lastDashboardSettingsRevision = -1;
  // Reexpand factory layouts after catalog changes; preserve user-saved layouts.
  let dashboardUsesFactoryDefaults = false;
  // Catalog revisions are opaque. Snapshot changes trigger a catalog fetch; retry failed fetches on the next frame.
  let backendCatalogRevision: string | null = null;
  // After the first push, only this subscription updates the metric catalog; reset when it ends.
  let catalogFromPush = false;
  // Push catalogs carry their revision; GET catalogs have an unknown revision (null).
  let heldCatalogRevision: string | null = null;

  const activeCards = cards;
  const resourceBarEditing = createMemo(() => resourceBarEditMode());
  const activeResourceBars = resourceBars;
  const resourceTableEditing = createMemo(() => resourceTableEditMode());
  const activeResourceTableColumns = createMemo(() => {
    const processMode = resourceTableMode() === "process";
    return processMode ? resourceTableProcessColumns() : resourceTableColumns();
  });
  const visibleCards = createMemo(() => activeCards().filter(card => options.visibleDashboardCardIds().includes(card.id)));
  const currentMetricIds = createMemo(() => {
    const ids = new Set<string>();
    for (const card of options.metricSnapshotActive() ? visibleCards() : []) {
      // Device fields use field subscriptions independently of metric subscriptions.
      for (const slot of card.slots) {
        if (!fieldCardSlot(slot.metricId, slot.binding) && dashboardMetricAvailable(slot.metricId, slot.binding, catalog())) {
          ids.add(slot.metricId);
        }
      }
    }
    if (options.resourceTableActive() && resourceTableMode() === "performance") {
      for (const metricId of defaultPerformanceMetricIds(catalog())) {
        ids.add(metricId);
      }
    }

    return [...ids];
  });
  const snapshotMetricIds = currentMetricIds;
  // Software details use software IDs; residual details use their resource bar metric IDs.
  const resourceBreakdownDetailSelections = createMemo(() => [resourceSelection()]
    .filter((selection): selection is ResourcePrecisionSelection => selection !== null && selection.softwareId.length > 0));
  const resourceBreakdownProcessDetailSoftwareIds = createMemo(() => [...new Set(resourceBreakdownDetailSelections()
    .filter((selection) => !isReservedSegment(selection)).map((selection) => selection.softwareId))]);
  const resourceBreakdownResidualDetailMetricIds = createMemo(() => [...new Set(resourceBreakdownDetailSelections()
    .filter(isReservedSegment).map((selection) => selection.metricId))]);

  const unsubscribeMetricCatalog = metricCatalogLease.subscribe(
    applyMetricCatalogSourceSnapshot);
  const unsubscribeDashboardSettings = dashboardSettingsLease.subscribe(
    applyDashboardSettingsSourceSnapshot);
  applyMetricCatalogSourceSnapshot(metricCatalogLease.snapshot);
  applyDashboardSettingsSourceSnapshot(dashboardSettingsLease.snapshot);

  const layoutReady = () => dashboardSettingsState() === "ready";
  const metricSnapshotNeeded = createMemo(() => layoutReady() && (options.metricSnapshotActive()
    || (options.resourceTableActive() && resourceTableMode() === "performance")));
  const resourceTableNeeded = createMemo(() => layoutReady() && options.resourceTableActive()
    && resourceTableMode() !== "performance");
  const metricSubscriptionKey = createMemo(() => JSON.stringify({
    active: metricSnapshotNeeded() && snapshotMetricIds().length > 0,
    query: normalizeMetricSnapshotQuery(createMetricSnapshotQuery()),
    intervalMs: options.metricSnapshotIntervalMs()
  }));
  const breakdownBindings = createResourceBreakdownBindings({
    active: () => layoutReady() && options.resourceBarsActive() && requestedResourceBars().length > 0,
    intervalMs: options.resourceBarsIntervalMs,
    bars: requestedResourceBars,
    detailSoftwareIds: resourceBreakdownProcessDetailSoftwareIds,
    detailResidualMetricIds: resourceBreakdownResidualDetailMetricIds,
    barsSource: options.resourceMonitorSource,
    detailsSource: options.resourceProcessDetailsSource,
    // Selection follows the bar views shown for this frame, computed once in orderedSnapshotBars.
    onBars: () => pruneResourceSelection(orderedSnapshotBars())
  });
  const resourceTableSubscriptionKey = createMemo(() => JSON.stringify({
    active: resourceTableNeeded() && (createResourceTableQuery().sampleMetricIds?.length ?? 0) > 0,
    query: normalizeResourceMonitorQuery(createResourceTableQuery()),
    intervalMs: options.resourceTableIntervalMs()
  }));
  const metricSourceSnapshot = usePushSource(options.metricSnapshotSource,
    () => JSON.parse(metricSubscriptionKey()) as { active: boolean; query: MetricSnapshotQuery; intervalMs: number | null }).snapshot;
  const tableSourceSnapshot = usePushSource(options.resourceMonitorSource,
    () => JSON.parse(resourceTableSubscriptionKey()) as { active: boolean; query: ResourceMonitorQuery; intervalMs: number | null }).snapshot;
  const snapshot = createMemo(() => metricSourceSnapshot().data);
  const snapshotObservation = createMemo(() => snapshotMetricIds().length === 0 ? readyObservation() : pushObservation(metricSourceSnapshot()));
  const resourceBreakdownSnapshot = createMemo(() => {
    const value = breakdownBindings.bars()?.data;
    return value?.scope === "bars" ? value.breakdown : null;
  });
  const resourceProcessDetails = createMemo(() => breakdownBindings.details()?.data ?? null);
  const resourceProcessDetailsObservation = createMemo(() => pushObservation(breakdownBindings.details()));
  const resourceTableFacts = createMemo(() => {
    const value = tableSourceSnapshot().data;
    return value?.scope === "table" ? value.facts : null;
  });
  const resourceTableSnapshot = createResourceFactsProjection(resourceTableFacts,
    activeResourceTableColumns, resourceTableMode, resourceTableSort, currentLanguage);
  const resourceBarsObservation = createMemo(() => pushObservation(breakdownBindings.bars()));
  const resourceTableObservation = createMemo(() => pushObservation(tableSourceSnapshot()));
  const orderedSnapshotBars = createMemo(() => resourceBarViews(resourceBreakdownSnapshot()?.bars ?? [], activeResourceBars(), processFields()));
  // Equivalent catalogs, including reconnect replays, must not rebuild cards or selectors.
  const fieldCatalogSnapshot = usePushSource(options.metricFieldCatalogSource, () => ({
    active: options.metricSnapshotActive(), intervalMs: options.metricSnapshotIntervalMs()
  })).snapshot;
  const fieldCatalog = createMemo(() => fieldCatalogSnapshot()?.data?.fields ?? null, null, { equals: sameMetricFieldCatalog });
  const fieldSlots = createMemo(() => fieldCardSlots(visibleCards()));
  const fieldQuery = createMemo(() => ({
    fields: [...new Set(fieldSlots().map((slot) => slot.fieldName))].sort()
  }));
  const fieldDevicesSnapshot = usePushSource(options.metricFieldsSource, () => ({
    active: options.metricSnapshotActive() && fieldQuery().fields.length > 0,
    intervalMs: options.metricSnapshotIntervalMs(), query: fieldQuery()
  })).snapshot;
  const fieldDevices = createMemo(() => fieldDevicesSnapshot().data);
  const cardCatalog = createMemo(() => [...catalog(), ...fieldCardDefinitions(fieldCatalog() ?? [])]);
  // Display backend GPU names beside their unchanged numeric indices.
  const gpuNames = createMemo(() => gpuAdapterNames(catalog(), fieldCatalog()));
  // Existing cards retain all catalog metadata; source filtering affects only the selector.
  const pickerCatalog = createMemo(() => cardCatalog().filter(metricOfferedInPicker));
  const cardSnapshot = createMemo<MetricSnapshot | null>(() => {
    const items = fieldCardValues(fieldSlots(), fieldCatalog(), fieldDevices());
    const legacy = snapshot();
    if (Object.keys(items).length === 0) {
      return legacy;
    }
    // Cards retain the metric frame revision, or no revision before the first frame.
    return { version: 5, capturedAt: legacy?.capturedAt ?? null, catalogRevision: legacy?.catalogRevision ?? "", items: { ...legacy?.items, ...items } };
  });
  const fieldObservation = createMemo<ObservationState>(() => {
    if (fieldQuery().fields.length === 0) return readyObservation();
    const observation = pushObservation(fieldDevicesSnapshot() ?? null);
    return observation.status === "error"
      ? { ...observation, lastError: uiText.monitorPage.deviceFieldReadingsFailed } : observation;
  });
  // Missing values remain loading only until their source answers or fails.
  // Device slots appear after both values and catalog arrive, including explicit absence.
  // A failed source ends loading and reports a read failure.
  // Metric projections include explicit unavailability; a missing entry is unanswered.
  const cardReadingGaps = createMemo(() => {
    const items = cardSnapshot()?.items ?? {};
    const legacyFailed = snapshotObservation().status === "error";
    const fieldsFailed = pushObservation(fieldDevicesSnapshot() ?? null).status === "error";
    const pending = new Set<string>();
    const failed = new Set<string>();
    for (const card of activeCards()) {
      for (const { metricId, binding } of card.slots) {
        if (items[metricId]) continue;
        if (fieldCardSlot(metricId, binding)) {
          (fieldsFailed ? failed : pending).add(metricId);
        } else if (!legacyFailed) {
          pending.add(metricId);
        }
      }
    }
    return { pending: pending as ReadonlySet<string>, failed: failed as ReadonlySet<string> };
  });
  const cardPendingMetricIds = () => cardReadingGaps().pending;
  const cardFailedMetricIds = () => cardReadingGaps().failed;
  // Record trends only from the source carrying that metric to avoid duplicate timestamps.
  const trendMetricIds = createMemo(() => new Set(activeCards().flatMap((card) => card.slots
    .filter((slot) => slot.size === "large" || slot.size === "full").map((slot) => slot.metricId))));
  const [cardTrends, setCardTrends] = createSignal<CardTrends>(new Map());
  createEffect(on([snapshot, fieldDevices, trendMetricIds], ([legacy, devices, ids], previous) => {
    const [previousLegacy, previousDevices] = previous ?? [null, null];
    const fieldIds = new Set(untrack(fieldSlots).map((slot) => slot.metricId));
    const items = untrack(cardSnapshot)?.items ?? {};
    setCardTrends((current) => appendCardTrends(current, ids, (metricId) => fieldIds.has(metricId)
      ? devices !== null && devices !== previousDevices
      : legacy !== null && legacy !== previousLegacy, items));
  }));

  createEffect(on(metricSourceSnapshot, value => {
    if (value.data) {
      if (!catalogFromPush && value.data.catalogRevision !== backendCatalogRevision) {
        backendCatalogRevision = value.data.catalogRevision;
        void followBackendCatalog(value.data.catalogRevision);
      }
    }
  }));
  // Custom layouts remap GPU indices against the new catalog without rereading settings.
  createEffect(on(catalog, items => applyDashboardLayout({ version: 0, cards: cards(), resourceBars: resourceBars(),
    resourceTableColumns: resourceTableColumns(), resourceTableProcessColumns: resourceTableProcessColumns() }, items), { defer: true }));
  createEffect(on(fieldCatalogSnapshot, value => {
    catalogFromPush = false;
    if (options.metricSnapshotActive() && options.metricSnapshotIntervalMs() !== null && value.data) {
      applyCatalogPush(value.data);
    }
  }));
  onCleanup(() => {
    unsubscribeMetricCatalog();
    unsubscribeDashboardSettings();
    metricCatalogLease.release();
    dashboardSettingsLease.release();
    unsubscribeProcessFieldCatalog();
    processFieldCatalogLease.release();
    unsubscribePreferenceReset();
  });

  function createMetricSnapshotQuery(): MetricSnapshotQuery {
    return { ids: snapshotMetricIds() };
  }

  function requestedResourceBars(): NonNullable<ResourceBreakdownQuery["bars"]> {
    return activeResourceBars().filter(bar => dashboardMetricAvailable(bar.metricId, bar.binding, catalog())).map(resourceBarQuery);
  }
  function createResourceTableQuery(): ResourceMonitorQuery {
    return {
      scope: "table",
      bars: [],
      // Identity/attribution are explicit facts; hidden numeric columns have no demand.
      sampleMetricIds: tableSampleItems(activeResourceTableColumns(), catalog(), processFields()),
      processDetailLevel: activeResourceTableColumns().some(column => column.visible && ["user", "architecture"].includes(column.id)) ? "full" : "basic",
      visibleColumnIds: [],
      sortColumnId: "",
      sortDirection: "desc",
      processDetailSoftwareIds: [],
      residualDetailMetricIds: [],
      tableMode: "software"
    };
  }

  /** Apply revision and catalogs atomically. Compare known revisions, otherwise full content, to avoid equivalent rebuilds. Reload factory layouts on revision changes. */
  function applyCatalogPush(push: MetricCatalogPush): void {
    catalogFromPush = true;
    // Subscription callbacks may be synchronous; avoid tracking the catalog in their enclosing effect.
    untrack(() => {
      const unchanged = heldCatalogRevision === null
        ? JSON.stringify(catalog()) === JSON.stringify(push.metrics)
        : heldCatalogRevision === push.catalogRevision;
      if (!unchanged) setCatalog(push.metrics);
      heldCatalogRevision = push.catalogRevision;
      if (catalogObservation().status !== "ready") setCatalogObservation(readyObservation());
    });
    if (push.catalogRevision !== backendCatalogRevision) {
      backendCatalogRevision = push.catalogRevision;
      if (dashboardUsesFactoryDefaults) void refreshSettings();
    }
  }

  function applyMetricCatalogSourceSnapshot(
    source: SourceSnapshot<MetricDefinition[]>
  ): void {
    if (catalogFromPush) {
      return;
    }
    if (sourceCanRender(source) && source.data) {
      if (source.revision !== lastCatalogRevision) {
        const items = source.data;
        setCatalog(items);
        heldCatalogRevision = null;
        lastCatalogRevision = source.revision;
      }
      if (source.status === "stale") {
        setCatalogObservation((previous) => failedObservation(
          observationCanRender(previous)
            ? previous
            : readyObservation(source.capturedAt ?? undefined),
          userFacingErrorMessage(source.error, uiText.stores.metricCatalogRefreshFailed)));
      } else if (source.status === "refreshing") {
        setCatalogObservation(refreshingObservation);
      } else {
        setCatalogObservation(readyObservation(source.capturedAt ?? undefined));
      }
      return;
    }
    if (source.status === "error"
      || source.status === "unavailable"
      || source.status === "disposed") {
      setCatalogObservation((previous) => failedObservation(
        previous,
        userFacingErrorMessage(source.error, uiText.stores.metricCatalogRefreshFailed)));
      return;
    }
    setCatalogObservation(loadingObservation());
  }

  function applyDashboardSettingsSourceSnapshot(
    source: SourceSnapshot<DashboardSettingsResult>
  ): void {
    if (!sourceCanRender(source) || !source.data) {
      if (source.status === "error"
        || source.status === "unavailable"
        || source.status === "disposed") {
        setDashboardSettingsState("error");
      } else if (dashboardSettingsState() === "error") {
        setDashboardSettingsState("loading");
      }
      return;
    }

    const result = source.data;
    const settings = result.settings;
    dashboardUsesFactoryDefaults = settings.usesFactoryDefaults === true;
    if (source.revision !== lastDashboardSettingsRevision) {
      // Retain equivalent layouts to avoid rebuilding cards, visibility demands and subscriptions.

      applyDashboardLayout(settings, untrack(catalog));
      lastDashboardSettingsRevision = source.revision;
    }
    setDashboardSettingsState("ready");
  }

  /** Remap GPU indices by stable device identity against the active catalog. Preserve the layout before catalog load and leave the edited section untouched. */
  function applyDashboardLayout(settings: DashboardSettings, items: readonly MetricDefinition[]): void {
    const layout = items.length > 0 ? resolveDashboardBindings(settings, items) : settings;
    untrack(() => {
      if (!editMode()) {
        replaceIfChanged(cards, setCards, layout.cards);
      }
      if (!resourceBarEditing()) {
        replaceIfChanged(resourceBars, setResourceBars, layout.resourceBars);
      }
      if (!resourceTableEditing()) {
        replaceIfChanged(resourceTableColumns, setResourceTableColumns, layout.resourceTableColumns);
        replaceIfChanged(resourceTableProcessColumns, setResourceTableProcessColumns, layout.resourceTableProcessColumns);
      }
    });
  }

  async function refreshCatalog() {
    const source = await metricCatalogLease.refresh();
    return sourceCanRender(source);
  }

  async function followBackendCatalog(revision: string) {
    if (!await refreshCatalog()) {
      if (backendCatalogRevision === revision) {
        backendCatalogRevision = null;
      }
      return;
    }
    if (dashboardUsesFactoryDefaults) {
      await refreshSettings();
    }
  }

  async function refreshSettings() {
    const source = await dashboardSettingsLease.refresh();
    return sourceCanRender(source) && source.data !== null;
  }

  function addCard() {
    updateCards((items) => [...items, { id: crypto.randomUUID(), slots: [] }]);
  }

  function enterEditMode() {
    if (editMode()) {
      return;
    }
    setDashboardSaveState("idle");
    dashboardRollbackSnapshot = structuredClone(cards());
    dashboardEditGeneration += 1;
    setEditMode(true);
  }

  let dashboardSaveInFlight = false;

  async function persistDashboardSettings(
    settings: DashboardSettings,
    setSaveState: (state: "idle" | "saving" | "error") => void,
    isCurrent: () => boolean
  ): Promise<DashboardSettingsResult | null> {
    if (dashboardSaveInFlight) {
      if (isCurrent()) {
        setSaveState("error");
      }
      return null;
    }

    dashboardSaveInFlight = true;
    setSaveState("saving");
    try {
      const result = await saveDashboardSettings(settings);
      applyDashboardSettingsSourceSnapshot(
        dashboardSettingsLease.acceptAuthoritative(result));
      return result;
    } catch (error) {
      console.error("Failed to save dashboard settings.", error);
      if (isCurrent()) {
        setSaveState("error");
      }
      return null;
    } finally {
      dashboardSaveInFlight = false;
    }
  }

  async function saveDraftAndLeaveEditMode() {
    const generation = dashboardEditGeneration;
    const isCurrent = () => editMode() && dashboardEditGeneration === generation;
    const result = await persistDashboardSettings({
      version: dashboardSettingsVersion,
      cards: normalizeCards(cards()),
      resourceBars: normalizeResourceBars(resourceBars()),
      resourceTableColumns: normalizeResourceTableColumns(resourceTableColumns(), catalog(), "software"),
      resourceTableProcessColumns: normalizeResourceTableColumns(resourceTableProcessColumns(), catalog(), "process")
    }, setDashboardSaveState, isCurrent);
    if (!result || !isCurrent()) {
      return;
    }

    setCards(structuredClone(result.settings.cards));
    dashboardRollbackSnapshot = null;
    setEditMode(false);
    setDashboardSaveState("idle");
    closeMetricModal();
  }

  function cancelDashboardEdit() {
    if (!editMode()) {
      return;
    }
    dashboardEditGeneration += 1;
    if (dashboardRollbackSnapshot) {
      setCards(structuredClone(dashboardRollbackSnapshot));
    }
    dashboardRollbackSnapshot = null;
    setEditMode(false);
    setDashboardSaveState("idle");
    closeMetricModal();
  }

  function openMetricModal(target: OpenMetricModalTarget) {
    setMetricModalTarget({ cardId: target.cardId, index: target.index, size: target.size });
    setSelectedMetricId(target.currentMetricId);
    if (catalog().some((metric) => metric.requiredComponentId)) {
      options.onMetricDependenciesRequested();
    }
  }

  function closeMetricModal() {
    setMetricModalTarget(null);
    setSelectedMetricId(null);
  }

  function confirmMetricSelection() {
    const target = metricModalTarget();
    const metricId = selectedMetricId();
    if (!target || !metricId) {
      return;
    }
    const binding = metricBindingForDefinition(
      cardCatalog().find((metric) => metric.id === metricId));

    updateCards((items) => {
      const card = items.find((candidate) => candidate.id === target.cardId);
      return card && target.index < card.slots.length
        ? replaceCardSlotMetric(items, target, metricId, binding)
        : addCardSlot(items, target.cardId, { metricId, binding, size: target.size });
    });
    closeMetricModal();
  }

  function clearMetricSlot(cardId: string, index: number) {
    updateCards((items) => removeCardSlot(items, { cardId, index }));
  }

  function resizeMetricSlot(cardId: string, index: number, size: DashboardSlotSize) {
    updateCards((items) => resizeCardSlot(items, { cardId, index }, size));
  }

  function moveDashboardCard(sourceCardId: string, targetCardId: string) {
    if (sourceCardId === targetCardId) {
      return;
    }

    updateCards((items) => {
      const sourceIndex = items.findIndex((card) => card.id === sourceCardId);
      const targetIndex = items.findIndex((card) => card.id === targetCardId);
      if (sourceIndex < 0 || targetIndex < 0 || sourceIndex === targetIndex) {
        return items;
      }

      const next = [...items];
      const [moved] = next.splice(sourceIndex, 1);
      next.splice(targetIndex, 0, moved);
      return next;
    });
  }

  function moveDashboardMetricSlot(source: DashboardSlotRef, target: DashboardSlotRef) {
    updateCards((items) => moveCardSlot(items, source, target));
  }

  function toggleResourceBarEditMode() {
    if (resourceBarEditMode()) {
      void saveResourceBarDraftAndLeaveEditMode();
      return;
    }

    setResourceBarSaveState("idle");
    resourceBarRollbackSnapshot = structuredClone(resourceBars());
    resourceBarEditGeneration += 1;
    setResourceBarEditMode(true);
  }

  async function saveResourceBarDraftAndLeaveEditMode() {
    const generation = resourceBarEditGeneration;
    const isCurrent = () => resourceBarEditMode()
      && resourceBarEditGeneration === generation;
    const result = await persistDashboardSettings({
      version: dashboardSettingsVersion,
      cards: normalizeCards(cards()),
      resourceBars: normalizeResourceBars(resourceBars()),
      resourceTableColumns: normalizeResourceTableColumns(resourceTableColumns(), catalog(), "software"),
      resourceTableProcessColumns: normalizeResourceTableColumns(resourceTableProcessColumns(), catalog(), "process")
    }, setResourceBarSaveState, isCurrent);
    if (!result || !isCurrent()) {
      return;
    }

    setResourceBars(structuredClone(result.settings.resourceBars));
    resourceBarRollbackSnapshot = null;
    setResourceBarEditMode(false);
    setResourceBarSaveState("idle");
  }

  function cancelResourceBarEdit() {
    if (!resourceBarEditMode()) {
      return;
    }
    resourceBarEditGeneration += 1;
    if (resourceBarRollbackSnapshot) {
      setResourceBars(structuredClone(resourceBarRollbackSnapshot));
    }
    resourceBarRollbackSnapshot = null;
    setResourceBarEditMode(false);
    setResourceBarSaveState("idle");
  }

  function toggleResourceBar(metricId: string, enabled: boolean) {
    updateResourceBars((bars) => {
      if (enabled) {
        return bars.some((bar) => bar.metricId === metricId)
          ? bars
          : [...bars, {
            id: `resource-${metricId.replaceAll(".", "-")}`,
            metricId,
            scaleMode: normalizeResourceBarScaleMode(metricId),
            binding: metricBindingForDefinition(
              catalog().find((metric) => metric.id === metricId))
          }];
      }

      return bars.filter((bar) => bar.metricId !== metricId);
    });
  }

  function updateResourceBarScale(metricId: string, scaleMode: "capacity" | "active") {
    updateResourceBars((bars) => bars.map((bar) => bar.metricId === metricId
      ? { ...bar, scaleMode: normalizeResourceBarScaleMode(metricId, scaleMode) }
      : bar));
  }

  function changeResourceBarSource(metricId: string, source: string | null) {
    updateResourceBars((bars) => bars.map((bar) => bar.metricId === metricId ? { ...bar, source } : bar));
  }

  function reorderResourceBars(sourceMetricId: string, targetMetricId: string) {
    if (sourceMetricId === targetMetricId) {
      return;
    }

    updateResourceBars((bars) => moveItemBefore(bars, (bar) => bar.metricId, sourceMetricId, targetMetricId));
  }

  function changeResourceTableMode(mode: ResourceTableViewMode) {
    if (mode === "performance") {
      cancelResourceTableEdit();
    }
    setResourceTableMode(mode);
    if (mode !== "process" && isProcessDetailResourceTableColumn(resourceTableSort().columnId)) {
      setResourceTableSort(defaultResourceTableSort);
    }
    if (mode !== "software") {
      setExpandedResourceTableRows({});
    }
  }

  function toggleResourceTableEditMode() {
    if (resourceTableMode() === "performance") {
      return;
    }
    if (resourceTableEditMode()) {
      void saveResourceTableDraftAndLeaveEditMode();
      return;
    }

    setResourceTableSaveState("idle");
    resourceTableRollbackSnapshot = {
      software: structuredClone(resourceTableColumns()),
      process: structuredClone(resourceTableProcessColumns())
    };
    resourceTableEditGeneration += 1;
    setResourceTableEditMode(true);
  }

  async function saveResourceTableDraftAndLeaveEditMode() {
    const generation = resourceTableEditGeneration;
    const isCurrent = () => resourceTableEditMode()
      && resourceTableEditGeneration === generation;
    const result = await persistDashboardSettings({
      version: dashboardSettingsVersion,
      cards: normalizeCards(cards()),
      resourceBars: normalizeResourceBars(resourceBars()),
      resourceTableColumns: normalizeResourceTableColumns(resourceTableColumns(), catalog(), "software"),
      resourceTableProcessColumns: normalizeResourceTableColumns(resourceTableProcessColumns(), catalog(), "process")
    }, setResourceTableSaveState, isCurrent);
    if (!result || !isCurrent()) {
      return;
    }

    setResourceTableColumns(structuredClone(result.settings.resourceTableColumns));
    setResourceTableProcessColumns(structuredClone(result.settings.resourceTableProcessColumns));
    resourceTableRollbackSnapshot = null;
    setResourceTableEditMode(false);
    setResourceTableSaveState("idle");
  }

  function cancelResourceTableEdit() {
    if (!resourceTableEditMode()) {
      return;
    }
    resourceTableEditGeneration += 1;
    if (resourceTableRollbackSnapshot) {
      setResourceTableColumns(structuredClone(resourceTableRollbackSnapshot.software));
      setResourceTableProcessColumns(structuredClone(resourceTableRollbackSnapshot.process));
    }
    resourceTableRollbackSnapshot = null;
    setResourceTableEditMode(false);
    setResourceTableSaveState("idle");
  }

  function cycleResourceTableSort(columnId: string) {
    setResourceTableSort(nextResourceTableSort(resourceTableSort(), columnId));
  }

  function toggleResourceTableColumn(columnId: string, visible: boolean) {
    updateResourceTableColumns((columns) => {
      const normalized = normalizeResourceTableColumns(columns, catalog(), resourceTableMode());
      return normalized.map((column) => column.id === columnId ? { ...column, visible: column.id === "name" ? true : visible } : column);
    });
  }

  function addResourceTableColumn(columnId: string) {
    updateResourceTableColumns(columns => addTableColumn(columns, columnId, processFields(), resourceTableMode(), catalog()));
  }

  function removeResourceTableColumn(columnId: string) {
    updateResourceTableColumns(columns => removeTableColumn(columns, columnId));
    if (resourceTableSort().columnId === columnId) setResourceTableSort(defaultResourceTableSort);
  }

  function changeResourceTableColumnSource(columnId: string, source: string | null) {
    updateResourceTableColumns((columns) => normalizeResourceTableColumns(columns, catalog(), resourceTableMode())
      .map((column) => column.id === columnId ? { ...column, source } : column));
  }

  function updateResourceTableColumnWidth(columnId: string, width: number, commit: boolean) {
    const mode = resourceTableMode();
    const applyWidth = (columns: ResourceTableColumnSettings[]) => normalizeResourceTableColumns(columns, catalog(), mode)
      .map((column) => column.id === columnId ? { ...column, width: clampResourceTableColumnWidth(width) } : column);

    if (resourceTableEditing()) {
      if (mode === "process") {
        setResourceTableProcessColumns((current) => applyWidth(structuredClone(current)));
      } else {
        setResourceTableColumns((current) => applyWidth(structuredClone(current)));
      }
      return;
    }

    const nextColumns = applyWidth(mode === "process" ? resourceTableProcessColumns() : resourceTableColumns());
    if (mode === "process") {
      setResourceTableProcessColumns(nextColumns);
    } else {
      setResourceTableColumns(nextColumns);
    }
    if (commit) {
      void saveResourceTableColumns(nextColumns, mode);
    }
  }

  async function saveResourceTableColumns(columns: ResourceTableColumnSettings[], mode: ResourceTableViewMode) {
    const softwareColumns = mode === "process"
      ? normalizeResourceTableColumns(resourceTableColumns(), catalog(), "software")
      : normalizeResourceTableColumns(columns, catalog(), "software");
    const processColumns = mode === "process"
      ? normalizeResourceTableColumns(columns, catalog(), "process")
      : normalizeResourceTableColumns(resourceTableProcessColumns(), catalog(), "process");

    const result = await persistDashboardSettings({
      version: dashboardSettingsVersion,
      cards: normalizeCards(cards()),
      resourceBars: normalizeResourceBars(resourceBars()),
      resourceTableColumns: softwareColumns,
      resourceTableProcessColumns: processColumns
    }, setResourceTableSaveState, () => !resourceTableEditMode());
    if (!result) {
      return;
    }

    setResourceTableColumns(structuredClone(result.settings.resourceTableColumns));
    setResourceTableProcessColumns(structuredClone(result.settings.resourceTableProcessColumns));
  }

  function toggleResourceTableExpand(softwareId: string) {
    setExpandedResourceTableRows((current) => ({ ...current, [softwareId]: !current[softwareId] }));
  }

  function reorderResourceTableColumns(sourceColumnId: string, targetColumnId: string) {
    if (sourceColumnId === targetColumnId) {
      return;
    }

    updateResourceTableColumns((columns) => moveItemBefore(columns, (column) => column.id, sourceColumnId, targetColumnId));
  }

  // Keyboard and wheel navigation select without toggling.
  function selectResourceSegment(selection: ResourcePrecisionSelection, hideCursor: boolean) {
    setResourceSelection(selection);
    hidePrecisionCursor(hideCursor);
  }

  // Clicking or activating the selected segment closes its details.
  function toggleResourceSegment(selection: ResourcePrecisionSelection, hideCursor: boolean) {
    setResourceSelection((current) => toggledResourceSelection(current, selection));
    hidePrecisionCursor(hideCursor);
  }

  function hidePrecisionCursor(hideCursor: boolean) {
    if (hideCursor) {
      document.body.classList.add("resource-precision-cursor-hidden");
      window.setTimeout(() => document.body.classList.remove("resource-precision-cursor-hidden"), 900);
    }
  }

  function clearResourceSelection() {
    setResourceSelection(null);
    document.body.classList.remove("resource-precision-cursor-hidden");
  }

  function pruneResourceSelection(bars: ResourceBreakdownBar[]) {
    setResourceSelection((current) => reconcileResourceSelection(bars, current));
  }

  function updateCards(updater: (cards: DashboardCardSettings[]) => DashboardCardSettings[]) {
    if (dashboardSaveState() === "saving") {
      return;
    }
    setCards((current) => updater(structuredClone(current)));
  }

  function updateResourceBars(updater: (bars: ResourceBarSettings[]) => ResourceBarSettings[]) {
    if (resourceBarSaveState() === "saving") {
      return;
    }
    setResourceBars((current) => updater(structuredClone(current)));
  }

  function updateResourceTableColumns(updater: (columns: ResourceTableColumnSettings[]) => ResourceTableColumnSettings[]) {
    if (resourceTableSaveState() === "saving") {
      return;
    }
    const processMode = resourceTableMode() === "process";
    if (processMode) {
      setResourceTableProcessColumns((current) => updater(structuredClone(current)));
    } else {
      setResourceTableColumns((current) => updater(structuredClone(current)));
    }
  }

  function cancelAllMonitorEdits() {
    cancelDashboardEdit();
    cancelResourceBarEdit();
    cancelResourceTableEdit();
  }

  return {
    dashboardSettingsState,
    dashboardSaveState,
    resourceBarSaveState,
    resourceTableSaveState,
    editMode,
    catalog,
    catalogObservation,
    snapshot,
    snapshotObservation,
    cardCatalog,
    pickerCatalog,
    cardSnapshot,
    cardPendingMetricIds,
    cardFailedMetricIds,
    fieldObservation,
    cardTrends,
    activeCards,
    resourceBarEditing,
    activeResourceBars,
    resourceTableEditing,
    resourceTableMode,
    activeResourceTableColumns,
    processFields,
    resourceBreakdownSnapshot,
    resourceProcessDetails,
    resourceProcessDetailsObservation,
    resourceBarsObservation,
    resourceTableSnapshot,
    resourceTableObservation,
    resourceTableSort,
    gpuNames,
    expandedResourceTableRows,
    resourceSelection,
    metricModalTarget,
    selectedMetricId,
    setSelectedMetricId,
    orderedSnapshotBars,
    refreshCatalog,
    refreshSettings,
    addCard,
    enterEditMode,
    saveDraftAndLeaveEditMode,
    cancelDashboardEdit,
    openMetricModal,
    closeMetricModal,
    confirmMetricSelection,
    clearMetricSlot,
    resizeMetricSlot,
    moveDashboardCard,
    moveDashboardMetricSlot,
    toggleResourceBarEditMode,
    cancelResourceBarEdit,
    toggleResourceBar,
    updateResourceBarScale,
    changeResourceBarSource,
    reorderResourceBars,
    changeResourceTableMode,
    toggleResourceTableEditMode,
    cancelResourceTableEdit,
    cycleResourceTableSort,
    toggleResourceTableColumn,
    addResourceTableColumn,
    removeResourceTableColumn,
    changeResourceTableColumnSource,
    updateResourceTableColumnWidth,
    toggleResourceTableExpand,
    reorderResourceTableColumns,
    selectResourceSegment,
    toggleResourceSegment,
    clearResourceSelection,
    cancelAllMonitorEdits
  };
}

/** Retain equal JSON layouts to keep cards, visibility demands and subscriptions stable. */
function replaceIfChanged<T>(current: Accessor<T>, set: Setter<T>, next: T): void {
  if (JSON.stringify(current()) !== JSON.stringify(next)) set(() => structuredClone(next));
}

/** Compare the complete JSON catalog so newly added fields participate automatically. */
function sameMetricFieldCatalog(
  left: readonly MetricFieldCatalogDevice[] | null,
  right: readonly MetricFieldCatalogDevice[] | null): boolean {
  if (left === right) {
    return true;
  }
  if (!left || !right || left.length !== right.length) {
    return false;
  }
  return JSON.stringify(left) === JSON.stringify(right);
}

function moveItemBefore<T>(items: T[], key: (item: T) => string, sourceKey: string, targetKey: string) {
  const sourceIndex = items.findIndex((item) => key(item) === sourceKey);
  const targetIndex = items.findIndex((item) => key(item) === targetKey);
  if (sourceIndex < 0 || targetIndex < 0 || sourceIndex === targetIndex) {
    return items;
  }

  const next = [...items];
  const [moved] = next.splice(sourceIndex, 1);
  next.splice(targetIndex, 0, moved);
  return next;
}

function pushObservation<T>(snapshot: SourceSnapshot<T> | null): ObservationState {
  if (snapshot?.status === "ready") {
    return readyObservation(snapshot.capturedAt ?? undefined);
  }
  if (snapshot && (snapshot.status === "error" || snapshot.status === "unavailable" || snapshot.status === "disposed")) {
    return { status: "error", source: "none" };
  }
  return loadingObservation();
}
