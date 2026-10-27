import { usePushSource } from "../frontendRuntime/push/usePushSource.ts";
import { dashboardMetricAvailable } from "../preferences/dashboardPreferences.ts";
import { subscribeFrontendPreferencesReset } from "../preferences/frontendPreferences.ts";
import { createEffect, createMemo, createSignal, on, onCleanup, untrack } from "solid-js";
import type { Accessor, Setter } from "solid-js";
import { saveDashboardSettings } from "../api";
import type { DashboardSlotRef } from "../features/monitor/Dashboard";
import { defaultPerformanceMetricIds } from "../features/resourceTable/ResourcePerformancePanel";
import { createResourceFactsProjection } from "../features/resourceTable/resourceFacts.ts";
import { resourceTableMetricId } from "../features/resourceTable/resourceTableSources.ts";
import { defaultResourceTableSort, nextResourceTableSort, type ResourceTableSort } from "../features/resourceTable/resourceTableSort.ts";
import { gpuAdapterNames } from "../features/monitor/gpuAdapterNames.ts";
import type { ProcessMetricField } from "../data/resourceMonitor/processFieldCatalog.ts";
import type { ResourcePrecisionSelection } from "../features/resourceBreakdown/ResourceBreakdown";
import { reconcileResourceSelection } from "../features/resourceBreakdown/resourceSelection";
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
import { uiText } from "../text.ts";
import { createResourceBreakdownBindings } from "./resourceBreakdownBindings.ts";

