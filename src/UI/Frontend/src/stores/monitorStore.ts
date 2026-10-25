import { createEffect, createMemo, createSignal, onCleanup } from "solid-js";
import type { Accessor } from "solid-js";
import { saveDashboardSettings } from "../api";
import type { DashboardSlotRef } from "../features/monitor/Dashboard";
import { defaultPerformanceMetricIds } from "../features/resourceTable/ResourcePerformancePanel";
import { createResourceFactsProjection, resourceTableMetricId } from "../features/resourceTable/resourceFacts.ts";
import type { ResourcePrecisionSelection } from "../features/resourceBreakdown/ResourceBreakdown";
import { reconcileResourceSelection } from "../features/resourceBreakdown/resourceSelection";
import type {
  DashboardCardSettings,
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
import { textOrEmpty } from "../utils";
import {
  failedObservation,
  loadingObservation,
  observationCanRender,
  readyObservation,
  refreshingObservation,
  type ObservationState
} from "../observation/observationState";
import { userFacingErrorMessage } from "../presentation/userFacingText";
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
import {
  fieldCardDefinitions,
  fieldCardSlot,
  fieldCardSlots,
  fieldCardValues
} from "../features/monitor/fieldCards.ts";
import {
  allCatalogMetricIds,
  clampResourceTableColumnWidth,
  dashboardSettingsVersion,
  dashboardSmallMetricLimit,
  defaultResourceTableColumns,
  isProcessDetailResourceTableColumn,
  normalizeCards,
  normalizeResourceBars,
  normalizeResourceTableColumns,
  metricBindingForDefinition,
  readDashboardSlot,
  readDashboardSlotBinding,
  writeDashboardSlot
} from "./monitorConfig";
import { uiText } from "../text.ts";
import { createResourceBreakdownBindings } from "./resourceBreakdownBindings.ts";

export interface MetricModalTarget {
  cardId: string;
  slot: "main" | "small";
  index: number;
}

export interface OpenMetricModalTarget extends MetricModalTarget {
  currentMetricId: string | null;
}

export interface MonitorStoreOptions {
  smartMonitoringEnabled: Accessor<boolean>;
  onMetricDependenciesRequested: () => void;
  metricCatalogSource: SourceHandle<MetricDefinition[]>;
  dashboardSettingsSource: SourceHandle<DashboardSettingsResult>;
  metricSnapshotSource: PushValueSourceFamily<MetricSnapshotQuery, MetricSnapshot>;
  /** 规范字段的设备目录：卡片选择器的设备字段条目、卡片上的设备名和「设备不在」都从这里来。 */
  metricFieldCatalogSource: PushValueSource<readonly MetricFieldCatalogDevice[]>;
  /** 卡片上设备字段的当前值，按字段集合订阅。 */
  metricFieldsSource: PushValueSourceFamily<MetricFieldQuery, readonly MetricFieldDevice[]>;
  resourceMonitorSource: PushValueSourceFamily<
    ResourceMonitorQuery,
    ResourceMonitorSnapshot
  >;
  resourceProcessDetailsSource: PushValueSourceFamily<ResourceBreakdownQuery, ResourceProcessDetailsSnapshot>;
  metricSnapshotActive: Accessor<boolean>;
  resourceBarsActive: Accessor<boolean>;
  resourceTableActive: Accessor<boolean>;
  metricSnapshotIntervalMs: Accessor<number | null>;
  resourceBarsIntervalMs: Accessor<number | null>;
  resourceTableIntervalMs: Accessor<number | null>;
}

export type DashboardSettingsLoadState = "loading" | "ready" | "recovered" | "error";

export interface MonitorStore {
  dashboardSettingsState: Accessor<DashboardSettingsLoadState>;
  dashboardSettingsSource: Accessor<DashboardSettingsResult["source"] | null>;
  dashboardSaveState: Accessor<"idle" | "saving" | "error">;
  resourceBarSaveState: Accessor<"idle" | "saving" | "error">;
  resourceTableSaveState: Accessor<"idle" | "saving" | "error">;
  editMode: Accessor<boolean>;
  catalog: Accessor<MetricDefinition[]>;
  catalogObservation: Accessor<ObservationState>;
  snapshot: Accessor<MetricSnapshot | null>;
  snapshotObservation: Accessor<ObservationState>;
  /** 卡片可选的全部指标：原有指标目录加上每台设备的规范字段。 */
  cardCatalog: Accessor<MetricDefinition[]>;
  /** 选择器里列出的指标：cardCatalog 去掉当前读数来源设置下不列出的设备字段来源。 */
  pickerCatalog: Accessor<MetricDefinition[]>;
  /** 卡片显示用的当前值：原有指标快照加上卡片上设备字段的值。 */
  cardSnapshot: Accessor<MetricSnapshot | null>;
  activeCards: Accessor<DashboardCardSettings[]>;
  resourceBarEditing: Accessor<boolean>;
  activeResourceBars: Accessor<ResourceBarSettings[]>;
  resourceTableEditing: Accessor<boolean>;
  resourceTableMode: Accessor<ResourceTableViewMode>;
  activeResourceTableColumns: Accessor<ResourceTableColumnSettings[]>;
  resourceBreakdownSnapshot: Accessor<ResourceBreakdownSnapshot | null>;
  resourceProcessDetails: Accessor<ResourceProcessDetailsSnapshot | null>;
  resourceProcessDetailsObservation: Accessor<ObservationState>;
  resourceBarsObservation: Accessor<ObservationState>;
  resourceTableSnapshot: Accessor<ResourceTableSnapshot | null>;
  resourceTableObservation: Accessor<ObservationState>;
  resourceTableSort: Accessor<{ columnId: string; direction: "asc" | "desc" }>;
  expandedResourceTableRows: Accessor<Record<string, boolean>>;
  resourceSelection: Accessor<ResourcePrecisionSelection | null>;
  expandedResourceSegments: Accessor<Record<string, string>>;
  metricModalTarget: Accessor<MetricModalTarget | null>;
  selectedMetricId: Accessor<string | null>;
  setSelectedMetricId: (metricId: string | null) => void;
  orderedSnapshotBars: Accessor<ResourceBreakdownBar[]>;
  refreshCatalog: () => Promise<boolean>;
  refreshSettings: () => Promise<boolean>;
  addCard: () => void;
  enterEditMode: () => void;
  saveDraftAndLeaveEditMode: () => Promise<void>;
  cancelDashboardEdit: () => void;
  openMetricModal: (target: OpenMetricModalTarget) => void;
  closeMetricModal: () => void;
  confirmMetricSelection: () => void;
  clearMetricSlot: (cardId: string, slot: "main" | "small", index: number) => void;
  moveDashboardCard: (sourceCardId: string, targetCardId: string) => void;
  moveDashboardMetricSlot: (source: DashboardSlotRef, target: DashboardSlotRef) => void;
  toggleResourceBarEditMode: () => void;
  cancelResourceBarEdit: () => void;
  toggleResourceBar: (metricId: string, enabled: boolean) => void;
  updateResourceBarScale: (metricId: string, scaleMode: "capacity" | "active") => void;
  reorderResourceBars: (sourceMetricId: string, targetMetricId: string) => void;
  changeResourceTableMode: (mode: ResourceTableViewMode) => void;
  toggleResourceTableEditMode: () => void;
  cancelResourceTableEdit: () => void;
  updateResourceTableSort: (columnId: string, direction: "asc" | "desc") => void;
  toggleResourceTableColumn: (columnId: string, visible: boolean) => void;
  updateResourceTableColumnWidth: (columnId: string, width: number, commit: boolean) => void;
  toggleResourceTableExpand: (softwareId: string) => void;
  reorderResourceTableColumns: (sourceColumnId: string, targetColumnId: string) => void;
  selectResourceSegment: (selection: ResourcePrecisionSelection, hideCursor: boolean) => void;
  clearResourceSelection: () => void;
  cancelAllMonitorEdits: () => void;
}

interface MonitorSourceBinding<T> {
  readonly queryKey: string;
  readonly source: SourceSnapshot<T>;
}

export function createMonitorStore(options: MonitorStoreOptions): MonitorStore {
  const [dashboardSettingsState, setDashboardSettingsState] = createSignal<DashboardSettingsLoadState>("loading");
  const [dashboardSettingsSource, setDashboardSettingsSource] = createSignal<DashboardSettingsResult["source"] | null>(null);
  const [dashboardSaveState, setDashboardSaveState] = createSignal<"idle" | "saving" | "error">("idle");
  const [resourceBarSaveState, setResourceBarSaveState] = createSignal<"idle" | "saving" | "error">("idle");
  const [resourceTableSaveState, setResourceTableSaveState] = createSignal<"idle" | "saving" | "error">("idle");
  const [editMode, setEditMode] = createSignal(false);
  const [catalog, setCatalog] = createSignal<MetricDefinition[]>([]);
  const [catalogObservation, setCatalogObservation] =
    createSignal<ObservationState>(loadingObservation());
  const [metricSourceSnapshot, setMetricSourceSnapshot] = createSignal<MonitorSourceBinding<MetricSnapshot> | null>(null);
  // 看板布局由后端按本机目录生成，设置到达前不画卡片，免得先闪一版和后端不一样的占位卡。
  const [cards, setCards] = createSignal<DashboardCardSettings[]>([]);
  const [resourceBars, setResourceBars] = createSignal<ResourceBarSettings[]>([]);
  const [resourceTableColumns, setResourceTableColumns] = createSignal<ResourceTableColumnSettings[]>(defaultResourceTableColumns([], "software"));
  const [resourceTableProcessColumns, setResourceTableProcessColumns] = createSignal<ResourceTableColumnSettings[]>(defaultResourceTableColumns([], "process"));
  const [resourceTableMode, setResourceTableMode] = createSignal<ResourceTableViewMode>("software");
  const [resourceTableEditMode, setResourceTableEditMode] = createSignal(false);
  const [resourceBarEditMode, setResourceBarEditMode] = createSignal(false);
  const [tableSourceSnapshot, setTableSourceSnapshot] = createSignal<MonitorSourceBinding<ResourceMonitorSnapshot> | null>(null);
  const [resourceTableSort, setResourceTableSort] = createSignal<{ columnId: string; direction: "asc" | "desc" }>({ columnId: "impact", direction: "desc" });
  const [expandedResourceTableRows, setExpandedResourceTableRows] = createSignal<Record<string, boolean>>({});
  const [resourceSelection, setResourceSelection] = createSignal<ResourcePrecisionSelection | null>(null);
  const [expandedResourceSegments, setExpandedResourceSegments] = createSignal<Record<string, string>>({});
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
  // 出厂布局随目录重新展开（见 release_defaults_contract.md），目录换新时要重取看板设置；用户保存过的布局不用。
  let dashboardUsesFactoryDefaults = false;
  // 快照帧带来的后端目录修订号。第一次收到和以后每次变化都重取目录；重取失败就清掉，下一帧再试。
  let backendCatalogRevision: string | null = null;

  const activeCards = cards;
  const resourceBarEditing = createMemo(() => resourceBarEditMode());
  const activeResourceBars = resourceBars;
  const resourceTableEditing = createMemo(() => resourceTableEditMode());
  const activeResourceTableColumns = createMemo(() => {
    const processMode = resourceTableMode() === "process";
    return processMode ? resourceTableProcessColumns() : resourceTableColumns();
  });
  const currentMetricIds = createMemo(() => {
    const ids = new Set<string>();
    for (const card of activeCards()) {
      // 设备字段走按字段订阅，不进原有指标订阅。
      if (card.main && !fieldCardSlot(card.main, card.mainBinding)) {
        ids.add(card.main);
      }
      (card.small ?? []).forEach((metricId, index) => {
        if (metricId && !fieldCardSlot(metricId, card.smallBindings?.[index])) {
          ids.add(metricId);
        }
      });
    }
    if (resourceTableMode() === "performance") {
      for (const metricId of defaultPerformanceMetricIds(catalog())) {
        ids.add(metricId);
      }
    }

    return [...ids];
  });
  const snapshotMetricIds = createMemo(() => {
    if (options.smartMonitoringEnabled()) {
      return currentMetricIds();
    }
    const catalogIds = allCatalogMetricIds(catalog());
    return catalogIds.length > 0 ? catalogIds : currentMetricIds();
  });
  const resourceBreakdownProcessDetailSoftwareIds = createMemo(() => {
    const ids = new Set(
      Object.values(expandedResourceSegments())
        .filter((softwareId) => softwareId.length > 0));
    const selectedSoftwareId = resourceSelection()?.softwareId;
    if (selectedSoftwareId) {
      ids.add(selectedSoftwareId);
    }

    return [...ids];
  });

  const unsubscribeMetricCatalog = metricCatalogLease.subscribe(
    applyMetricCatalogSourceSnapshot);
  const unsubscribeDashboardSettings = dashboardSettingsLease.subscribe(
    applyDashboardSettingsSourceSnapshot);
  applyMetricCatalogSourceSnapshot(metricCatalogLease.snapshot);
  applyDashboardSettingsSourceSnapshot(dashboardSettingsLease.snapshot);

  const metricSnapshotNeeded = createMemo(() => options.metricSnapshotActive()
    || (options.resourceTableActive() && resourceTableMode() === "performance"));
  const resourceTableNeeded = createMemo(() => options.resourceTableActive()
    && resourceTableMode() !== "performance");
  const metricSubscriptionKey = createMemo(() => JSON.stringify({
    active: metricSnapshotNeeded(),
    query: normalizeMetricSnapshotQuery(createMetricSnapshotQuery()),
    intervalMs: options.metricSnapshotIntervalMs()
  }));
  const breakdownBindings = createResourceBreakdownBindings({
    active: options.resourceBarsActive,
    intervalMs: options.resourceBarsIntervalMs,
    bars: requestedResourceBars,
    detailSoftwareIds: resourceBreakdownProcessDetailSoftwareIds,
    barsSource: options.resourceMonitorSource,
    detailsSource: options.resourceProcessDetailsSource,
    onBars: value => pruneResourceSelection(value.breakdown.bars ?? [])
  });
  const resourceTableSubscriptionKey = createMemo(() => JSON.stringify({
    active: resourceTableNeeded(),
    query: normalizeResourceMonitorQuery(createResourceTableQuery()),
    intervalMs: options.resourceTableIntervalMs()
  }));
  const snapshot = createMemo(() => metricSourceSnapshot()?.queryKey === JSON.stringify(normalizeMetricSnapshotQuery(createMetricSnapshotQuery()))
    ? metricSourceSnapshot()?.source.data ?? null : null);
  const snapshotObservation = createMemo(() => metricSourceSnapshot()?.queryKey === JSON.stringify(normalizeMetricSnapshotQuery(createMetricSnapshotQuery()))
    ? pushObservation(metricSourceSnapshot()?.source ?? null) : loadingObservation());
  const resourceBreakdownSnapshot = createMemo(() => breakdownBindings.bars()?.data?.breakdown ?? null);
  const resourceProcessDetails = createMemo(() => breakdownBindings.details()?.data ?? null);
  const resourceProcessDetailsObservation = createMemo(() => pushObservation(breakdownBindings.details()));
  const resourceTableQueryKey = createMemo(() => JSON.stringify(normalizeResourceMonitorQuery(createResourceTableQuery())));
  const resourceTableFacts = createMemo(() => tableSourceSnapshot()?.queryKey === resourceTableQueryKey()
    ? tableSourceSnapshot()?.source.data?.facts ?? null : null);
  const resourceTableSnapshot = createResourceFactsProjection(resourceTableFacts,
    activeResourceTableColumns, resourceTableMode, resourceTableSort);
  const resourceBarsObservation = createMemo(() => pushObservation(breakdownBindings.bars()));
  const resourceTableObservation = createMemo(() => tableSourceSnapshot()?.queryKey === JSON.stringify(normalizeResourceMonitorQuery(createResourceTableQuery()))
    ? pushObservation(tableSourceSnapshot()?.source ?? null) : loadingObservation());
  const orderedSnapshotBars = createMemo(() => {
    const bars = resourceBreakdownSnapshot()?.bars ?? [];
    const order = new Map(activeResourceBars().map((bar, index) => [bar.metricId, index]));
    return bars.filter((bar) => order.has(bar.metricId)).sort((left, right) =>
      (order.get(left.metricId) ?? Number.MAX_SAFE_INTEGER) - (order.get(right.metricId) ?? Number.MAX_SAFE_INTEGER));
  });
  // 目录每 5 秒推一次，内容通常不变；内容相同就不算变化，卡片目录和指标选择器不跟着重建。
  const [fieldCatalogSnapshot, setFieldCatalogSnapshot] = createSignal<SourceSnapshot<readonly MetricFieldCatalogDevice[]> | null>(null);
  const [fieldDevicesSnapshot, setFieldDevicesSnapshot] = createSignal<MonitorSourceBinding<readonly MetricFieldDevice[]> | null>(null);
  const fieldCatalog = createMemo(() => fieldCatalogSnapshot()?.data ?? null, null, { equals: sameMetricFieldCatalog });
  const fieldSlots = createMemo(() => fieldCardSlots(activeCards()));
  const fieldQuery = createMemo(() => ({
    fields: [...new Set(fieldSlots().map((slot) => slot.fieldName))].sort()
  }));
  const fieldDevices = createMemo(() => fieldDevicesSnapshot()?.queryKey === JSON.stringify(fieldQuery())
    ? fieldDevicesSnapshot()?.source.data ?? null : null);
  const fieldSubscriptionKey = createMemo(() => JSON.stringify({
    active: options.metricSnapshotActive(),
    fields: fieldQuery().fields,
    intervalMs: options.metricSnapshotIntervalMs()
  }));
  const cardCatalog = createMemo(() => [...catalog(), ...fieldCardDefinitions(fieldCatalog() ?? [])]);
  // 卡片上已经放了的字段照常从 cardCatalog 取设备名和标记；只有选择器按设置少列。
  const pickerCatalog = createMemo(() => cardCatalog().filter((metric) => metric.readingSource?.listed !== false));
  const cardSnapshot = createMemo<MetricSnapshot | null>(() => {
    const items = fieldCardValues(fieldSlots(), fieldCatalog(), fieldDevices());
    const legacy = snapshot();
    if (Object.keys(items).length === 0) {
      return legacy;
    }
    // 卡片显示不读目录修订号，沿用指标帧的；还没有指标帧时为空。
    return { version: 4, capturedAt: legacy?.capturedAt ?? null, catalogRevision: legacy?.catalogRevision ?? "", items: { ...legacy?.items, ...items } };
  });

  createEffect(() => {
    const binding = JSON.parse(metricSubscriptionKey()) as {
      active: boolean;
      query: MetricSnapshotQuery;
      intervalMs: number | null;
    };
    const queryKey = JSON.stringify(binding.query);
    if (!binding.active || binding.intervalMs === null) {
      return;
    }
    let accepting = true;
    const unsubscribe = options.metricSnapshotSource.subscribeSnapshot(
      binding.query,
      binding.intervalMs,
      (value) => {
        if (accepting) {
          setMetricSourceSnapshot({ queryKey, source: value });
          if (value.data && value.data.catalogRevision !== backendCatalogRevision) {
            backendCatalogRevision = value.data.catalogRevision;
            void followBackendCatalog(value.data.catalogRevision);
          }
        }
      });
    onCleanup(() => {
      accepting = false;
      unsubscribe();
    });
  });
  // 字段目录和监视读数共用后端发布的间隔。
  createEffect(() => {
    const intervalMs = options.metricSnapshotIntervalMs();
    if (!options.metricSnapshotActive() || intervalMs === null) {
      return;
    }
    const unsubscribe = options.metricFieldCatalogSource.subscribeSnapshot(intervalMs, setFieldCatalogSnapshot);
    onCleanup(unsubscribe);
  });
  createEffect(() => {
    const binding = JSON.parse(fieldSubscriptionKey()) as {
      active: boolean;
      fields: string[];
      intervalMs: number | null;
    };
    if (!binding.active || binding.intervalMs === null || binding.fields.length === 0) {
      return;
    }
    let accepting = true;
    const unsubscribe = options.metricFieldsSource.subscribeSnapshot(
      { fields: binding.fields },
      binding.intervalMs,
      (value) => {
        if (accepting) {
          setFieldDevicesSnapshot({ queryKey: JSON.stringify({ fields: binding.fields }), source: value });
        }
      });
    onCleanup(() => {
      accepting = false;
      unsubscribe();
    });
  });
  createEffect(() => {
    const binding = JSON.parse(resourceTableSubscriptionKey()) as {
      active: boolean;
      query: ResourceMonitorQuery;
      intervalMs: number | null;
    };
    const queryKey = JSON.stringify(binding.query);
    if (!binding.active || binding.intervalMs === null) {
      return;
    }
    let accepting = true;
    const unsubscribe = options.resourceMonitorSource.subscribeSnapshot(
      binding.query,
      binding.intervalMs,
      (value) => {
        if (accepting) {
          setTableSourceSnapshot({ queryKey, source: value });
        }
      });
    onCleanup(() => {
      accepting = false;
      unsubscribe();
    });
  });
  onCleanup(() => {
    unsubscribeMetricCatalog();
    unsubscribeDashboardSettings();
    metricCatalogLease.release();
    dashboardSettingsLease.release();
  });

  function createMetricSnapshotQuery(): MetricSnapshotQuery {
    const useCompiledPlan = (
      options.smartMonitoringEnabled()
      && !editMode()
      && resourceTableMode() !== "performance"
    );
    if (useCompiledPlan) {
      return { ids: null };
    }
    const ids = snapshotMetricIds();
    return { ids: ids.length === 0 ? null : ids };
  }

  function requestedResourceBars(): ResourceBreakdownQuery["bars"] {
    const useCompiledBars = options.smartMonitoringEnabled()
      && !resourceBarEditing();
    return useCompiledBars ? null : activeResourceBars().map((bar) => ({
      metricId: bar.metricId,
      scaleMode: bar.scaleMode
    }));
  }

  function createResourceTableQuery(): ResourceMonitorQuery {
    return {
      scope: "table",
      bars: [],
      // Configured facts keep identities and impact stable when a column is hidden.
      sampleMetricIds: [...new Set(normalizeResourceTableColumns(activeResourceTableColumns(), catalog(), resourceTableMode())
        .map(column => resourceTableMetricId(column.id)).filter((id): id is string => id !== null))],
      visibleColumnIds: [],
      sortColumnId: "",
      sortDirection: "desc",
      processDetailSoftwareIds: [],
      tableMode: "software"
    };
  }

  function applyMetricCatalogSourceSnapshot(
    source: SourceSnapshot<MetricDefinition[]>
  ): void {
    if (sourceCanRender(source) && source.data) {
      if (source.revision !== lastCatalogRevision) {
        const items = source.data;
        setCatalog(items);
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
      if (!editMode()) {
        setCards(structuredClone(settings.cards));
      }
      if (!resourceBarEditing()) {
        setResourceBars(structuredClone(settings.resourceBars));
      }
      if (!resourceTableEditing()) {
        setResourceTableColumns(structuredClone(settings.resourceTableColumns));
        setResourceTableProcessColumns(structuredClone(settings.resourceTableProcessColumns));
      }
      setDashboardSettingsSource(result.source);
      lastDashboardSettingsRevision = source.revision;
    }
    setDashboardSettingsState(
      result.source.recoveryDisposition === "recoveredLastKnownGood"
        || result.source.recoveryDisposition === "recoveredDefaultsAfterCorruption"
        ? "recovered"
        : "ready");
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
    updateCards((items) => [...items, {
      id: crypto.randomUUID(),
      main: null,
      small: [],
      mainBinding: null,
      smallBindings: []
    }]);
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
    setMetricModalTarget({ cardId: target.cardId, slot: target.slot, index: target.index });
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

    updateCards((items) => items.map((card) => {
      if (card.id !== target.cardId) {
        return card;
      }

      if (target.slot === "main") {
        return {
          ...card,
          main: metricId,
          mainBinding: binding
        };
      }

      const small = [...(card.small ?? [])];
      const smallBindings = [...(card.smallBindings ?? [])];
      small[target.index] = metricId;
      smallBindings[target.index] = binding;
      return normalizeCards([{
        ...card,
        small,
        smallBindings: smallBindings.slice(0, dashboardSmallMetricLimit)
      }])[0];
    }));
    closeMetricModal();
  }

  function clearMetricSlot(cardId: string, slot: "main" | "small", index: number) {
    updateCards((items) => items.map((card) => {
      if (card.id !== cardId) {
        return card;
      }

      if (slot === "main") {
        return { ...card, main: null, mainBinding: null };
      }

      const small = [...(card.small ?? [])];
      const smallBindings = [...(card.smallBindings ?? [])];
      small.splice(index, 1);
      smallBindings.splice(index, 1);
      return { ...card, small, smallBindings };
    }));
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
    if (source.cardId === target.cardId && source.slot === target.slot && source.index === target.index) {
      return;
    }

    updateCards((items) => {
      const next = items.map((card) => ({
        ...card,
        small: [...(card.small ?? [])],
        smallBindings: [...(card.smallBindings ?? [])]
      }));
      const sourceCard = next.find((card) => card.id === source.cardId);
      const targetCard = next.find((card) => card.id === target.cardId);
      if (!sourceCard || !targetCard) {
        return items;
      }

      const sourceValue = readDashboardSlot(sourceCard, source);
      const sourceBinding = readDashboardSlotBinding(sourceCard, source);
      if (!sourceValue) {
        return items;
      }

      const targetValue = readDashboardSlot(targetCard, target);
      const targetBinding = readDashboardSlotBinding(targetCard, target);
      if (source.cardId === target.cardId && source.slot === "small" && target.slot === "small" && targetValue) {
        const small = sourceCard.small.map(textOrEmpty).filter(Boolean);
        const smallBindings = [...(sourceCard.smallBindings ?? [])];
        if (source.index < small.length && target.index < small.length) {
          const [moved] = small.splice(source.index, 1);
          const [movedBinding] = smallBindings.splice(source.index, 1);
          small.splice(target.index, 0, moved);
          smallBindings.splice(target.index, 0, movedBinding ?? null);
          sourceCard.small = small.slice(0, dashboardSmallMetricLimit);
          sourceCard.smallBindings = smallBindings.slice(0, dashboardSmallMetricLimit);
          return next;
        }
      }

      writeDashboardSlot(sourceCard, source, targetValue, targetBinding);
      writeDashboardSlot(targetCard, target, sourceValue, sourceBinding);
      return normalizeCards(next);
    });
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
      setResourceTableSort({ columnId: "impact", direction: "desc" });
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

  function updateResourceTableSort(columnId: string, direction: "asc" | "desc") {
    setResourceTableSort({ columnId, direction });
  }

  function toggleResourceTableColumn(columnId: string, visible: boolean) {
    updateResourceTableColumns((columns) => {
      const normalized = normalizeResourceTableColumns(columns, catalog(), resourceTableMode());
      return normalized.map((column) => column.id === columnId ? { ...column, visible: column.id === "name" ? true : visible } : column);
    });
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

  function selectResourceSegment(selection: ResourcePrecisionSelection, hideCursor: boolean) {
    setResourceSelection(selection);
    setExpandedResourceSegments((current) => ({ ...current, [selection.metricId]: selection.softwareId }));
    if (hideCursor) {
      document.body.classList.add("resource-precision-cursor-hidden");
      window.setTimeout(() => document.body.classList.remove("resource-precision-cursor-hidden"), 900);
    }
  }

  function clearResourceSelection() {
    setResourceSelection(null);
    setExpandedResourceSegments({});
    document.body.classList.remove("resource-precision-cursor-hidden");
  }

  function pruneResourceSelection(bars: ResourceBreakdownBar[]) {
    const current = resourceSelection();
    const next = reconcileResourceSelection(
      bars,
      current);
    setResourceSelection(next);
    if (!next) {
      setExpandedResourceSegments({});
      return;
    }
    setExpandedResourceSegments((expanded) => {
      const nextExpanded = { ...expanded };
      if (current && current.metricId !== next.metricId) {
        delete nextExpanded[current.metricId];
      }
      nextExpanded[next.metricId] = next.softwareId;
      return nextExpanded;
    });
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
    dashboardSettingsSource,
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
    activeCards,
    resourceBarEditing,
    activeResourceBars,
    resourceTableEditing,
    resourceTableMode,
    activeResourceTableColumns,
    resourceBreakdownSnapshot,
    resourceProcessDetails,
    resourceProcessDetailsObservation,
    resourceBarsObservation,
    resourceTableSnapshot,
    resourceTableObservation,
    resourceTableSort,
    expandedResourceTableRows,
    resourceSelection,
    expandedResourceSegments,
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
    moveDashboardCard,
    moveDashboardMetricSlot,
    toggleResourceBarEditMode,
    cancelResourceBarEdit,
    toggleResourceBar,
    updateResourceBarScale,
    reorderResourceBars,
    changeResourceTableMode,
    toggleResourceTableEditMode,
    cancelResourceTableEdit,
    updateResourceTableSort,
    toggleResourceTableColumn,
    updateResourceTableColumnWidth,
    toggleResourceTableExpand,
    reorderResourceTableColumns,
    selectResourceSegment,
    clearResourceSelection,
    cancelAllMonitorEdits
  };
}

/** 目录只有设备描述和字段定义（纯 JSON 数据），按完整内容比较，目录合同加字段时也不漏。 */
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
