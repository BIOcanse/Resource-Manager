import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { observeLayout } from "../../interactions/solid/layoutObservation.ts";
import {
  resourceTableCellPresentation,
  resourceTableCellText,
  type ResourceTableCellPresentation,
  resourceTableColumnLabel,
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

const rowHeight = 38;
const overscan = 6;
const slotReserve = 10;
const minColumnWidth = 56;
const maxColumnWidth = 520;
const columnResizeKeyStep = 12;
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
  /** 后端编号的显卡各自的名字（monitorStore.gpuNames）：GPU 列的表头和芯片在编号旁用小字写出。 */
  gpuNames: ReadonlyMap<string, string>;
  sortColumnId: string;
  sortDirection: string;
  expandedSoftwareIds: Record<string, boolean>;
  highlightedSoftwareId?: string | null;
  onModeChange: (mode: ResourceTableViewMode) => void;
  onToggleEdit: () => void;
  onCancelEdit: () => void;
  /** 点了某列表头；排序怎么变由 store 决定（resourceTableSort.ts）。 */
  onSortColumn: (columnId: string) => void;
  onToggleColumn: (columnId: string, visible: boolean) => void;
  /** 后端进程字段目录：列的来源选择和选了来源后的表头口径。 */
  processFields: readonly ProcessMetricField[];
  onColumnSourceChange: (columnId: string, source: string | null) => void;
  onColumnWidthChange: (columnId: string, width: number, commit: boolean) => void;
  onToggleExpand: (softwareId: string) => void;
  onSoftwareContextMenu?: SoftwareContextMenuHandler;
  /** 有右键菜单打开时，被右键的那一行钉住不动。 */
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
  // Heat is each mounted cell's share of its column maximum over the shown rows; rows and cells are not copied for it.
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
  // 必须每次读，不能在组件建立时固化，否则切语言后这三个标签不会更新。
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
  /** 当前快照的列：自动选择的列带着后端这次实际读的字段。 */
  snapshotColumns: readonly ResourceTableColumn[];
  onToggle: (columnId: string, visible: boolean) => void;
  onSourceChange: (columnId: string, source: string | null) => void;
}) {
  const byId = () => new Map(props.columns.map((column) => [column.id, column]));
  // 自动选择这一项写出后端这次实际读的字段：只有这一列现在就是自动选择时快照里的字段才是它。
  const automaticField = (columnId: string) => {
    const column = props.snapshotColumns.find((candidate) => candidate.id === columnId);
    return column && column.source === null ? column.processFieldName : null;
  };
  const editorColumns = createMemo(() => resourceTableEditorColumns(props.columns, props.catalog, props.mode));
  // 按列 id 排芯片：列对象每次重算都是新的，按对象排会在每次勾选后重建芯片，键盘焦点随之丢失。
  const editorColumnIds = createMemo(() => editorColumns().map((column) => column.id));
  const editorColumnById = createMemo(() => new Map(editorColumns().map((column) => [column.id, column])));
  // 先选项目、再选来源：点芯片的方格子只切换这一列显示与否；点芯片其他位置是选中这一列（同时只有一个，进入编辑时没有），
  // 下面只摆选中列的“来源”，第一项是自动选择；选中的列没有来源可选时不摆来源行。再点一次选中的芯片取消选中。
  const [selectedColumnId, setSelectedColumnId] = createSignal<string | null>(null);
  // 来源行属于哪一列：选中的列还在编辑栏里、而且有来源可选时就是它，否则不摆来源行。
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
      // 左右方向键在芯片之间移动焦点，只移动、不选中；Home/End 到头尾。
      onKeyDown={(event) => {
        if ((event.target as Element).classList.contains("resource-table-column-select")) {
          moveRovingFocus(event, { selector: ".resource-table-column-select", previousKeys: ["ArrowLeft"], nextKeys: ["ArrowRight"] });
        }
      }}
    >
      <For each={editorColumnIds()}>
        {(columnId) => {
          const label = () => resourceTableColumnLabel(columnId);
          const selected = () => selectedColumnId() === columnId;
          return (
            // 芯片里除了方格子都是“选中”：选中按钮之外的内边距也算，按钮的点击（含键盘 Enter/空格）冒泡到这里处理。
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
                <GpuNameNote metricId={columnId} names={props.gpuNames} />
              </button>
            </div>
          );
        }}
      </For>
      <Show when={sourceColumnId()} keyed>
        {(columnId) => {
          const choices = () => resourceColumnSourceChoices(columnId, props.processFields);
          const label = () => uiText.resourceTable.columnSourceLabel(resourceTableColumnLabel(columnId));
          const current = () => byId().get(columnId)?.source ?? null;
          return (
            <div class="resource-table-column-source">
              <strong>{label()}<GpuNameNote metricId={columnId} names={props.gpuNames} /></strong>
              <RadioGroupRoot class="segmented resource-table-column-source-choices" ariaLabel={label()}
                // 存的来源不在目录里（目录变了）时不勾任何一项，不把它显示成自动选择。
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
  /** Column maxima of rows (resourceTableHeat.ts); each cell's heat is its share of its column's maximum. */
  heatMaxima: ResourceTableHeatMaxima;
  snapshot: ResourceTableSnapshot | null;
  columnSettings: ResourceTableColumnSettings[];
  processFields: readonly ProcessMetricField[];
  gpuNames: ReadonlyMap<string, string>;
  sortColumnId: string;
  sortDirection: string;
  expandedSoftwareIds: Record<string, boolean>;
  highlightedSoftwareId?: string | null;
  /** 点了某列表头；排序怎么变由 store 决定（resourceTableSort.ts）。 */
  onSortColumn: (columnId: string) => void;
  onColumnWidthChange: (columnId: string, width: number, commit: boolean) => void;
  onToggleExpand: (softwareId: string) => void;
  onSoftwareContextMenu?: SoftwareContextMenuHandler;
  contextMenuOpen?: boolean;
  onReorderColumn: (sourceColumnId: string, targetColumnId: string) => void;
}) {
  let viewport: HTMLDivElement | undefined;
  // 视口实际的滚动位置，由滚动事件和本组件的写入维护。每秒的数据更新只和它比较，不读 DOM（读会强制同步布局）。
  let viewportScrollTop = 0;
  const [scrollTop, setScrollTop] = createSignal(0);
  const [scrollLeft, setScrollLeft] = createSignal(0);
  const [viewportHeight, setViewportHeight] = createSignal(360);
  const [viewportWidth, setViewportWidth] = createSignal(0);
  const [scrollbarWidth, setScrollbarWidth] = createSignal(0);
  const [activeRowId, setActiveRowId] = createSignal<string | null>(null);
  // 被右键的那一行钉在它当时所在的位置；菜单关掉就松开。
  const [pinnedRowId, setPinnedRowId] = createSignal<string | null>(null);
  const [pinnedIndex, setPinnedIndex] = createSignal<number | null>(null);
  createEffect(() => {
    if (!props.contextMenuOpen) {
      setPinnedRowId(null);
      setPinnedIndex(null);
    }
  });
  const rows = createMemo(() => pinResourceTableRow(props.rows, pinnedRowId(), pinnedIndex()));
  // 展开、收起：交互上当场就是新的行（行位换成别的行，焦点、aria-expanded 都在这一刻改好）；画面交给呈现层
  // （presentation/nodeAnimation.ts，docs/resource_table_rows_2026-10-25.md）。
  // 收起：换之前（createComputed，DOM 还没改），还显示着的、属于收起的组的进程行各留一份幽灵，播放行的退出槽位。
  // 展开：换之后（createEffect），刚露出来的进程行播放进场槽位 row；滚动时正在播的直接到终态，滚动换进行位的行不播。
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

  // 编辑时按住表头格拖到另一列上换位；里面的列宽把手是内层组件，按在它上面由它处理。
  const columnReorder = createReorderGroup("表头列", (source, target) => props.onReorderColumn(source, target));
  const baseTableWidth = createMemo(() => baseColumns().reduce((sum, column) => sum + clampColumnWidth(column.width), 0));
  const tableWidth = createMemo(() => Math.max(baseTableWidth(), viewportWidth()));
  const columnWidths = createMemo(() => distributeColumnWidths(baseColumns(), tableWidth()));
  const columns = createMemo<ResourceTableColumn[]>((previous) => {
    const next = baseColumns().map((column, index) => ({
      ...column,
      width: columnWidths()[index] ?? column.width
    }));
    return resourceTableColumnsEqual(previous, next) ? previous : next;
  }, []);
  const columnIds = createMemo(() => baseColumns().map((column) => column.id));
  /** 第 index 列存的宽度改成 width 时，它的右边缘离表头左边缘多远（多余宽度照常分给各列）。 */
  const columnRightEdge = (index: number, width: number) => {
    const resized = baseColumns().map((column, at) => at === index ? { ...column, width } : column);
    const total = resized.reduce((sum, column) => sum + clampColumnWidth(column.width), 0);
    return distributeColumnWidths(resized, Math.max(total, viewportWidth()))
      .slice(0, index + 1)
      .reduce((sum, columnWidth) => sum + columnWidth, 0);
  };
  /**
   * 右边缘要到 edge 时第 index 列存多宽：右边缘随存的宽度单调不减，取到得了 edge 的最小宽度，限制在最小、最大宽度之间。
   * 最后一列铺满前右边缘不动，这时一拖过右边缘就取到刚好不再分多余宽度的那个宽度。整数二分，最多十来步。
   */
  const columnWidthForEdge = (index: number, edge: number) => {
    let low = minColumnWidth;
    let high = maxColumnWidth;
    while (low < high) {
      const middle = Math.floor((low + high) / 2);
      if (columnRightEdge(index, middle) >= edge) high = middle;
      else low = middle + 1;
    }
    return low;
  };
  // 表头、单元格提示用的列名和说明：选了来源的列写出口径，自动选择写出后端这次实际读的字段。
  // Width correction must not invalidate every cell's current text/reading.
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
    // Keep index-based reuse and overscan, but do not mount cells for rows that
    // do not exist. Short/empty results need no unused viewport-sized pool.
    return Array.from({ length: Math.min(rows().length, count) }, (_, index) => index);
  });
  // One synchronous projection for the mounted pool, rather than hundreds of
  // per-cell computation owners. Values/locale/range changes replace it now.
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
    // 汇总行没有软件标识，不开菜单；软件行和进程行都带软件标识（resourceFacts 解码时必填）。
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
      // 软件行可以展开出进程行，所以是树形表格：行带层级和展开状态，焦点按行移动（方向键、Home/End、左右展开收起）。
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
                {/* 编辑时整个表头格是拖动排序的把手，排序按钮停用：按下拖动时运行时捕获指针，浏览器把随后的 click 交给表头格，
                    按钮本来也收不到；停用的按钮算表头格的一部分，可用的按钮按在上面交给浏览器（interactions/solid/reorderGroup.ts）。 */}
                {/* 排序按钮的完整内容提示是列名本身：列窄、名字被截断时也看得到全名。口径说明（第一行就是列名和来源）在旁边的问号里。 */}
                <button
                  class="resource-table-sort-button"
                  type="button"
                  data-full-content={columnText(column()).label}
                  disabled={!column().sortable || props.editMode}
                  onClick={() => updateSort(column())}
                >
                  <span>{columnText(column()).label}</span>
                  {/* GPU 列：列名下面一行小字写这块显卡的名字（编号原样），排序箭头和列名的位置不变。 */}
                  <GpuNameNote metricId={columnId} names={props.gpuNames} />
                  {/* 排序箭头一直在（不占位置变化）：当前排序列按方向画出，其余可排序列悬停时淡出点下去会得到的降序。
                      数值列的箭头在列名左边，列名右边缘和下面的数字对齐。 */}
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

/** Only editing owns a resize controller or its geometry. */
function ResourceTableColumnResizer(props: {
  columnId: string;
  label: string;
  storedWidth: () => number;
  rightEdge: (width: number) => number;
  widthForEdge: (edge: number) => number;
  onWidthChange: ResourceTableProps["onColumnWidthChange"];
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
  /** 右键菜单打开时它所属的那一行（菜单关掉为 null）：这一行显示成当前行，看得出菜单是对谁的。 */
  contextRowId: () => string | null;
  onActivateRow: (rowId: string, focus: boolean) => void;
  onRowKeyDown: (event: KeyboardEvent, row: ResourceTableRow, element: HTMLElement) => void;
  onToggleExpand: (softwareId: string) => void;
  onSoftwareContextMenu?: SoftwareContextMenuHandler;
  /** 行上的右键与「更多操作」都走这一个入口，钉住逻辑只在那里。 */
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
        // 软件行、它的进程行和归属行都带后端的软件 displayKind；软件 id 不透明。
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
        // 原生属性只影响这个网格，不让列宽自定义属性继承到每个单元格的后代。
        "grid-template-columns": props.gridTemplate(),
        // The pool retains current DOM values in every slot. Only clipped
        // overscan descendants skip rendering; scrolling/focus enables them
        // synchronously, with one extra row at each edge and no geometry read.
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
    .map((setting) => availableById.get(setting.id))
    .filter((column): column is ResourceTableColumn => Boolean(column));
  const configuredIds = new Set(configured.map((column) => column.id));
  return configured.concat(available.filter((column) => !configuredIds.has(column.id)));
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
  // 只代表一个分组的行后端不发名字，菜单里的确认框与提示要用解析后的名字，
  // 否则标题会是空的。
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
        {/* 按行标识重建展开按钮：行位换成另一行时箭头直接是那一行的方向，只有展开、收起同一行时箭头才转过去（motion.css）。 */}
        <Show
          when={row()?.kind === "software" && row()?.softwareId && (row()?.processCount ?? 0) > 0 ? row()!.id : undefined}
          keyed
          // 软件级是树：不能展开的行留出箭头的位置，名字对齐。进程级没有树，不留空位。
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
            {/* Fixed Lucide geometry (ISC, included in credits): a static SVG
                template avoids a Dynamic/For/context tree in every row. */}
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
  const processMode = mode === "process";
  return [
    { id: "name", unit: "", visible: true, sortable: true, width: 260, source: null, processFieldName: null, additiveAcrossProcesses: null },
    ...(processMode ? [{ id: "pid", unit: "", visible: true, sortable: true, width: 76, source: null, processFieldName: null, additiveAcrossProcesses: null }] : []),
    { id: "status", unit: "", visible: true, sortable: true, width: 92, source: null, processFieldName: null, additiveAcrossProcesses: null },
    ...(processMode ? [
      { id: "user", unit: "", visible: true, sortable: true, width: 150, source: null, processFieldName: null, additiveAcrossProcesses: null },
      { id: "architecture", unit: "", visible: true, sortable: true, width: 76, source: null, processFieldName: null, additiveAcrossProcesses: null }
    ] : []),
    { id: "cpu", unit: "%", visible: true, sortable: true, width: 86, source: null, processFieldName: null, additiveAcrossProcesses: null },
    { id: "memory", unit: "B", visible: true, sortable: true, width: 110, source: null, processFieldName: null, additiveAcrossProcesses: null },
    ...gpuResourceTableColumns(catalog),
    { id: "disk", unit: "B/s", visible: true, sortable: true, width: 106, source: null, processFieldName: null, additiveAcrossProcesses: null },
    { id: "network", unit: "bps", visible: true, sortable: true, width: 106, source: null, processFieldName: null, additiveAcrossProcesses: null }
  ];
}

function gpuResourceTableColumns(catalog: MetricDefinition[]): ResourceTableColumn[] {
  return catalog
    .filter((metric) => /^gpu\.\d+\.(usage|vram)$/i.test(metric.id))
    .sort((left, right) => gpuColumnOrder(left.id) - gpuColumnOrder(right.id))
    .map((metric) => ({
      id: metric.id,
      unit: metric.id.endsWith(".vram") ? "B" : "%",
      visible: true,
      sortable: true,
      width: metric.id.endsWith(".vram") ? 132 : 108,
      source: null,
      processFieldName: null,
      additiveAcrossProcesses: null
    }));
}

/**
 * 列宽把手的呈现层：逻辑中心是把手中线，也就是列的右边缘。通道是右边缘离表头左边缘多少屏幕 px，
 * 几何就是表头左边缘两侧各一个边界远的两点（1 个单位 = 1 px），表头横向滚动时跟着表头走。
 * 列宽由表格按宿主值画，把手自己不另画；无障碍的当前宽度由表格按存的宽度写在把手上。
 */
function columnResizeView(handle: HTMLElement, editing: () => boolean): Presentation {
  return {
    element: handle,
    bindingLabel: "右边缘位置",
    geometry: () => {
      // 非编辑模式没有可拖把手；宿主列宽更新不需要把屏幕坐标换成通道值。
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
  return Math.round(Math.min(maxColumnWidth, Math.max(minColumnWidth, Number(value) || 100)));
}

function distributeColumnWidths(columns: ResourceTableColumn[], targetWidth: number) {
  const baseWidths = columns.map((column) => clampColumnWidth(column.width));
  const baseWidth = baseWidths.reduce((sum, width) => sum + width, 0);
  const extraWidth = Math.max(0, Math.floor(targetWidth) - baseWidth);
  if (columns.length === 0 || extraWidth <= 0) {
    return baseWidths;
  }

  const weights = columns.map((column) => resourceTableColumnFlexWeight(column.id));
  const totalWeight = weights.reduce((sum, weight) => sum + weight, 0) || columns.length;
  let assignedExtra = 0;
  return baseWidths.map((width, index) => {
    if (index === baseWidths.length - 1) {
      return width + extraWidth - assignedExtra;
    }

    const extra = Math.floor(extraWidth * weights[index] / totalWeight);
    assignedExtra += extra;
    return width + extra;
  });
}

function resourceTableColumnFlexWeight(columnId: string) {
  if (columnId === "name") return 3;
  if (columnId === "status" || columnId === "user") return 1.4;
  if (columnId === "memory" || columnId.endsWith(".vram")) return 1.2;
  if (columnId === "pid" || columnId === "architecture") return 0.7;
  return 1;
}

// 状态列：汇总行装的是采样状态标识，软件行装的是分组标识，进程行装的是进程状态。
function rowStatus(row: ResourceTableRow | undefined) {
  const value = String(row?.status ?? "").trim();
  if (row?.kind === "summary") {
    if (value === "empty") return uiText.status.noData;
    const copy = uiText.resourceTable.summaryRow.status as Record<string, string | undefined>;
    return copy[value] ?? uiText.resourceTableView.unknownStatus;
  }

  // 系统/驱动保留下面的每一项（例如物理内存的 PID -1 分项）不是进程：按所属条目判断，状态与保留量那一行相同。
  if (row?.kind === "process" && isReservedSegment(row)) {
    return uiText.softwareKind.system_residual;
  }

  if (value === "running") {
    return uiText.status.state.running;
  }

  // 系统/驱动保留行（软件行和它的进程行）装的是归属类型 system-residual，不是进程状态，名字和资源条的分组同一出处。
  if (value === "system-residual") {
    return uiText.softwareKind.system_residual;
  }

  return softwareGroupLabel(value)
    ?? userFacingLabel(row?.status, uiText.resourceTableView.unknownStatus);
}

// 分组标识（后端 SoftwareDisplayKinds）转成当前语言的名字；不是分组标识就返回 undefined。
function softwareGroupLabel(value?: string | null) {
  const group = String(value ?? "").trim() as keyof typeof uiText.softwareKind;
  return uiText.softwareKind[group];
}

function gpuColumnOrder(metricId: string) {
  const match = /^gpu\.(\d+)\.(usage|vram)$/i.exec(metricId);
  return match ? Number(match[1]) * 2 + (match[2].toLowerCase() === "vram" ? 1 : 0) : 999;
}