/** 选监控项弹窗为哪个槽位打开：序号等于槽位数时是在末尾新加一个这个大小的槽位，否则换掉那个槽位的监控项。 */
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
  /** 进程字段目录：资源表列的来源选择和表头口径从这里来。 */
  processFieldCatalogSource: SourceHandle<readonly ProcessMetricField[]>;
  dashboardSettingsSource: SourceHandle<DashboardSettingsResult>;
  metricSnapshotSource: PushValueSourceFamily<MetricSnapshotQuery, MetricSnapshot>;
  /**
   * 目录订阅：每帧同时带目录修订号、规范字段的设备目录和指标目录。设备目录给卡片选择器的设备字段条目、
   * 卡片上的设备名和「设备不在」；订阅收到过帧之后，指标目录也只从这里来（见 applyCatalogPush）。
   */
  metricFieldCatalogSource: PushValueSource<MetricCatalogPush>;
  /** 卡片上设备字段的当前值，按字段集合订阅。 */
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
  /** 卡片可选的全部指标：原有指标目录加上每台设备的规范字段。 */
  cardCatalog: Accessor<MetricDefinition[]>;
  /** 选择器里列出的指标：cardCatalog 去掉当前读数来源设置下不列出的设备字段来源。 */
  pickerCatalog: Accessor<MetricDefinition[]>;
  /** 卡片显示用的当前值：原有指标快照加上卡片上设备字段的值。 */
  cardSnapshot: Accessor<MetricSnapshot | null>;
  /**
   * 还在等首个读数的卡片指标：cardSnapshot 里没有它，且送它的那一路还没交过这次查询的帧
   * （设备字段的值或目录未到；原有指标的这次查询还在加载）。卡片据此画“读取中”，不写“无读数”。
   */
  cardPendingMetricIds: Accessor<ReadonlySet<string>>;
  /** 大、超大槽位里监控项最近的读数（features/monitor/cardTrends.ts）。 */
  cardTrends: Accessor<CardTrends>;
  activeCards: Accessor<DashboardCardSettings[]>;
  resourceBarEditing: Accessor<boolean>;
  activeResourceBars: Accessor<ResourceBarSettings[]>;
  resourceTableEditing: Accessor<boolean>;
  resourceTableMode: Accessor<ResourceTableViewMode>;
  activeResourceTableColumns: Accessor<ResourceTableColumnSettings[]>;
  /** 后端进程字段目录；还没读到时为空数组。 */
  processFields: Accessor<readonly ProcessMetricField[]>;
  resourceBreakdownSnapshot: Accessor<ResourceBreakdownSnapshot | null>;
  resourceProcessDetails: Accessor<ResourceProcessDetailsSnapshot | null>;
  resourceProcessDetailsObservation: Accessor<ObservationState>;
  resourceBarsObservation: Accessor<ObservationState>;
  resourceTableSnapshot: Accessor<ResourceTableSnapshot | null>;
  resourceTableObservation: Accessor<ObservationState>;
  resourceTableSort: Accessor<ResourceTableSort>;
  /** 后端编号的显卡（键是编号 N）各自的名字；后端没给名字的编号不在里面。 */
  gpuNames: Accessor<ReadonlyMap<string, string>>;
  expandedResourceTableRows: Accessor<Record<string, boolean>>;
  resourceSelection: Accessor<ResourcePrecisionSelection | null>;
  expandedResourceSegments: Accessor<Record<string, string>>;
  metricModalTarget: Accessor<MetricModalTarget | null>;
  selectedMetricId: Accessor<string | null>;
  setSelectedMetricId: (metricId: string | null) => void;
  /** 按资源条设置的顺序接到项目上的后端资源条（选了来源的条按项目 id 显示）。 */
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
  /** 改一条资源条的来源：null 是自动选择，其余是进程字段名。进编辑草稿，保存时写前端布局缓存。 */
  changeResourceBarSource: (metricId: string, source: string | null) => void;
  reorderResourceBars: (sourceMetricId: string, targetMetricId: string) => void;
  changeResourceTableMode: (mode: ResourceTableViewMode) => void;
  toggleResourceTableEditMode: () => void;
  cancelResourceTableEdit: () => void;
  /** 点一次某列表头：降序、升序、回到默认的综合排序轮流（resourceTableSort.ts）。 */
  cycleResourceTableSort: (columnId: string) => void;
  toggleResourceTableColumn: (columnId: string, visible: boolean) => void;
  /** 改一列的来源：null 是自动选择，其余是进程字段名。和显示/隐藏一样进编辑草稿，保存时写前端布局缓存。 */
  changeResourceTableColumnSource: (columnId: string, source: string | null) => void;
  updateResourceTableColumnWidth: (columnId: string, width: number, commit: boolean) => void;
  toggleResourceTableExpand: (softwareId: string) => void;
  reorderResourceTableColumns: (sourceColumnId: string, targetColumnId: string) => void;
  selectResourceSegment: (selection: ResourcePrecisionSelection, hideCursor: boolean) => void;
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
  // 当前显示的指标帧回答了哪些指标：带数据的那一帧的查询里的指标，查询不限指标时是全部。
  // 布局由前端缓存或本次构建的点值产生；加载前不制造第二份占位默认值。
  const [cards, setCards] = createSignal<DashboardCardSettings[]>([]);
  const [resourceBars, setResourceBars] = createSignal<ResourceBarSettings[]>([]);
  const [resourceTableColumns, setResourceTableColumns] = createSignal<ResourceTableColumnSettings[]>([]);
  const [resourceTableProcessColumns, setResourceTableProcessColumns] = createSignal<ResourceTableColumnSettings[]>([]);
  const [resourceTableMode, setResourceTableMode] = createSignal<ResourceTableViewMode>("software");
  const [resourceTableEditMode, setResourceTableEditMode] = createSignal(false);
  const [resourceBarEditMode, setResourceBarEditMode] = createSignal(false);
  const [resourceTableSort, setResourceTableSort] = createSignal<ResourceTableSort>(defaultResourceTableSort);
  const [expandedResourceTableRows, setExpandedResourceTableRows] = createSignal<Record<string, boolean>>({});
  const [resourceSelection, setResourceSelection] = createSignal<ResourcePrecisionSelection | null>(null);
  // 每条资源条展开的段（按条的 metricId），记着选中时这一段的类型化身份。
  const [expandedResourceSelections, setExpandedResourceSelections] = createSignal<Record<string, ResourcePrecisionSelection>>({});
  const expandedResourceSegments = createMemo(() => Object.fromEntries(Object.entries(expandedResourceSelections())
    .map(([metricId, selection]) => [metricId, selection.softwareId])));
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
  // 进程字段目录是后端常量表，读一次。
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
  // 出厂布局随目录重新展开（见 release_defaults_contract.md），目录换新时要重取看板设置；用户保存过的布局不用。
  let dashboardUsesFactoryDefaults = false;
  // 当前目录所属的后端目录修订号（不透明，只作相等比较）。来自目录订阅帧，或快照帧（这时重取目录，重取失败就清掉，下一帧再试）。
  let backendCatalogRevision: string | null = null;
  // 目录订阅在当前这次订阅里收到过帧：指标目录只由订阅帧写，GET 结果和快照帧的修订号不再改它。订阅结束时清掉。
  let catalogFromPush = false;

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
      // 设备字段走按字段订阅，不进原有指标订阅。
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
  // 明细订阅：展开或选中的真实软件按 id 进 processDetails；系统/驱动保留按所属资源条的指标 id 进 residualDetails。
  const resourceBreakdownDetailSelections = createMemo(() => [...Object.values(expandedResourceSelections()), resourceSelection()]
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
    onBars: value => pruneResourceSelection(resourceBarViews(value.breakdown.bars ?? [], activeResourceBars(), processFields()))
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
  const resourceBreakdownSnapshot = createMemo(() => breakdownBindings.bars()?.data?.breakdown ?? null);
  const resourceProcessDetails = createMemo(() => breakdownBindings.details()?.data ?? null);
  const resourceProcessDetailsObservation = createMemo(() => pushObservation(breakdownBindings.details()));
  const resourceTableFacts = createMemo(() => tableSourceSnapshot().data?.facts ?? null);
  const resourceTableSnapshot = createResourceFactsProjection(resourceTableFacts,
    activeResourceTableColumns, resourceTableMode, resourceTableSort);
  const resourceBarsObservation = createMemo(() => pushObservation(breakdownBindings.bars()));
  const resourceTableObservation = createMemo(() => pushObservation(tableSourceSnapshot()));
  const orderedSnapshotBars = createMemo(() => resourceBarViews(resourceBreakdownSnapshot()?.bars ?? [], activeResourceBars(), processFields()));
  // 目录订阅每个探测周期推一次，内容通常不变；内容相同就不算变化，卡片目录和指标选择器不跟着重建。
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
  // 后端编号的显卡各自的名字（gpuAdapterNames.ts）：卡片、资源条、资源表在 GPU 编号旁用小字写出，编号原样。
  const gpuNames = createMemo(() => gpuAdapterNames(catalog(), fieldCatalog()));
  // 卡片上已经放了的字段照常从 cardCatalog 取设备名和标记；只有选择器按设置少列。
  const pickerCatalog = createMemo(() => cardCatalog().filter(metricOfferedInPicker));
  const cardSnapshot = createMemo<MetricSnapshot | null>(() => {
    const items = fieldCardValues(fieldSlots(), fieldCatalog(), fieldDevices());
    const legacy = snapshot();
    if (Object.keys(items).length === 0) {
      return legacy;
    }
    // 卡片显示不读目录修订号，沿用指标帧的；还没有指标帧时为空。
    return { version: 5, capturedAt: legacy?.capturedAt ?? null, catalogRevision: legacy?.catalogRevision ?? "", items: { ...legacy?.items, ...items } };
  });
  // 卡片上的一个指标还在等首个读数：当前值里没有它，且送它的那一路还没回答过它、也没有报错。
  // 设备字段：值和目录都到之前 fieldCardValues 不写这一项，到了之后每个槽位都写（值、不可用或设备不在）。
  // 原有指标由 family 投影所有已回答项（含明确不可用的项）；没有项就是尚未收到回答。
  const cardPendingMetricIds = createMemo<ReadonlySet<string>>(() => {
    const items = cardSnapshot()?.items ?? {};
    const legacyFailed = snapshotObservation().status === "error";
    const fieldsFailed = pushObservation(fieldDevicesSnapshot() ?? null).status === "error";
    const pending = new Set<string>();
    for (const card of activeCards()) {
      for (const { metricId, binding } of card.slots) {
        if (items[metricId]) continue;
        const waiting = fieldCardSlot(metricId, binding)
          ? !fieldsFailed
          : !legacyFailed;
        if (waiting) pending.add(metricId);
      }
    }
    return pending;
  });
  // 大、超大槽位的趋势：送这个监控项的那一路每来一帧（原有指标帧或设备字段帧）记一点，另一路的帧不记，时间轴不重复。
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
      sampleMetricIds: [...new Set([...(activeResourceTableColumns().some(column => column.visible) ? ["process.inventory", "process.attribution"] : []), ...normalizeResourceTableColumns(activeResourceTableColumns(), catalog(), resourceTableMode())
        .filter(column => column.visible && dashboardMetricAvailable(column.id, column.binding, catalog())).map(column => resourceTableMetricId(column)).filter((id): id is string => id !== null)])],
      processDetailLevel: activeResourceTableColumns().some(column => column.visible && ["user", "architecture"].includes(column.id)) ? "full" : "basic",
      visibleColumnIds: [],
      sortColumnId: "",
      sortDirection: "desc",
      processDetailSoftwareIds: [],
      residualDetailMetricIds: [],
      tableMode: "software"
    };
  }

  /**
   * 目录订阅的一帧：指标目录与设备目录、修订号同帧生效，不等普通指标帧。
   * 同一修订号下组件安装信息也会变，所以指标目录按内容替换，不看修订号是否变化；
   * 修订号变化时，仍是出厂布局就重取看板设置（和快照帧的路径相同）。
   */
  function applyCatalogPush(push: MetricCatalogPush): void {
    catalogFromPush = true;
    // 订阅可能在 createEffect 里同步回调：读当前值不能让那个 effect 跟踪目录。
    untrack(() => {
      if (JSON.stringify(catalog()) !== JSON.stringify(push.metrics)) setCatalog(push.metrics);
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
      // 目录修订后按出厂布局重读，内容通常不变；相同内容不算变化，
      // 否则卡片全部重建、可见需求注销再注册，订阅随之退订重订，合并订阅通道整条重建。
      if (!editMode()) {
        replaceIfChanged(cards, setCards, settings.cards);
      }
      if (!resourceBarEditing()) {
        replaceIfChanged(resourceBars, setResourceBars, settings.resourceBars);
      }
      if (!resourceTableEditing()) {
        replaceIfChanged(resourceTableColumns, setResourceTableColumns, settings.resourceTableColumns);
        replaceIfChanged(resourceTableProcessColumns, setResourceTableProcessColumns, settings.resourceTableProcessColumns);
      }
      lastDashboardSettingsRevision = source.revision;
    }
    setDashboardSettingsState("ready");
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

  function selectResourceSegment(selection: ResourcePrecisionSelection, hideCursor: boolean) {
    setResourceSelection(selection);
    setExpandedResourceSelections((current) => ({ ...current, [selection.metricId]: selection }));
    if (hideCursor) {
      document.body.classList.add("resource-precision-cursor-hidden");
      window.setTimeout(() => document.body.classList.remove("resource-precision-cursor-hidden"), 900);
    }
  }

  function clearResourceSelection() {
    setResourceSelection(null);
    setExpandedResourceSelections({});
    document.body.classList.remove("resource-precision-cursor-hidden");
  }

  function pruneResourceSelection(bars: ResourceBreakdownBar[]) {
    const current = resourceSelection();
    const next = reconcileResourceSelection(
      bars,
      current);
    setResourceSelection(next);
    if (!next) {
      setExpandedResourceSelections({});
      return;
    }
    setExpandedResourceSelections((expanded) => {
      const nextExpanded = { ...expanded };
      if (current && current.metricId !== next.metricId) {
        delete nextExpanded[current.metricId];
      }
      nextExpanded[next.metricId] = next;
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
    changeResourceTableColumnSource,
    updateResourceTableColumnWidth,
    toggleResourceTableExpand,
    reorderResourceTableColumns,
    selectResourceSegment,
    clearResourceSelection,
    cancelAllMonitorEdits
  };
}

/** 布局数据是纯 JSON；内容相同就保留当前对象，消费端（卡片列表、可见需求、订阅）不随之重建。 */
function replaceIfChanged<T>(current: Accessor<T>, set: Setter<T>, next: T): void {
  if (JSON.stringify(current()) !== JSON.stringify(next)) set(() => structuredClone(next));
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
