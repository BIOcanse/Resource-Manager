import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { observeLayout } from "../../interactions/solid/layoutObservation.ts";
import {
  resourceTableCellPresentation,
  resourceTableCellText,
  type ResourceTableCellPresentation,
  resourceTableRowName as rowName
} from "../../presentation/metricLabels";
import type { ProcessMetricField } from "../../data/resourceMonitor/processFieldCatalog.ts";
import {
  resourceColumnSourceChoices,
  resourceColumnSourceLabel,
  resourceTableColumnText
} from "./resourceTableSources.ts";
import { RadioGroupItem, RadioGroupRoot } from "../../ui/primitives/RadioGroup.tsx";
import { batch, createComputed, createEffect, createMemo, createSignal, For, Index, onCleanup, onMount, Show } from "solid-js";
import type { JSX } from "solid-js";
import { ArrowDown, ArrowLeft, ArrowRight } from "lucide-solid";
import { userFacingLabel } from "../../presentation/userFacingText";
import { isReservedSegment } from "../resourceBreakdown/resourceProcessIdentity.ts";
import { ResourcePerformancePanel } from "./ResourcePerformancePanel";
import type { SoftwareContextMenuTarget } from "../../components/SoftwareContextMenu";
import type { MetricDefinition, MetricSnapshot, ResourceTableColumn, ResourceTableColumnSettings, ResourceTableRow, ResourceTableSnapshot, ResourceTableViewMode } from "../../types";
import { uiText } from "../../text.ts";
import { SegmentedControl } from "../../ui/primitives/SegmentedControl.tsx";
import { moveRovingFocus } from "../../ui/primitives/RovingFocus.ts";
import type { ObservationState } from "../../observation/observationState";
import {
  moveResourceTableFocus,
  reconcileResourceTableFocus,
  resourceTableFocusableRows,
  type ResourceRowFocusMove
} from "./resourceTableFocus";
import {
  classifyResourceTableContentState,
  resourceTableCountText,
  type ResourceTableContentState
} from "./resourceTableContentState";
import { pinResourceTableRow } from "./resourceTableRowPin";
import { resourceTableColumnsEqual } from "./resourceTableColumnIdentity";
import { resourceTableHeatMaxima, type ResourceTableHeatMaxima } from "./resourceTableHeat";
import { accountResourceTableVisibility, resourceTableBusinessRowCount } from "./resourceTableAccounting.ts";
import { ContentState } from "../../ui/patterns/ContentState.tsx";
import { useInlineEditorFocus } from "../../interactions/inlineEditorFocus";
import { columnEdgeBound, columnResizeHandleDefinition } from "../../interactions/components/columnResizeHandle.ts";
import { solidHost, useInteraction } from "../../interactions/solid/lodestone.ts";
import { createReorderGroup, type ReorderGroup } from "../../interactions/solid/reorderGroup.ts";
import type { Presentation } from "../../interactions/protocol/runtime.ts";
import { enterSlot, exitInPlace, settleEntrancesWithin } from "../../presentation/nodeAnimation.ts";
import { GpuNameNote } from "../monitor/GpuNameNote.tsx";
import { HelpHint, OptionsHelpHint } from "../../ui/primitives/HelpHint.tsx";
import { MetricModal } from "../monitor/MetricModal.tsx";
import { fitTableColumnWidth, tableFieldCatalog } from "./resourceTableColumns.ts";
import { ResourceManager } from "../../preferences/generated/frontendPoints.values.ts";
import { dashboardPointValues } from "../../preferences/dashboardPreferences.ts";
import { clampResourceTableColumnWidth } from "../../stores/monitorConfig.ts";

const rowHeight = 38;
const overscan = 6;
const slotReserve = 10;
const minColumnWidth = ResourceManager.FrontendPoints.DashboardLayoutLimits.minimumColumnWidth;
const maxColumnWidth = ResourceManager.FrontendPoints.DashboardLayoutLimits.maximumColumnWidth;
const columnResizeKeyStep = ResourceManager.FrontendPoints.TableColumnResizeKeyStep;
type CssVars = JSX.CSSProperties & Record<string, string>;
interface ResourceTableRowPresentation {
  readonly row: ResourceTableRow | undefined;
  readonly name: string;
  readonly status: string;
  readonly cells: ReadonlyMap<string, ResourceTableCellPresentation>;
}
const emptyCellPresentation: ResourceTableCellPresentation = {
  display: "--", title: "", heat: 0, privateHeat: 0, unavailable: false
};
interface RowSearchKeyCacheEntry {
  signature: string;
  key: string;
}

type SoftwareContextMenuHandler = (
  event: MouseEvent,
  target: SoftwareContextMenuTarget,
  returnFocusTarget?: HTMLElement | null
) => void;

interface ResourceTableProps {
  editMode: boolean;
  editingAvailable: boolean;
  saveState: "idle" | "saving" | "error";
  mode: ResourceTableViewMode;
  catalog: MetricDefinition[];
  metricSnapshot: MetricSnapshot | null;
  metricObservation: ObservationState;
  columnSettings: ResourceTableColumnSettings[];
  snapshot: ResourceTableSnapshot | null;
  observation: ObservationState;
  /** Backend GPU names keyed by their unchanged indices. */
  gpuNames: ReadonlyMap<string, string>;
  sortColumnId: string;
  sortDirection: string;
  expandedSoftwareIds: Record<string, boolean>;
  highlightedSoftwareId?: string | null;
  onModeChange: (mode: ResourceTableViewMode) => void;
  onToggleEdit: () => void;
  onCancelEdit: () => void;
  /** The store owns header-click sort transitions. */
  onSortColumn: (columnId: string) => void;
  onToggleColumn: (columnId: string, visible: boolean) => void;
  onAddColumn: (columnId: string) => void;
  onRemoveColumn: (columnId: string) => void;
  /** Backend process fields define source choices and header semantics. */
  processFields: readonly ProcessMetricField[];
  onColumnSourceChange: (columnId: string, source: string | null) => void;
  onColumnWidthChange: (columnId: string, width: number, commit: boolean) => void;
  onToggleExpand: (softwareId: string) => void;
  onSoftwareContextMenu?: SoftwareContextMenuHandler;
  /** Pin the context-menu row until the menu closes. */
  contextMenuOpen?: boolean;
  onReorderColumn: (sourceColumnId: string, targetColumnId: string) => void;
}

