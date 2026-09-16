import {
  localizedMetricLabel,
  resourceTableCellText,
  resourceTableColumnLabel,
  softwareDisplayName
} from "../presentation/metricLabels";
import { createEffect, createMemo, createSignal, For, onCleanup, onMount, Show } from "solid-js";
import type { JSX } from "solid-js";
import { ArrowLeft, ArrowRight, MoreHorizontal } from "lucide-solid";
import { pointerReorderProps } from "../interactions/pointerReorder";
import { userFacingLabel } from "../presentation/userFacingText";
import { ResourcePerformancePanel } from "./ResourcePerformancePanel";
import type { SoftwareContextMenuTarget } from "./SoftwareContextMenu";
import type { MetricDefinition, MetricSnapshot, ResourceTableColumn, ResourceTableColumnSettings, ResourceTableRow, ResourceTableSnapshot, ResourceTableViewMode } from "../types";
import { uiText } from "../text.ts";
import { SegmentedControl } from "../ui/primitives/SegmentedControl.tsx";
import type { ObservationState } from "../observation/observationState";
import {
  moveResourceTableFocus,
  reconcileResourceTableFocus,
  resourceTableFocusableRows,
  type ResourceRowFocusMove
} from "../resourceTable/resourceTableFocus";
import {
  classifyResourceTableContentState,
  type ResourceTableContentState
} from "../resourceTable/resourceTableContentState";
import { pinResourceTableRow } from "../resourceTable/resourceTableRowPin";
import { resourceTableColumnsEqual } from "../resourceTable/resourceTableColumnIdentity";
import { projectResourceTableHeat } from "../resourceTable/resourceTableHeat";
import { formatBytes } from "../presentation/byteUnits.ts";
import { ContentState } from "../ui/patterns/ContentState.tsx";
import { useInlineEditorFocus } from "../interactions/inlineEditorFocus";

const rowHeight = 38;
const overscan = 6;
const slotReserve = 10;
const minColumnWidth = 56;
const maxColumnWidth = 520;
type CssVars = JSX.CSSProperties & Record<string, string>;
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
  sortColumnId: string;
  sortDirection: string;
  expandedSoftwareIds: Record<string, boolean>;
  highlightedSoftwareId?: string | null;
  onModeChange: (mode: ResourceTableViewMode) => void;
  onToggleEdit: () => void;
  onCancelEdit: () => void;
  onSortChange: (columnId: string, direction: "asc" | "desc") => void;
  onToggleColumn: (columnId: string, visible: boolean) => void;
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
  const visibleRows = createMemo(() => projectResourceTableHeat(projectedRows()));
  const baseCount = createMemo(() => {
    const rows = sourceRows();
    return props.mode === "software"
      ? rows.filter((row) => row.kind === "software").length
      : rows.filter((row) => row.kind !== "summary").length;
  });
  const visibleBusinessCount = createMemo(() => projectedRows()
    .filter((row) => row.kind !== "summary").length);
  const contentState = createMemo(() => props.snapshot === null
    ? "ready-empty"
    : classifyResourceTableContentState({
      businessRowCount: baseCount(),
      visibleBusinessRowCount: visibleBusinessCount(),
      searchActive: Boolean(normalizeSearchText(searchQuery()))
    }));
  const statusText = createMemo(() => {
    if (props.mode === "performance") {
      return "--";
    }

    const total = baseCount();
    if (normalizeSearchText(searchQuery())) {
      return `${projectedRows().filter((row) => row.kind !== "summary").length} / ${total}`;
    }

    return String(total);
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
            <span id="resourceTableStatus">{statusText()}</span>
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
              {props.editMode
                ? props.saveState === "saving" ? uiText.common.saving : uiText.resourceTable.save
                : uiText.resourceTable.edit}
            </button>
          </Show>
        </div>
      </div>
      <Show when={props.editMode && props.mode !== "performance"}>
        <ResourceTableColumnEditor
          onElement={(element) => { editorRegion = element; }}
          mode={props.mode}
          catalog={props.catalog}
          columns={props.columnSettings}
          onToggle={props.onToggleColumn}
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
                snapshot={props.snapshot}
                columnSettings={props.columnSettings}
                sortColumnId={props.sortColumnId}
                sortDirection={props.sortDirection}
                expandedSoftwareIds={props.expandedSoftwareIds}
                highlightedSoftwareId={props.highlightedSoftwareId}
                onSortChange={props.onSortChange}
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
  if (props.state === "filtered-empty") {
    return (
      <ContentState
        kind="empty"
        title={uiText.resourceTableView.noMatchTitle}
        detail={uiText.resourceTableView.noMatchDetail}
        actions={<button class="secondary" type="button" onClick={props.onClearSearch}>{uiText.resourceTableView.clearSearch}</button>}
      />
    );
  }
  return (
    <ContentState
      kind="empty"
      title={uiText.resourceTableView.emptyTitle}
      detail={uiText.resourceTableView.emptyDetail}
    />
  );
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
  columns: ResourceTableColumnSettings[];
  onToggle: (columnId: string, visible: boolean) => void;
}) {
  const byId = () => new Map(props.columns.map((column) => [column.id, column]));
  return (
    <div
      ref={props.onElement}
      class="resource-table-column-editor"
      role="group"
      aria-label={uiText.resourceTableView.columnEditorLabel}
    >
      <For each={resourceTableEditorColumns(props.columns, props.catalog, props.mode)}>
        {(column) => (
          <label class="resource-table-column-option">
            <input
              type="checkbox"
              disabled={column.id === "name"}
              checked={byId().get(column.id)?.visible ?? column.visible}
              onChange={(event) => props.onToggle(column.id, event.currentTarget.checked)}
            />
            <span>{resourceTableColumnLabel(column.id)}</span>
          </label>
        )}
      </For>
    </div>
  );
}