export function ResourceTable(props: ResourceTableProps) {
  let editButton: HTMLButtonElement | undefined;
  let editorRegion: HTMLDivElement | undefined;
  const [searchQuery, setSearchQuery] = createSignal("");
  const rowSearchKeyCache = new Map<string, RowSearchKeyCacheEntry>();
  const sourceRows = createMemo(() => props.snapshot?.rows ?? []);
  const projectedRows = createMemo(() => projectResourceTableRows(
    sourceRows(),
    props.mode,
    props.expandedSoftwareIds,
    searchQuery(),
    rowSearchKeyCache));
  const visibleRows = createMemo(() => accountResourceTableVisibility(sourceRows(), projectedRows(), props.mode, props.snapshot?.columns ?? []));
  // Compute heat from mounted rows without copying rows or cells.
  const heatMaxima = createMemo(() => resourceTableHeatMaxima(visibleRows()));
  const baseCount = createMemo(() => {
    const rows = sourceRows();
    return props.mode === "software"
      ? rows.filter((row) => row.kind === "software").length
      : rows.filter((row) => row.kind !== "summary").length;
  });
  const visibleBusinessCount = createMemo(() => resourceTableBusinessRowCount(visibleRows()));
  const contentState = createMemo(() => classifyResourceTableContentState({
    hasSnapshot: props.snapshot !== null,
    status: props.observation.status,
    businessRowCount: baseCount(),
    visibleBusinessRowCount: visibleBusinessCount(),
    searchActive: Boolean(normalizeSearchText(searchQuery()))
  }));
  const statusText = createMemo(() => {
    if (props.mode === "performance") {
      return "--";
    }

    return resourceTableCountText({
      state: contentState(),
      businessRowCount: baseCount(),
      visibleBusinessRowCount: visibleBusinessCount(),
      searchActive: Boolean(normalizeSearchText(searchQuery()))
    });
  });
  useInlineEditorFocus({
    active: () => props.editMode,
    opener: () => editButton,
    editor: () => editorRegion
  });

  return (
    <section
      class="resource-table-panel"
      aria-label={uiText.resourceTable.panel}
    >
      <div class="panel-header">
        <div class="resource-table-title">
          <h2>{props.mode === "performance" ? uiText.resourceTable.performance : uiText.resourceTable.panel}</h2>
          <Show when={props.mode !== "performance"}>
            <span id="resourceTableStatus" class="count-badge">{statusText()}</span>
            <HelpHint>{uiText.resourceTable.accountingBasis}</HelpHint>
          </Show>
        </div>
        <div class="panel-header-actions">
          <Show when={props.saveState === "error"}>
            <span class="save-state error">{uiText.common.saveFailed}</span>
          </Show>
          <Show when={props.mode !== "performance"}>
            <label class="resource-table-search">
              <input
                type="search"
                aria-label={uiText.resourceTable.searchPlaceholder}
                value={searchQuery()}
                placeholder={uiText.resourceTable.searchPlaceholder}
                spellcheck={false}
                onInput={(event) => setSearchQuery(event.currentTarget.value)}
              />
            </label>
          </Show>
          <ResourceTableModeSwitch mode={props.mode} onModeChange={props.onModeChange} />
          <Show when={props.editMode}>
            <button
              class="secondary"
              type="button"
              aria-label={uiText.resourceTableView.cancelEditLabel}
              disabled={props.saveState === "saving"}
              onClick={props.onCancelEdit}
            >
              {uiText.resourceTableView.cancel}
            </button>
          </Show>
          <Show when={props.mode !== "performance"}>
            <button
              ref={editButton}
              class="panel-refresh-button"
              type="button"
              data-focus-key="resource-table-edit"
              aria-label={props.editMode ? uiText.resourceTableView.saveLabel : uiText.resourceTableView.editLabel}
              disabled={!props.editingAvailable || props.saveState === "saving"}
              onClick={props.onToggleEdit}
            >
              <StableLabel
                labels={{ edit: uiText.resourceTable.edit, save: uiText.resourceTable.save, saving: uiText.common.saving }}
                current={props.editMode ? props.saveState === "saving" ? "saving" : "save" : "edit"}
              />
            </button>
          </Show>
        </div>
      </div>
      <Show when={props.editMode && props.mode !== "performance"}>
        <ResourceTableColumnEditor
          onElement={(element) => { editorRegion = element; }}
          mode={props.mode}
          catalog={props.catalog}
          gpuNames={props.gpuNames}
          columns={props.columnSettings}
          processFields={props.processFields}
          snapshotColumns={props.snapshot?.columns ?? []}
          onToggle={props.onToggleColumn}
          onAdd={props.onAddColumn}
          onRemove={props.onRemoveColumn}
          onSourceChange={props.onColumnSourceChange}
        />
      </Show>
      <Show
        when={props.mode === "performance"}
        fallback={
          <>
            <div class="resource-table-retained-surface" hidden={contentState() !== null}>
              <VirtualResourceTable
                mode={props.mode}
                editMode={props.editMode}
                rows={visibleRows()}
                heatMaxima={heatMaxima()}
                snapshot={props.snapshot}
                columnSettings={props.columnSettings}
                processFields={props.processFields}
                gpuNames={props.gpuNames}
                sortColumnId={props.sortColumnId}
                sortDirection={props.sortDirection}
                expandedSoftwareIds={props.expandedSoftwareIds}
                highlightedSoftwareId={props.highlightedSoftwareId}
                onSortColumn={props.onSortColumn}
                onColumnWidthChange={props.onColumnWidthChange}
                onToggleExpand={props.onToggleExpand}
                onSoftwareContextMenu={props.onSoftwareContextMenu}
                contextMenuOpen={props.contextMenuOpen}
                onReorderColumn={props.onReorderColumn}
              />
            </div>
            <Show when={contentState() !== null}>
              <ResourceTableContentStateView
                state={contentState()!}
                onClearSearch={() => setSearchQuery("")}
              />
            </Show>
          </>
        }
      >
        <ResourcePerformancePanel
          catalog={props.catalog}
          snapshot={props.metricSnapshot}
          observation={props.metricObservation}
        />
      </Show>
    </section>
  );
}

function ResourceTableContentStateView(props: {
  readonly state: ResourceTableContentState;
  readonly onClearSearch: () => void;
}) {
  const title = () => {
    switch (props.state) {
      case "pending": return uiText.observationNotice.loading(uiText.resourceTable.panel);
      case "unavailable": return uiText.observationNotice.unavailable(uiText.resourceTable.panel);
      case "filtered-empty": return uiText.resourceTableView.noMatchTitle;
      default: return uiText.resourceTableView.emptyTitle;
    }
  };
  return <ContentState
    kind={props.state === "pending" ? "waiting" : props.state === "unavailable" ? "unavailable" : "empty"}
    title={title()}
    detail={props.state === "filtered-empty" ? uiText.resourceTableView.noMatchDetail
      : props.state === "ready-empty" ? uiText.resourceTableView.emptyDetail : undefined}
    actions={<Show when={props.state === "filtered-empty"}>
      <button class="secondary" type="button" onClick={props.onClearSearch}>{uiText.resourceTableView.clearSearch}</button>
    </Show>}
  />;
}

function ResourceTableModeSwitch(props: {
  mode: ResourceTableViewMode;
  onModeChange: (mode: ResourceTableViewMode) => void;
}) {
  // Resolve these labels reactively so language changes update them.
  const modes = (): Array<{ id: ResourceTableViewMode; label: string }> => [
    { id: "software", label: uiText.resourceTable.modes.software },
    { id: "process", label: uiText.resourceTable.modes.process },
    { id: "performance", label: uiText.resourceTable.modes.performance }
  ];
  return (
    <SegmentedControl
      value={props.mode}
      options={modes()}
      ariaLabel={uiText.resourceTable.viewLabel}
      class="resource-table-mode-switch"
      itemClass="resource-table-mode-button"
      onChange={props.onModeChange}
    />
  );
}

function ResourceTableColumnEditor(props: {
  onElement?: (element: HTMLDivElement) => void;
  mode: ResourceTableViewMode;
  catalog: MetricDefinition[];
  gpuNames: ReadonlyMap<string, string>;
  columns: ResourceTableColumnSettings[];
  processFields: readonly ProcessMetricField[];
  /** Automatic columns carry the field actually read in this snapshot. */
  snapshotColumns: readonly ResourceTableColumn[];
  onToggle: (columnId: string, visible: boolean) => void;
  onAdd: (columnId: string) => void;
  onRemove: (columnId: string) => void;
  onSourceChange: (columnId: string, source: string | null) => void;
}) {
  const [pickerOpen, setPickerOpen] = createSignal(false);
  const [selectedField, setSelectedField] = createSignal<string | null>(null);
  const pickerCatalog = createMemo(() => tableFieldCatalog(props.processFields, props.mode, props.catalog)
    .filter(field => !props.columns.some(column => column.id === field.id)));
  const byId = () => new Map(props.columns.map((column) => [column.id, column]));
  // Use the snapshot field for automatic selection only when the column is currently automatic.
  const automaticField = (columnId: string) => {
    const column = props.snapshotColumns.find((candidate) => candidate.id === columnId);
    return column && column.source === null ? column.processFieldName : null;
  };
  const editorColumns = createMemo(() => resourceTableEditorColumns(props.columns, props.catalog, props.mode));
  // Key chips by column ID so recalculation preserves keyboard focus.
  const editorColumnIds = createMemo(() => editorColumns().map((column) => column.id));
  const editorColumnById = createMemo(() => new Map(editorColumns().map((column) => [column.id, column])));
  // The checkbox toggles visibility; the rest of the chip selects one column.
  // Show sources only for the selected column; selecting it again clears selection.
  const [selectedColumnId, setSelectedColumnId] = createSignal<string | null>(null);
  // Show a source row only while its selected column still exists and offers choices.
  const sourceColumnId = createMemo(() => {
    const id = selectedColumnId();
    return id !== null && editorColumnById().has(id) && resourceColumnSourceChoices(id, props.processFields).length > 0 ? id : null;
  });
  return (
    <div
      ref={props.onElement}
      class="resource-table-column-editor"
      role="group"
      aria-label={uiText.resourceTableView.columnEditorLabel}
      // Arrow keys and Home/End move focus without changing selection.
      onKeyDown={(event) => {
        if ((event.target as Element).classList.contains("resource-table-column-select")) {
          moveRovingFocus(event, { selector: ".resource-table-column-select", previousKeys: ["ArrowLeft"], nextKeys: ["ArrowRight"] });
        }
      }}
    >
      <button type="button" class="secondary resource-table-add-column" onClick={() => { setSelectedField(null); setPickerOpen(true); }}>
        {uiText.resourceTableView.addColumn}
      </button>
      <MetricModal open={pickerOpen()} catalog={pickerCatalog()} components={[]} selectedMetricId={selectedField()}
        onSelect={setSelectedField} onClose={() => setPickerOpen(false)}
        onConfirm={() => { const id = selectedField(); if (id) props.onAdd(id); setPickerOpen(false); }}
        onDependencyPrompt={() => {}} />
      <For each={editorColumnIds()}>
        {(columnId) => {
          const label = () => resourceTableColumnText({ id: columnId }, props.processFields).label;
          const selected = () => selectedColumnId() === columnId;
          return (
            // Treat chip padding as selection; handle bubbled button activation once.
            <div
              class="resource-table-column-option"
              classList={{ selected: selected() }}
              onClick={(event) => {
                if (event.target instanceof HTMLInputElement) return;
                setSelectedColumnId(selected() ? null : columnId);
              }}
            >
              <input
                type="checkbox"
                aria-label={uiText.resourceTableView.showColumn(label())}
                disabled={columnId === "name"}
                checked={byId().get(columnId)?.visible ?? editorColumnById().get(columnId)?.visible ?? false}
                onChange={(event) => props.onToggle(columnId, event.currentTarget.checked)}
              />
              <button type="button" class="resource-table-column-select" aria-pressed={selected()}>
                {label()}
                <GpuNameNote metricId={props.processFields.find(field => field.name === columnId)?.resourceFactTotalMetricId ?? columnId} names={props.gpuNames} />
              </button>
              <Show when={columnId !== "name"}>
                <button type="button" class="resource-table-column-remove" aria-label={uiText.resourceTableView.removeColumn(label())}
                  onClick={event => { event.stopPropagation(); props.onRemove(columnId); }} >×</button>
              </Show>
            </div>
          );
        }}
      </For>
      <Show when={sourceColumnId()} keyed>
        {(columnId) => {
          const choices = () => resourceColumnSourceChoices(columnId, props.processFields);
          const label = () => uiText.resourceTable.columnSourceLabel(resourceTableColumnText({ id: columnId }, props.processFields).label);
          const current = () => byId().get(columnId)?.source ?? null;
          return (
            <div class="resource-table-column-source">
              <strong>{label()}<GpuNameNote metricId={props.processFields.find(field => field.name === columnId)?.resourceFactTotalMetricId ?? columnId} names={props.gpuNames} /></strong>
              <RadioGroupRoot class="segmented resource-table-column-source-choices" ariaLabel={label()}
                // If a saved source disappears, leave all choices unchecked.
                value={(() => { const index = choices().findIndex((choice) => choice.source === current()); return index < 0 ? "" : `source-${index}`; })()}
                onChange={(value) => {
                  const choice = choices()[Number(value.slice("source-".length))];
                  if (choice) props.onSourceChange(columnId, choice.source);
                }}>
                <Index each={choices()}>
                  {(choice, index) => (
                    <RadioGroupItem value={`source-${index}`} class="segment">
                      {resourceColumnSourceLabel(choice(), props.processFields, automaticField(columnId))}
                    </RadioGroupItem>
                  )}
                </Index>
              </RadioGroupRoot>
              <OptionsHelpHint entries={choices().flatMap((choice) => {
                const basis = resourceTableColumnText({ id: columnId, source: choice.source }, props.processFields).basis;
                return basis ? [{ label: resourceColumnSourceLabel(choice, props.processFields, automaticField(columnId)), text: basis }] : [];
              })} />
            </div>
          );
        }}
      </Show>
    </div>
  );
}