function VirtualResourceTable(props: {
  mode: ResourceTableViewMode;
  editMode: boolean;
  rows: ResourceTableRow[];
  snapshot: ResourceTableSnapshot | null;
  columnSettings: ResourceTableColumnSettings[];
  sortColumnId: string;
  sortDirection: string;
  expandedSoftwareIds: Record<string, boolean>;
  highlightedSoftwareId?: string | null;
  onSortChange: (columnId: string, direction: "asc" | "desc") => void;
  onColumnWidthChange: (columnId: string, width: number, commit: boolean) => void;
  onToggleExpand: (softwareId: string) => void;
  onSoftwareContextMenu?: SoftwareContextMenuHandler;
  contextMenuOpen?: boolean;
  onReorderColumn: (sourceColumnId: string, targetColumnId: string) => void;
}) {
  let viewport: HTMLDivElement | undefined;
  const [scrollTop, setScrollTop] = createSignal(0);
  const [scrollLeft, setScrollLeft] = createSignal(0);
  const [viewportHeight, setViewportHeight] = createSignal(360);
  const [viewportWidth, setViewportWidth] = createSignal(0);
  const [scrollbarWidth, setScrollbarWidth] = createSignal(0);
  const [dragColumnId, setDragColumnId] = createSignal<string | null>(null);
  const [overColumnId, setOverColumnId] = createSignal<string | null>(null);
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

  const clearColumnDragState = () => {
    setDragColumnId(null);
    setOverColumnId(null);
  };
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
  const columnIds = createMemo(() => columns().map((column) => column.id));
  const gridTemplate = createMemo(() => columnWidths().map((width) => `${width}px`).join(" "));
  const visibleRange = createMemo(() => {
    const start = Math.max(0, Math.floor(scrollTop() / rowHeight) - overscan);
    return { start };
  });
  const slots = createMemo(() => {
    const count = Math.ceil(viewportHeight() / rowHeight) + overscan * 2 + slotReserve;
    return Array.from({ length: Math.max(1, count) }, (_, index) => index);
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
    if (viewport.scrollTop !== target) {
      viewport.scrollTop = target;
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
    setScrollTop(nextScrollTop);
  });

  onMount(() => {
    const updateSize = () => {
      if (viewport) {
        setViewportHeight(Math.max(180, viewport.clientHeight));
        setViewportWidth(Math.max(0, viewport.clientWidth));
        setScrollbarWidth(Math.max(0, viewport.offsetWidth - viewport.clientWidth));
      }
    };
    updateSize();
    const observer = new ResizeObserver(updateSize);
    if (viewport) {
      observer.observe(viewport);
    }
    onCleanup(() => observer.disconnect());
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
    if (!props.onSoftwareContextMenu || (row.kind !== "software" && row.kind !== "process")) {
      return;
    }
    const index = rows().findIndex((candidate) => candidate.id === row.id);
    if (index >= 0) {
      setPinnedRowId(row.id);
      setPinnedIndex(index);
    }
    props.onSoftwareContextMenu(
      contextMenuEventForElement(element),
      createSoftwareContextTargetFromRow(row, props.mode, props.expandedSoftwareIds),
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
    if (!column.sortable) {
      return;
    }

    const nextDirection = props.sortColumnId === column.id && props.sortDirection === "desc" ? "asc" : "desc";
    props.onSortChange(column.id, nextDirection);
  };

  const startResize = (event: PointerEvent, column: ResourceTableColumn) => {
    if (!props.editMode) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    const startX = event.clientX;
    const startWidth = clampColumnWidth(column.width);
    const resize = (moveEvent: PointerEvent) => {
      const nextWidth = clampColumnWidth(startWidth + moveEvent.clientX - startX);
      props.onColumnWidthChange(column.id, nextWidth, false);
    };
    const stop = (upEvent: PointerEvent) => {
      window.removeEventListener("pointermove", resize);
      window.removeEventListener("pointerup", stop);
      const nextWidth = clampColumnWidth(startWidth + upEvent.clientX - startX);
      props.onColumnWidthChange(column.id, nextWidth, true);
    };

    window.addEventListener("pointermove", resize);
    window.addEventListener("pointerup", stop, { once: true });
  };

  const resizeByKeyboard = (
    event: KeyboardEvent,
    column: ResourceTableColumn
  ) => {
    if (!props.editMode
      || event.altKey
      || event.ctrlKey
      || event.metaKey
      || (event.key !== "ArrowLeft" && event.key !== "ArrowRight")) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    const direction = event.key === "ArrowRight" ? 1 : -1;
    props.onColumnWidthChange(
      column.id,
      clampColumnWidth(column.width + direction * 12),
      false);
  };

  return (
    <div
      class="resource-table-frame"
      role="table"
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
              const column = () => columns().find((candidate) => candidate.id === columnId)!;
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
                  editing: props.editMode,
                  "drag-source": dragColumnId() === columnId,
                  "drag-over": overColumnId() === columnId && dragColumnId() !== null && dragColumnId() !== columnId
                }}
                {...pointerReorderProps(() => ({
                  enabled: props.editMode,
                  group: "resource-table-column",
                  sourceId: columnId,
                  onStart: setDragColumnId,
                  onOver: setOverColumnId,
                  onCommit: props.onReorderColumn,
                  onEnd: clearColumnDragState
                }))}
              >
                <button
                  class="resource-table-sort-button"
                  type="button"
                  disabled={!column().sortable}
                  onClick={() => updateSort(column())}
                >
                  <span>{resourceTableColumnLabel(column().id)}</span>
                  <Show when={props.sortColumnId === columnId}>
                    <small>{props.sortDirection === "asc" ? "↑" : "↓"}</small>
                  </Show>
                </button>
                <Show when={props.editMode}>
                  <span class="resource-table-column-order-actions">
                    <button
                      class="resource-table-column-order-button"
                      type="button"
                      disabled={columnIndex() === 0}
                      aria-label={uiText.resourceTableView.moveColumnLeft(resourceTableColumnLabel(column().id))}
                      title={uiText.resourceTableView.moveLeftTitle}
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
                      aria-label={uiText.resourceTableView.moveColumnRight(resourceTableColumnLabel(column().id))}
                      title={uiText.resourceTableView.moveRightTitle}
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
                <span
                  class="resource-table-column-resizer"
                  role="separator"
                  tabIndex={props.editMode ? 0 : -1}
                  aria-orientation="vertical"
                  aria-label={uiText.resourceTableView.resizeColumn(resourceTableColumnLabel(column().id))}
                  aria-valuemin={minColumnWidth}
                  aria-valuemax={maxColumnWidth}
                  aria-valuenow={Math.round(clampColumnWidth(column().width))}
                  aria-disabled={props.editMode ? "false" : "true"}
                  onPointerDown={(event) => startResize(event, column())}
                  onKeyDown={(event) => resizeByKeyboard(event, column())}
                />
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
              const row = () => rows()[rowIndex()];
              return (
                <ResourceTableRowSlot
                  row={row}
                  columns={columns}
                  gridTemplate={gridTemplate}
                  rowIndex={rowIndex}
                  mode={props.mode}
                  expandedSoftwareIds={() => props.expandedSoftwareIds}
                  highlightedSoftwareId={props.highlightedSoftwareId}
                  activeRowId={activeRowId}
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

function ResourceTableRowSlot(props: {
  row: () => ResourceTableRow | undefined;
  columns: () => ResourceTableColumn[];
  gridTemplate: () => string;
  rowIndex: () => number;
  mode: ResourceTableViewMode;
  expandedSoftwareIds: () => Record<string, boolean>;
  highlightedSoftwareId?: string | null;
  activeRowId: () => string | null;
  onActivateRow: (rowId: string, focus: boolean) => void;
  onRowKeyDown: (event: KeyboardEvent, row: ResourceTableRow, element: HTMLElement) => void;
  onToggleExpand: (softwareId: string) => void;
  onSoftwareContextMenu?: SoftwareContextMenuHandler;
  /** 行上的右键与「更多操作」都走这一个入口，钉住逻辑只在那里。 */
  onRowContextMenu: (row: ResourceTableRow, element: HTMLElement) => void;
}) {
  const row = () => props.row();
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
      aria-rowindex={props.rowIndex() + 2}
      aria-label={rowName(row())}
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
        "empty-row": !hasRow(),
        highlighted: isHighlighted()
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
        "grid-template-columns": props.gridTemplate(),
        transform: `translateY(${props.rowIndex() * rowHeight}px)`,
        visibility: hasRow() ? "visible" : "hidden",
        "pointer-events": hasRow() ? "auto" : "none"
      }}
    >
      <For each={props.columns().map((column) => column.id)}>
        {(columnId, columnIndex) => {
          const column = () => props.columns().find((candidate) => candidate.id === columnId)!;
          return <ResourceTableCell
            column={column()}
            columnIndex={columnIndex()}
            onRowContextMenu={props.onRowContextMenu}
            row={row}
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
    name: row.kind === "process" ? row.softwareName ?? row.name : displayName,
    softwareId: row.softwareId,
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
    processNames: row.kind === "process" ? [row.name] : row.processNames ?? [],
    executablePaths: row.executablePaths ?? [],
    canExpand: mode === "software" && row.kind === "software" && Boolean(row.softwareId) && (row.processCount ?? 0) > 0,
    expanded: row.softwareId ? expandedSoftwareIds[row.softwareId] === true : false
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
  columnIndex: number;
  row: () => ResourceTableRow | undefined;
  mode: ResourceTableViewMode;
  expandedSoftwareIds: () => Record<string, boolean>;
  onToggleExpand: (softwareId: string) => void;
  onSoftwareContextMenu?: SoftwareContextMenuHandler;
  onRowContextMenu: (row: ResourceTableRow, element: HTMLElement) => void;
}) {
  const row = () => props.row();
  const value = () => row()?.values?.[props.column.id];
  const heat = () => value()?.heatPercent ?? 0;
  if (props.column.id === "name") {
    return (
      <div
        class="resource-table-cell name-cell"
        classList={{ indented: row()?.depth === 1 }}
        role="cell"
        aria-colindex={props.columnIndex + 1}
      >
        <Show
          when={row()?.kind === "software" && row()?.softwareId && (row()?.processCount ?? 0) > 0}
          fallback={<span class="resource-row-spacer" />}
        >
          <button
            type="button"
            class="resource-row-expander"
            tabIndex={-1}
            onClick={() => row()?.softwareId && props.onToggleExpand(row()!.softwareId!)}
            aria-label={props.expandedSoftwareIds()[row()?.softwareId ?? ""] ? uiText.resourceTable.collapseProcesses : uiText.resourceTable.expandProcesses}
            aria-expanded={props.expandedSoftwareIds()[row()?.softwareId ?? ""] ? "true" : "false"}
          >
            {props.expandedSoftwareIds()[row()?.softwareId ?? ""] ? "▾" : "▸"}
          </button>
        </Show>
        <span class="resource-row-name" title={rowName(row())}>{rowName(row())}</span>
        <Show when={props.onSoftwareContextMenu && (row()?.kind === "software" || row()?.kind === "process")}>
          <button
            type="button"
            class="resource-row-actions"
            tabIndex={-1}
            aria-label={uiText.resourceTableView.moreActions(rowName(row()) || uiText.resourceTableView.currentItem)}
            title={uiText.resourceTableView.moreActionsTitle}
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
            <MoreHorizontal aria-hidden="true" size={16} strokeWidth={2} />
          </button>
        </Show>
      </div>
    );
  }

  if (props.column.id === "status") {
    const status = () => rowStatus(row());
    return (
      <div class="resource-table-cell status-cell" role="cell" aria-colindex={props.columnIndex + 1} title={status()}>
        {status()}
      </div>
    );
  }

  if (props.column.id === "pid" || props.column.id === "user" || props.column.id === "architecture") {
    return (
      <div class="resource-table-cell text-cell" role="cell" aria-colindex={props.columnIndex + 1} title={resourceTableCellText(value(), props.column.id)}>
        {resourceTableCellText(value(), props.column.id)}
      </div>
    );
  }

  return (
    <div
      class="resource-table-cell value-cell"
      role="cell"
      aria-colindex={props.columnIndex + 1}
      classList={{ unavailable: value()?.availability === "Unavailable" }}
      style={{ "--heat": `${heat()}%`, "--private-heat": `${value()?.privateHeatPercent ?? heat()}%` } satisfies CssVars}
      title={value()?.sharedValue != null
        ? uiText.resourceTableView.memoryBreakdown(
          resourceTableColumnLabel(props.column.id),
          resourceTableCellText(value(), props.column.id),
          formatBytes((value()?.value ?? 0) - value()!.sharedValue!, "memory"),
          formatBytes(value()?.sharedValue, "memory"))
        : value()
          ? `${resourceTableColumnLabel(props.column.id)}: ${resourceTableCellText(value(), props.column.id)}`
          : resourceTableColumnLabel(props.column.id)}
    >
      {resourceTableCellText(value(), props.column.id)}
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
    { id: "name", unit: "", visible: true, sortable: true, width: 260 },
    ...(processMode ? [{ id: "pid", unit: "", visible: true, sortable: true, width: 76 }] : []),
    { id: "status", unit: "", visible: true, sortable: true, width: 92 },
    ...(processMode ? [
      { id: "user", unit: "", visible: true, sortable: true, width: 150 },
      { id: "architecture", unit: "", visible: true, sortable: true, width: 76 }
    ] : []),
    { id: "cpu", unit: "%", visible: true, sortable: true, width: 86 },
    { id: "memory", unit: "B", visible: true, sortable: true, width: 110 },
    ...gpuResourceTableColumns(catalog),
    { id: "disk", unit: "B/s", visible: true, sortable: true, width: 106 },
    { id: "network", unit: "bps", visible: true, sortable: true, width: 106 }
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
      width: metric.id.endsWith(".vram") ? 132 : 108
    }));
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

// 行名：汇总行用固定文案；软件与进程行用后端给的名字；
// 没有名字的行（例如「未归属进程」）按它的分组标识出名。
function rowName(row: ResourceTableRow | undefined) {
  if (row?.kind === "summary") {
    return uiText.resourceTable.summaryRow.name;
  }

  // 软件行的分组标识装在 status 里，其余由共用规则处理。
  return softwareDisplayName({
    softwareId: row?.softwareId,
    name: row?.name,
    displayKind: row?.status
  });
}

// 状态列：汇总行装的是采样状态标识，软件行装的是分组标识，进程行装的是进程状态。
function rowStatus(row: ResourceTableRow | undefined) {
  const value = String(row?.status ?? "").trim();
  if (row?.kind === "summary") {
    const copy = uiText.resourceTable.summaryRow.status as Record<string, string | undefined>;
    return copy[value] ?? uiText.resourceTableView.unknownStatus;
  }

  if (value === "running") {
    return uiText.status.state.running;
  }

  return softwareGroupLabel(value)
    ?? userFacingLabel(row?.status, uiText.resourceTableView.unknownStatus);
}

// 分组标识（后端 SoftwareDisplayKinds）转成当前语言的名字；不是分组标识就返回 undefined。
function softwareGroupLabel(value?: string | null) {
  const group = String(value ?? "").trim() as keyof typeof uiText.softwareKind;
  return uiText.softwareKind[group];
}

function gpuMetricLabel(metricId: string, fallback: string) {
  const match = /^gpu\.(\d+)\.(usage|vram)$/i.exec(metricId);
  if (!match) {
    return fallback;
  }

  return match[2].toLowerCase() === "vram"
    ? uiText.resourceTable.gpuVramLabel(match[1])
    : uiText.resourceTable.gpuUsageLabel(match[1]);
}

function gpuColumnOrder(metricId: string) {
  const match = /^gpu\.(\d+)\.(usage|vram)$/i.exec(metricId);
  return match ? Number(match[1]) * 2 + (match[2].toLowerCase() === "vram" ? 1 : 0) : 999;
}