function VirtualResourceTable(props: {
  mode: ResourceTableViewMode;
  editMode: boolean;
  rows: ResourceTableRow[];
  /** Each cell uses its share of the column maximum. */
  heatMaxima: ResourceTableHeatMaxima;
  snapshot: ResourceTableSnapshot | null;
  columnSettings: ResourceTableColumnSettings[];
  processFields: readonly ProcessMetricField[];
  gpuNames: ReadonlyMap<string, string>;
  sortColumnId: string;
  sortDirection: string;
  expandedSoftwareIds: Record<string, boolean>;
  highlightedSoftwareId?: string | null;
  /** The store owns header-click sort transitions. */
  onSortColumn: (columnId: string) => void;
  onColumnWidthChange: (columnId: string, width: number, commit: boolean) => void;
  onToggleExpand: (softwareId: string) => void;
  onSoftwareContextMenu?: SoftwareContextMenuHandler;
  contextMenuOpen?: boolean;
  onReorderColumn: (sourceColumnId: string, targetColumnId: string) => void;
}) {
  let viewport: HTMLDivElement | undefined;
  // Track scrolling through events and local writes; compare snapshots without forced DOM layout.
  let viewportScrollTop = 0;
  const [scrollTop, setScrollTop] = createSignal(0);
  const [scrollLeft, setScrollLeft] = createSignal(0);
  const [viewportHeight, setViewportHeight] = createSignal(360);
  const [viewportWidth, setViewportWidth] = createSignal(0);
  const [scrollbarWidth, setScrollbarWidth] = createSignal(0);
  const [activeRowId, setActiveRowId] = createSignal<string | null>(null);
  // Release the pinned row when its context menu closes.
  const [pinnedRowId, setPinnedRowId] = createSignal<string | null>(null);
  const [pinnedIndex, setPinnedIndex] = createSignal<number | null>(null);
  createEffect(() => {
    if (!props.contextMenuOpen) {
      setPinnedRowId(null);
      setPinnedIndex(null);
    }
  });
  const rows = createMemo(() => pinResourceTableRow(props.rows, pinnedRowId(), pinnedIndex()));
  // Commit rows, focus, and expansion state immediately;
  // animate only their presentation.
  // Before collapse changes the DOM, retain noninteractive copies of departing process rows.
  // Animate newly expanded rows; scrolling finishes active animations and does not animate recycled rows.
  let expandedBeforeChange: Record<string, boolean> = { ...props.expandedSoftwareIds };
  let expandedAfterChange: Record<string, boolean> = { ...props.expandedSoftwareIds };
  const processRowSlotsOf = (softwareIds: ReadonlySet<string>) => [...(viewport?.querySelectorAll<HTMLElement>(
    ".resource-table-spacer > .resource-table-row.process-row:not([data-exit-ghost])") ?? [])]
    .filter((element) => softwareIds.has(element.dataset.resourceSoftwareId ?? ""));
  createComputed(() => {
    const expanded = props.expandedSoftwareIds;
    const closed = new Set(Object.keys(expandedBeforeChange).filter((id) => expandedBeforeChange[id] && !expanded[id]));
    expandedBeforeChange = { ...expanded };
    for (const element of processRowSlotsOf(closed)) exitInPlace(element);
  });
  createEffect(() => {
    const expanded = props.expandedSoftwareIds;
    rows();
    const opened = new Set(Object.keys(expanded).filter((id) => expanded[id] && !expandedAfterChange[id]));
    expandedAfterChange = { ...expanded };
    for (const element of processRowSlotsOf(opened)) enterSlot(element, "row");
  });
  const columnSettingsById = createMemo(() => new Map(props.columnSettings.map((column) => [column.id, column])));
  const columnOrderIndex = createMemo(() => new Map(props.columnSettings.map((column, index) => [column.id, index])));
  const baseColumns = createMemo(() => {
    const source = props.snapshot?.columns ?? resourceTableColumnOptions([], props.mode).filter((column) => column.visible);
    const order = columnOrderIndex();
    return source
      .map((column) => ({
        ...column,
        width: columnSettingsById().get(column.id)?.width ?? column.width
      }))
      .sort((left, right) =>
        (order.get(left.id) ?? Number.MAX_SAFE_INTEGER) - (order.get(right.id) ?? Number.MAX_SAFE_INTEGER));
  });

  // Header dragging reorders columns; nested width handles own their input.
  const columnReorder = createReorderGroup("表头列", (source, target) => props.onReorderColumn(source, target));
  const baseTableWidth = createMemo(() => baseColumns().reduce((sum, column) => sum + clampColumnWidth(column.width), 0));
  const tableWidth = createMemo(() => Math.max(baseTableWidth(), viewportWidth()));
  const columnWidths = createMemo(() => baseColumns().map(column => clampColumnWidth(column.width)));
  const columns = createMemo<ResourceTableColumn[]>((previous) => {
    const next = baseColumns().map((column, index) => ({
      ...column,
      width: columnWidths()[index] ?? column.width
    }));
    return resourceTableColumnsEqual(previous, next) ? previous : next;
  }, []);
  const columnIds = createMemo(() => baseColumns().map((column) => column.id));
  /** Return the resized column edge relative to the header. */
  const columnRightEdge = (index: number, width: number) => {
    const resized = baseColumns().map((column, at) => at === index ? { ...column, width } : column);
    return resized.map(column => clampColumnWidth(column.width))
      .slice(0, index + 1)
      .reduce((sum, columnWidth) => sum + columnWidth, 0);
  };
  const columnWidthForEdge = (index: number, edge: number) => clampColumnWidth(edge - baseColumns()
    .slice(0, index).reduce((sum, column) => sum + clampColumnWidth(column.width), 0));
  // Headers and tooltips identify explicit sources or the actual automatic field.
  // Width corrections must not invalidate every cell reading.
  const columnsById = createMemo(() => new Map(columns().map(column => [column.id, column])));
  const columnById = (id: string) => columnsById().get(id)!;
  const columnTexts = createMemo(() => new Map(baseColumns().map(column => [column.id,
    resourceTableColumnText(column, props.processFields, column.processFieldName)])));
  const textById = (id: string) => columnTexts().get(id)!;
  const columnText = (column: ResourceTableColumn) => textById(column.id);
  const gridTemplate = createMemo(() => columnWidths().map((width) => `${width}px`).join(" "));
  const visibleRange = createMemo(() => {
    const start = Math.max(0, Math.floor(scrollTop() / rowHeight) - overscan);
    return { start };
  });
  const firstRenderedRow = createMemo(() => Math.max(0, Math.floor(scrollTop() / rowHeight) - 1));
  const lastRenderedRow = createMemo(() => Math.ceil((scrollTop() + viewportHeight()) / rowHeight) + 1);
  const slots = createMemo(() => {
    const count = Math.ceil(viewportHeight() / rowHeight) + overscan * 2 + slotReserve;
    // Retain index reuse and overscan without mounting nonexistent rows.
    // Short and empty results need no unused viewport pool.
    return Array.from({ length: Math.min(rows().length, count) }, (_, index) => index);
  });
  // Project the mounted pool synchronously in one computation.
  // Values, locale, and range changes replace that projection.
  const rowPresentations = createMemo(() => {
    const current = rows();
    const start = visibleRange().start;
    const ids = columnIds();
    const texts = columnTexts();
    const maxima = props.heatMaxima;
    return slots().map(slot => {
      const row = current[start + slot];
      const cells = new Map<string, ResourceTableCellPresentation>();
      for (const id of ids) {
        if (id !== "name" && id !== "status") {
          cells.set(id, resourceTableCellPresentation(row?.values?.[id], id, texts.get(id)!, maxima.get(id)));
        }
      }
      return { row, name: rowName(row), status: rowStatus(row), cells } satisfies ResourceTableRowPresentation;
    });
  });
  let lastScrolledHighlightKey = "";
  let lastActiveRowIndex = 0;
  let focusFrame = 0;

  createEffect(() => {
    const current = activeRowId();
    const focusable = resourceTableFocusableRows(rows());
    const currentIndex = focusable.findIndex((row) => row.id === current);
    if (currentIndex >= 0) {
      lastActiveRowIndex = currentIndex;
      return;
    }
    setActiveRowId(reconcileResourceTableFocus(
      focusable,
      current,
      lastActiveRowIndex));
  });

  createEffect(() => {
    const rowCount = rows().length;
    if (!viewport || rowCount === 0) {
      return;
    }
    const target = Math.min(
      scrollTop(),
      Math.max(0, rowCount * rowHeight - viewportHeight()));
    if (viewportScrollTop !== target) {
      viewport.scrollTop = target;
      viewportScrollTop = target;
    }
    if (scrollTop() !== target) {
      setScrollTop(target);
    }
  });

  createEffect(() => {
    const highlightedSoftwareId = props.highlightedSoftwareId;
    if (!viewport || !highlightedSoftwareId || props.mode !== "process") {
      return;
    }

    const rowIndex = rows().findIndex((row) => row.kind === "process" && row.softwareId === highlightedSoftwareId);
    if (rowIndex < 0) {
      return;
    }

    const key = `${highlightedSoftwareId}:${rowIndex}`;
    if (lastScrolledHighlightKey === key) {
      return;
    }

    lastScrolledHighlightKey = key;
    const nextScrollTop = Math.max(0, rowIndex * rowHeight - rowHeight * 2);
    viewport.scrollTop = nextScrollTop;
    viewportScrollTop = nextScrollTop;
    setScrollTop(nextScrollTop);
  });

  onMount(() => {
    const MIN_VIEWPORT_HEIGHT = 180;
    let size: { height: number; width: number; scrollbar: number } | null = null;
    const layout = observeLayout({
      read: () => {
        if (!viewport) return;
        const height = viewport.clientHeight;
        const width = viewport.clientWidth;
        const outerWidth = viewport.offsetWidth;
        size = { height: Math.max(MIN_VIEWPORT_HEIGHT, height), width: Math.max(0, width), scrollbar: Math.max(0, outerWidth - width) };
      },
      write: () => {
        if (!size) return;
        const measured = size;
        batch(() => {
          setViewportHeight(measured.height);
          setViewportWidth(measured.width);
          setScrollbarWidth(measured.scrollbar);
        });
      }
    });
    if (viewport) layout.update([viewport]);
    onCleanup(() => layout.dispose());
    onCleanup(() => {
      if (focusFrame) {
        cancelAnimationFrame(focusFrame);
      }
    });
  });

  const activateRow = (rowId: string, focus: boolean) => {
    const rowIndex = rows().findIndex((row) => row.id === rowId);
    if (rowIndex < 0) {
      return;
    }
    const focusableIndex = resourceTableFocusableRows(rows()).findIndex((row) => row.id === rowId);
    if (focusableIndex >= 0) {
      lastActiveRowIndex = focusableIndex;
    }
    setActiveRowId(rowId);
    if (!focus || !viewport) {
      return;
    }

    const rowTop = rowIndex * rowHeight;
    const rowBottom = rowTop + rowHeight;
    let nextScrollTop = viewport.scrollTop;
    if (rowTop < viewport.scrollTop) {
      nextScrollTop = rowTop;
    } else if (rowBottom > viewport.scrollTop + viewport.clientHeight) {
      nextScrollTop = Math.max(0, rowBottom - viewport.clientHeight);
    }
    if (nextScrollTop !== viewport.scrollTop) {
      viewport.scrollTop = nextScrollTop;
      viewportScrollTop = nextScrollTop;
      setScrollTop(nextScrollTop);
    }

    queueMicrotask(() => {
      if (!viewport) {
        return;
      }
      if (focusFrame) {
        cancelAnimationFrame(focusFrame);
      }
      focusFrame = requestAnimationFrame(() => {
        focusFrame = 0;
        const element = [...viewport!.querySelectorAll<HTMLElement>("[data-resource-row-id]")]
          .find((candidate) => candidate.dataset.resourceRowId === rowId);
        element?.focus({ preventScroll: true });
      });
    });
  };

  const moveActiveRow = (move: ResourceRowFocusMove) => {
    const next = moveResourceTableFocus(rows(), activeRowId(), move);
    if (next) {
      activateRow(next, true);
    }
  };

  const openRowContextMenu = (row: ResourceTableRow, element: HTMLElement) => {
    // Summary rows have no software identity and cannot open the menu.
    const softwareId = row.softwareId;
    if (!props.onSoftwareContextMenu || (row.kind !== "software" && row.kind !== "process") || !softwareId) {
      return;
    }
    const index = rows().findIndex((candidate) => candidate.id === row.id);
    if (index >= 0) {
      setPinnedRowId(row.id);
      setPinnedIndex(index);
    }
    props.onSoftwareContextMenu(
      contextMenuEventForElement(element),
      createSoftwareContextTargetFromRow(row, softwareId, props.mode, props.expandedSoftwareIds),
      element);
  };

  const handleRowKeyDown = (
    event: KeyboardEvent,
    row: ResourceTableRow,
    element: HTMLElement
  ) => {
    if (event.altKey || event.ctrlKey || event.metaKey) {
      return;
    }
    const move = event.key === "ArrowUp"
      ? "previous"
      : event.key === "ArrowDown"
        ? "next"
        : event.key === "Home"
          ? "first"
          : event.key === "End"
            ? "last"
            : null;
    if (move) {
      event.preventDefault();
      moveActiveRow(move);
      return;
    }
    const canExpand = props.mode === "software"
      && row.kind === "software"
      && Boolean(row.softwareId)
      && (row.processCount ?? 0) > 0;
    if (canExpand) {
      const expanded = props.expandedSoftwareIds[row.softwareId!] === true;
      const shouldToggle = event.key === " "
        || (event.key === "ArrowRight" && !expanded)
        || (event.key === "ArrowLeft" && expanded);
      if (shouldToggle) {
        event.preventDefault();
        props.onToggleExpand(row.softwareId!);
        return;
      }
    }
    if (event.key === "Enter"
      || event.key === "ContextMenu"
      || (event.shiftKey && event.key === "F10")) {
      event.preventDefault();
      openRowContextMenu(row, element);
    }
  };

  const updateSort = (column: ResourceTableColumn) => {
    if (column.sortable) props.onSortColumn(column.id);
  };

  return (
    <div
      class="resource-table-frame"
      // Use treegrid levels and row-based navigation for expandable software rows.
      role="treegrid"
      aria-label={uiText.resourceTable.panel}
      aria-rowcount={rows().length + 1}
      aria-colcount={columns().length}
    >
      <div
        class="resource-table-header-clip"
        role="rowgroup"
        style={{ "--table-scrollbar-width": `${scrollbarWidth()}px` } satisfies CssVars}
      >
        <div
          class="resource-table-header"
          role="row"
          aria-rowindex={1}
          style={{
            "grid-template-columns": gridTemplate(),
            width: `${tableWidth()}px`,
            transform: `translateX(${-scrollLeft()}px)`
          }}
        >
          <For each={columnIds()}>
            {(columnId, columnIndex) => {
              const column = () => columnById(columnId);
              const storedWidth = () => clampColumnWidth(baseColumns()[columnIndex()].width);
              let header: HTMLDivElement | undefined;
              let reorderRef: ((element: HTMLElement) => void) | undefined;
              const bindHeader = (ref: ((element: HTMLElement) => void) | undefined) => {
                reorderRef = ref;
                if (header) ref?.(header);
              };
              return (
              <div
                role="columnheader"
                aria-colindex={columnIndex() + 1}
                aria-sort={props.sortColumnId === columnId
                  ? props.sortDirection === "asc" ? "ascending" : "descending"
                  : "none"}
                class="resource-table-head-cell"
                classList={{
                  active: props.sortColumnId === columnId,
                  editing: props.editMode
                }}
                data-column-id={columnId}
                ref={element => { header = element; reorderRef?.(element); }}
              >
                {/* While editing, headers own reorder dragging and disable sorting; nested active controls retain their browser behavior. */}
                {/* Expose the full column name on sorting controls and the source explanation separately. */}
                <button
                  class="resource-table-sort-button"
                  type="button"
                  data-full-content={columnText(column()).label}
                  disabled={!column().sortable || props.editMode}
                  onClick={() => updateSort(column())}
                >
                  <span>{columnText(column()).label}</span>
                  {/* Place the GPU name below the indexed label without moving sort controls. */}
                  <GpuNameNote metricId={props.processFields.find(field => field.name === columnId)?.resourceFactTotalMetricId ?? columnId} names={props.gpuNames} />
                  {/* Reserve sort-arrow geometry; numeric labels remain aligned with their values. */}
                  <Show when={column().sortable}>
                    <ArrowDown
                      class={props.sortColumnId === columnId && props.sortDirection === "asc"
                        ? "resource-table-sort-indicator ascending"
                        : "resource-table-sort-indicator"}
                      aria-hidden="true"
                      size={12}
                      strokeWidth={2.4}
                    />
                  </Show>
                </button>
                <Show when={columnText(column()).basis}>
                  {(basis) => <HelpHint>{basis()}</HelpHint>}
                </Show>
                <Show when={props.editMode}>
                  <span class="resource-table-column-order-actions">
                    <button
                      class="resource-table-column-order-button"
                      type="button"
                      disabled={columnIndex() === 0}
                      aria-label={uiText.resourceTableView.moveColumnLeft(columnText(column()).label)}
                      data-name-tooltip=""
                      onClick={() => {
                        const previous = columns()[columnIndex() - 1];
                        if (previous) {
                          props.onReorderColumn(columnId, previous.id);
                        }
                      }}
                    >
                      <ArrowLeft aria-hidden="true" size={13} />
                    </button>
                    <button
                      class="resource-table-column-order-button"
                      type="button"
                      disabled={columnIndex() === columns().length - 1}
                      aria-label={uiText.resourceTableView.moveColumnRight(columnText(column()).label)}
                      data-name-tooltip=""
                      onClick={() => {
                        const next = columns()[columnIndex() + 1];
                        if (next) {
                          props.onReorderColumn(next.id, columnId);
                        }
                      }}
                    >
                      <ArrowRight aria-hidden="true" size={13} />
                    </button>
                  </span>
                </Show>
                <Show when={props.editMode} fallback={
                  <span class="resource-table-column-resizer" role="separator" tabIndex={-1}
                    aria-orientation="vertical" aria-label={uiText.resourceTableView.resizeColumn(columnText(column()).label)}
                    aria-valuemin={minColumnWidth} aria-valuemax={maxColumnWidth}
                    aria-valuenow={storedWidth()} aria-disabled="true" />
                }>
                  <ResourceTableColumnResizer
                    columnId={columnId}
                    label={columnText(column()).label}
                    storedWidth={storedWidth}
                    rightEdge={width => columnRightEdge(columnIndex(), width)}
                    widthForEdge={edge => columnWidthForEdge(columnIndex(), edge)}
                    onWidthChange={props.onColumnWidthChange}
                    onFit={() => {
                      const canvas = document.createElement("canvas");
                      const context = canvas.getContext("2d");
                      if (!context || !viewport) return;
                      context.font = getComputedStyle(viewport.querySelector(".resource-table-cell-text") ?? viewport).font;
                      const texts = [columnText(column()).label, ...rows().map(row => columnId === "name" ? rowName(row)
                        : columnId === "status" ? rowStatus(row) : resourceTableCellText(row.values[columnId], columnId))];
                      props.onColumnWidthChange(columnId, fitTableColumnWidth(texts, text => context.measureText(text).width), true);
                    }}
                    reorder={columnReorder}
                    bindHeader={bindHeader}
                  />
                </Show>
              </div>
              );
            }}
          </For>
        </div>
      </div>
      <div
        class="resource-table-viewport"
        role="rowgroup"
        ref={viewport}
        onScroll={(event) => {
          if (viewport) settleEntrancesWithin(viewport);
          viewportScrollTop = event.currentTarget.scrollTop;
          if (rows().length > 0) {
            setScrollTop(event.currentTarget.scrollTop);
          }
          setScrollLeft(event.currentTarget.scrollLeft);
        }}
      >
        <div class="resource-table-spacer" style={{ height: `${rows().length * rowHeight}px`, width: `${tableWidth()}px` }}>
          <For each={slots()}>
            {(slot) => {
              const rowIndex = () => visibleRange().start + slot;
              const presentation = () => rowPresentations()[slot];
              const row = () => presentation()?.row;
              return (
                <ResourceTableRowSlot
                  row={row}
                  presentation={presentation}
                  columnIds={columnIds}
                  columnById={columnById}
                  gridTemplate={gridTemplate}
                  rowIndex={rowIndex}
                  renderContents={(rowIndex() >= firstRenderedRow() && rowIndex() < lastRenderedRow())
                    || activeRowId() === row()?.id}
                  mode={props.mode}
                  expandedSoftwareIds={() => props.expandedSoftwareIds}
                  highlightedSoftwareId={props.highlightedSoftwareId}
                  activeRowId={activeRowId}
                  contextRowId={pinnedRowId}
                  onActivateRow={activateRow}
                  onRowKeyDown={handleRowKeyDown}
                  onToggleExpand={props.onToggleExpand}
                  onSoftwareContextMenu={props.onSoftwareContextMenu}
                  onRowContextMenu={openRowContextMenu}
                />
              );
            }}
          </For>
        </div>
      </div>
    </div>
  );
}

/** Only editing owns resize geometry and a controller. */
function ResourceTableColumnResizer(props: {
  columnId: string;
  label: string;
  storedWidth: () => number;
  rightEdge: (width: number) => number;
  widthForEdge: (edge: number) => number;
  onWidthChange: ResourceTableProps["onColumnWidthChange"];
  onFit: () => void;
  reorder: ReorderGroup;
  bindHeader: (ref: ((element: HTMLElement) => void) | undefined) => void;
}) {
  let element!: HTMLSpanElement;
  let widthBeforeDrag: number | null = null;
  props.bindHeader(props.reorder.item({ id: () => props.columnId, enabled: () => true }));
  onCleanup(() => props.bindHeader(undefined));
  useInteraction(`列宽把手「${props.columnId}」`, columnResizeHandleDefinition,
    () => columnResizeView(element, () => true), solidHost<number>({
      value: () => props.rightEdge(props.storedWidth()),
      enabled: () => true,
      call: (port, args) => {
        if (port === "resize") {
          const final = args[1] === true;
          if (!final && widthBeforeDrag === null) widthBeforeDrag = props.storedWidth();
          props.onWidthChange(props.columnId, props.widthForEdge(Number(args[0])), final);
          if (final) widthBeforeDrag = null;
        } else if (port === "step") {
          props.onWidthChange(props.columnId, clampColumnWidth(props.storedWidth() + Number(args[0]) * columnResizeKeyStep), true);
        } else if (port === "cancel" && widthBeforeDrag !== null) {
          props.onWidthChange(props.columnId, widthBeforeDrag, true);
          widthBeforeDrag = null;
        }
      }
    }));
  return <span ref={element} class="resource-table-column-resizer" role="separator" tabIndex={0}
    onDblClick={event => { event.preventDefault(); event.stopPropagation(); props.onFit(); }}
    onKeyDown={event => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault(); event.stopPropagation(); props.onFit();
      }
      if (event.key === "Home" || event.key === "End") {
        event.preventDefault(); event.stopPropagation();
        props.onWidthChange(props.columnId, event.key === "Home" ? minColumnWidth : maxColumnWidth, true);
      }
    }}
    aria-orientation="vertical" aria-label={uiText.resourceTableView.resizeColumn(props.label)}
    aria-valuemin={minColumnWidth} aria-valuemax={maxColumnWidth} aria-valuenow={props.storedWidth()} aria-disabled="false" />;
}

function ResourceTableRowSlot(props: {
  row: () => ResourceTableRow | undefined;
  presentation: () => ResourceTableRowPresentation | undefined;
  columnIds: () => string[];
  columnById: (id: string) => ResourceTableColumn;
  gridTemplate: () => string;
  rowIndex: () => number;
  renderContents: boolean;
  mode: ResourceTableViewMode;
  expandedSoftwareIds: () => Record<string, boolean>;
  highlightedSoftwareId?: string | null;
  activeRowId: () => string | null;
  /** Mark the context-menu row as current until the menu closes. */
  contextRowId: () => string | null;
  onActivateRow: (rowId: string, focus: boolean) => void;
  onRowKeyDown: (event: KeyboardEvent, row: ResourceTableRow, element: HTMLElement) => void;
  onToggleExpand: (softwareId: string) => void;
  onSoftwareContextMenu?: SoftwareContextMenuHandler;
  /** Route context-menu and more-action input through the same pinning entry point. */
  onRowContextMenu: (row: ResourceTableRow, element: HTMLElement) => void;
}) {
  const row = () => props.row();
  const name = () => props.presentation()?.name ?? "";
  const hasRow = () => row() !== undefined;
  const isHighlighted = () => Boolean(
    props.highlightedSoftwareId
    && row()?.softwareId === props.highlightedSoftwareId
    && (row()?.kind === "process" || row()?.kind === "software"));
  const isExpandable = () => props.mode === "software"
    && row()?.kind === "software"
    && Boolean(row()?.softwareId)
    && (row()?.processCount ?? 0) > 0;
  return (
    <div
      class="resource-table-row"
      role="row"
      data-resource-row-id={row()?.id}
      data-resource-software-id={row()?.softwareId}
      aria-rowindex={props.rowIndex() + 2}
      aria-label={name()}
      aria-level={hasRow() ? (row()?.depth ?? 0) + 1 : undefined}
      aria-expanded={isExpandable()
        ? props.expandedSoftwareIds()[row()?.softwareId ?? ""] === true
          ? "true"
          : "false"
        : undefined}
      tabIndex={hasRow() && row()?.id === props.activeRowId() ? 0 : -1}
      classList={{
        "process-row": row()?.kind === "process",
        "software-row": row()?.kind === "software",
        "summary-row": row()?.kind === "summary",
        // Use backend displayKind for software, process, and attribution rows; software IDs are opaque.
        "residual-row": isReservedSegment(row() ?? {}),
        "filtered-row": row()?.kind === "filtered",
        "attribution-row": row()?.kind === "attribution",
        "empty-row": !hasRow(),
        highlighted: isHighlighted(),
        "context-target": hasRow() && row()?.id === props.contextRowId()
      }}
      aria-hidden={hasRow() ? "false" : "true"}
      onFocus={() => {
        const current = row();
        if (current) {
          props.onActivateRow(current.id, false);
        }
      }}
      onPointerDown={() => {
        const current = row();
        if (current) {
          props.onActivateRow(current.id, false);
        }
      }}
      onKeyDown={(event) => {
        const current = row();
        if (current) {
          props.onRowKeyDown(event, current, event.currentTarget);
        }
      }}
      onContextMenu={(event) => {
        const current = row();
        if (!current) {
          return;
        }

        event.preventDefault();
        props.onRowContextMenu(current, event.currentTarget);
      }}
      style={{
        // Keep column-width custom properties local to this grid.
        "grid-template-columns": props.gridTemplate(),
        // Retain current DOM values in every pool slot.
        // Only clipped overscan descendants skip rendering.
        // Enable them synchronously on scroll or focus without reading geometry.
        "content-visibility": props.renderContents ? "visible" : "hidden",
        transform: `translateY(${props.rowIndex() * rowHeight}px)`,
        visibility: hasRow() ? "visible" : "hidden",
        "pointer-events": hasRow() ? "auto" : "none"
      }}
    >
      <For each={props.columnIds()}>
        {(columnId, columnIndex) => {
          return <ResourceTableCell
            column={props.columnById(columnId)}
            presentation={() => props.presentation()?.cells.get(columnId) ?? emptyCellPresentation}
            columnIndex={columnIndex()}
            onRowContextMenu={props.onRowContextMenu}
            row={row}
            rowName={name}
            rowStatus={() => props.presentation()?.status ?? ""}
            mode={props.mode}
            expandedSoftwareIds={props.expandedSoftwareIds}
            onToggleExpand={props.onToggleExpand}
            onSoftwareContextMenu={props.onSoftwareContextMenu}
          />;
        }}
      </For>
    </div>
  );
}

function resourceTableEditorColumns(
  settings: ResourceTableColumnSettings[],
  catalog: MetricDefinition[],
  mode: ResourceTableViewMode
): ResourceTableColumn[] {
  const available = resourceTableColumnOptions(catalog, mode);
  const availableById = new Map(available.map((column) => [column.id, column]));
  const configured = settings
    .map((setting) => availableById.get(setting.id) ?? { ...setting, unit: "", sortable: true,
      width: setting.width ?? ResourceManager.FrontendPoints.DashboardLayoutLimits.defaultColumnWidth,
      source: setting.source ?? null, processFieldName: null, additiveAcrossProcesses: null })
    .filter((column): column is ResourceTableColumn => Boolean(column));
  return configured;
}

function createSoftwareContextTargetFromRow(
  row: ResourceTableRow,
  softwareId: string,
  mode: ResourceTableViewMode,
  expandedSoftwareIds: Record<string, boolean>): SoftwareContextMenuTarget
{
  const processIds = row.kind === "process" && row.processId
    ? [row.processId]
    : row.processIds ?? [];
  // Use resolved names for group-row confirmations
  // because the backend may omit group names.
  const displayName = rowName(row);
  return {
    kind: row.kind === "process" ? "process" : "software",
    name: row.kind === "process" ? row.name : displayName,
    softwareId,
    softwareName: row.softwareName ?? (row.kind === "software" ? displayName : undefined),
    processIds,
    processTargets: row.kind === "process"
      && row.processId
      && row.processStartKey
        ? [{
            processId: row.processId,
            processStartKey: row.processStartKey
          }]
        : [],
    processNames: row.processNames ?? [],
    executablePaths: row.executablePaths ?? [],
    canExpand: mode === "software" && row.kind === "software" && (row.processCount ?? 0) > 0,
    expanded: expandedSoftwareIds[softwareId] === true
  };
}

function projectResourceTableRows(
  rows: ResourceTableRow[],
  mode: ResourceTableViewMode,
  expandedSoftwareIds: Record<string, boolean>,
  query: string,
  searchKeyCache: Map<string, RowSearchKeyCacheEntry>)
{
  const summaryRows = rows.filter((row) => row.kind === "summary");
  const dataRows = rows.filter((row) => row.kind !== "summary");
  const normalizedQuery = normalizeSearchText(query);
  if (normalizedQuery) {
    pruneSearchKeyCache(searchKeyCache, dataRows);
  }
  if (mode === "process") {
    const projected = normalizedQuery
      ? dataRows.filter((row) => resourceTableRowMatches(row, normalizedQuery, searchKeyCache))
      : dataRows;
    return [...summaryRows, ...projected];
  }

  if (!normalizedQuery) {
    return [...summaryRows, ...dataRows.filter((row) =>
      row.kind !== "process"
      || (row.softwareId ? expandedSoftwareIds[row.softwareId] : false))];
  }

  const matchedRowIds = new Set<string>();
  const matchedSoftwareIds = new Set<string>();
  const contextSoftwareIds = new Set<string>();
  for (const row of dataRows) {
    if (!resourceTableRowMatches(row, normalizedQuery, searchKeyCache)) {
      continue;
    }

    matchedRowIds.add(row.id);
    if (row.kind === "software" && row.softwareId) {
      matchedSoftwareIds.add(row.softwareId);
    } else if (row.kind === "process" && row.softwareId) {
      contextSoftwareIds.add(row.softwareId);
    }
  }

  return [...summaryRows, ...dataRows.filter((row) => {
    const softwareId = row.softwareId;
    if (row.kind === "software") {
      return matchedRowIds.has(row.id)
        || (softwareId !== undefined && softwareId !== null && contextSoftwareIds.has(softwareId));
    }

    if (row.kind !== "process" || !softwareId || !expandedSoftwareIds[softwareId]) {
      return false;
    }

    return matchedRowIds.has(row.id) || matchedSoftwareIds.has(softwareId);
  })];
}

function resourceTableRowMatches(
  row: ResourceTableRow,
  normalizedQuery: string,
  cache: Map<string, RowSearchKeyCacheEntry>)
{
  return resourceTableRowSearchKey(row, cache).includes(normalizedQuery);
}

function resourceTableRowSearchKey(
  row: ResourceTableRow,
  cache: Map<string, RowSearchKeyCacheEntry>)
{
  const parts = [
    row.id,
    row.name,
    row.status,
    row.softwareId ?? "",
    row.processId?.toString() ?? "",
    resourceTableCellText(row.values?.pid, "pid"),
    resourceTableCellText(row.values?.user, "user"),
    resourceTableCellText(row.values?.architecture, "architecture")
  ];
  const signature = parts.join("\u0000");
  const cached = cache.get(row.id);
  if (cached?.signature === signature) {
    return cached.key;
  }

  const key = normalizeSearchText(parts.join(" "));
  cache.set(row.id, { signature, key });
  return key;
}

function pruneSearchKeyCache(
  cache: Map<string, RowSearchKeyCacheEntry>,
  rows: ResourceTableRow[])
{
  if (cache.size <= rows.length * 2 + slotReserve) {
    return;
  }

  const liveIds = new Set(rows.map((row) => row.id));
  for (const rowId of cache.keys()) {
    if (!liveIds.has(rowId)) {
      cache.delete(rowId);
    }
  }
}

function normalizeSearchText(value: string) {
  return value.trim().toLocaleLowerCase();
}

function ResourceTableCell(props: {
  column: ResourceTableColumn;
  presentation: () => ResourceTableCellPresentation;
  columnIndex: number;
  row: () => ResourceTableRow | undefined;
  rowName: () => string;
  rowStatus: () => string;
  mode: ResourceTableViewMode;
  expandedSoftwareIds: () => Record<string, boolean>;
  onToggleExpand: (softwareId: string) => void;
  onSoftwareContextMenu?: SoftwareContextMenuHandler;
  onRowContextMenu: (row: ResourceTableRow, element: HTMLElement) => void;
}) {
  const row = () => props.row();
  const columnId = props.column.id;
  if (columnId === "name") {
    return (
      <div
        class="resource-table-cell name-cell"
        classList={{ indented: row()?.depth === 1 }}
        role="gridcell"
        aria-colindex={props.columnIndex + 1}
      >
        {/* Key the expand button by row identity so recycled rows do not animate the previous arrow state. */}
        <Show
          when={row()?.kind === "software" && row()?.softwareId && (row()?.processCount ?? 0) > 0 ? row()!.id : undefined}
          keyed
          // Reserve arrow space for software-level rows only.
          fallback={props.mode === "software" ? <span class="resource-row-spacer" /> : undefined}
        >
          <button
            type="button"
            class="resource-row-expander"
            tabIndex={-1}
            onClick={() => row()?.softwareId && props.onToggleExpand(row()!.softwareId!)}
            aria-label={props.expandedSoftwareIds()[row()?.softwareId ?? ""] ? uiText.resourceTable.collapseProcesses : uiText.resourceTable.expandProcesses}
            aria-expanded={props.expandedSoftwareIds()[row()?.softwareId ?? ""] ? "true" : "false"}
          >
            {/* Static Lucide geometry (ISC, included in credits) avoids per-row component trees. */}
            <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24"
              fill="none" stroke="currentColor" stroke-width="2.25" stroke-linecap="round" stroke-linejoin="round"
              class="lucide lucide-icon lucide-chevron-right" aria-hidden="true">
              <path d="m9 18 6-6-6-6" />
            </svg>
          </button>
        </Show>
        <span class="resource-row-name" data-full-content={props.rowName()}>{props.rowName()}</span>
        <Show when={props.onSoftwareContextMenu && (row()?.kind === "software" || row()?.kind === "process")}>
          <button
            type="button"
            class="resource-row-actions"
            tabIndex={-1}
            aria-label={uiText.resourceTableView.moreActions(props.rowName() || uiText.resourceTableView.currentItem)}
            data-name-tooltip=""
            data-focus-key={`resource-row-actions:${row()?.id ?? "unknown"}`}
            onClick={(event) => openRowActions(event.currentTarget)}
            onKeyDown={(event) => {
              if (event.key === "ContextMenu" || (event.shiftKey && event.key === "F10")) {
                event.stopPropagation();
              }
            }}
            onContextMenu={(event) => {
              event.preventDefault();
              event.stopPropagation();
              openRowActions(event.currentTarget);
            }}
          >
            <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24"
              fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"
              class="lucide lucide-icon lucide-ellipsis" aria-hidden="true">
              <circle cx="12" cy="12" r="1" />
              <circle cx="19" cy="12" r="1" />
              <circle cx="5" cy="12" r="1" />
            </svg>
          </button>
        </Show>
      </div>
    );
  }

  if (columnId === "status") {
    return (
      <div class="resource-table-cell status-cell" role="gridcell" aria-colindex={props.columnIndex + 1} data-full-content={props.rowStatus()}>
        <span class="resource-table-cell-text">{props.rowStatus()}</span>
      </div>
    );
  }

  if (columnId === "pid" || columnId === "user" || columnId === "architecture") {
    return (
      <div class="resource-table-cell text-cell" role="gridcell" aria-colindex={props.columnIndex + 1} data-full-content={props.presentation().display}>
        <span class="resource-table-cell-text">{props.presentation().display}</span>
      </div>
    );
  }

  const presentation = props.presentation;
  return (
    <div
      class="resource-table-cell value-cell"
      role="gridcell"
      aria-colindex={props.columnIndex + 1}
      classList={{ unavailable: presentation().unavailable }}
      style={{ "--heat": `${presentation().heat}%`, "--private-heat": `${presentation().privateHeat}%` } satisfies CssVars}
      data-full-content={presentation().title}
    >
      <span class="resource-table-cell-text">{presentation().display}</span>
    </div>
  );

  function openRowActions(element: HTMLElement) {
    const current = row();
    if (current) {
      props.onRowContextMenu(current, element);
    }
  }
}

function contextMenuEventForElement(element: HTMLElement) {
  const rect = element.getBoundingClientRect();
  return new MouseEvent("contextmenu", {
    clientX: Math.min(window.innerWidth - 8, rect.right),
    clientY: Math.min(window.innerHeight - 8, rect.bottom),
    bubbles: false,
    cancelable: true
  });
}

function resourceTableColumnOptions(catalog: MetricDefinition[], mode: ResourceTableViewMode = "software"): ResourceTableColumn[] {
  const defaults = dashboardPointValues(catalog);
  const settings = mode === "process" ? defaults.resourceTableProcessColumns : defaults.resourceTableColumns;
  return settings.map(setting => ({ ...setting, unit: "", sortable: true,
    width: setting.width!, source: setting.source ?? null, processFieldName: null, additiveAcrossProcesses: null }));
}
/** The resize channel measures the column edge in screen pixels relative to the header; the table owns rendered and accessible widths. */
function columnResizeView(handle: HTMLElement, editing: () => boolean): Presentation {
  return {
    element: handle,
    bindingLabel: "右边缘位置",
    geometry: () => {
      // Outside editing, host width changes need no screen-to-channel conversion.
      if (!editing()) return null;
      const header = handle.closest(".resource-table-header");
      const cell = handle.parentElement;
      if (!header || !cell) return null;
      const left = header.getBoundingClientRect().left;
      const rect = cell.getBoundingClientRect();
      const y = (rect.top + rect.bottom) / 2;
      return { center: "把手中线（列的右边缘）", atMin: { x: left - columnEdgeBound, y }, atMax: { x: left + columnEdgeBound, y } };
    },
    render: () => {},
    aria: (edge) => `右边缘 ${edge} px，宽度见 aria-valuenow`
  };
}
function clampColumnWidth(value: number) {
  return clampResourceTableColumnWidth(value);
}

// The state column holds sampling, group, or process state according to row kind.
function rowStatus(row: ResourceTableRow | undefined) {
  const value = String(row?.status ?? "").trim();
  if (row?.kind === "summary") {
    if (value === "empty") return uiText.status.noData;
    const copy = uiText.resourceTable.summaryRow.status as Record<string, string | undefined>;
    return copy[value] ?? uiText.resourceTableView.unknownStatus;
  }

  // Classify reserved attribution leaves by owner; they share reserved-row state.
  if (row?.kind === "process" && isReservedSegment(row)) {
    return uiText.softwareKind.system_residual;
  }

  if (value === "running") {
    return uiText.status.state.running;
  }

  // Use system-residual attribution state for reserved software and its leaves.
  if (value === "system-residual") {
    return uiText.softwareKind.system_residual;
  }

  return softwareGroupLabel(value)
    ?? userFacingLabel(row?.status, uiText.resourceTableView.unknownStatus);
}

// Localize known backend display kinds; return undefined otherwise.
function softwareGroupLabel(value?: string | null) {
  const group = String(value ?? "").trim() as keyof typeof uiText.softwareKind;
  return uiText.softwareKind[group];
}
