import { createSignal, For, Show } from "solid-js";
import { formatBytes } from "../../presentation/byteUnits.ts";
import { uiText } from "../../text.ts";
import { DiskUsageTreemapPlane } from "./DiskUsageTreemapPlane";
import type { DiskUsageTileFacts } from "./diskUsageLayoutFacts.ts";
import type { DiskUsageViewWindow } from "./diskUsageViewport.ts";
import type {
  DiskUsageLayout,
  DiskUsageScanSummary
} from "./diskUsageLayoutTypes.ts";

/**
 * 扫描结果面板：概况 + 方格图 + 选中提示。
 *
 * 选中提示跟条形图的提示同一口径：跟着指针走，说清楚这一格是什么、占多少。
 */
export function DiskUsageResultPanel(props: {
  summary: DiskUsageScanSummary;
  layout: DiskUsageLayout;
  selectedNodeId: number;
  factsOf: (nodeId: number) => DiskUsageTileFacts | undefined;
  labelOf: (nodeId: number) => string;
  onSelect: (nodeId: number) => void;
  onActivate: (nodeId: number) => void;
  onContextMenu: (nodeId: number, clientX: number, clientY: number) => void;
  onViewChanged: (view: DiskUsageViewWindow) => void;
  /** 复位：视口回到整图，布局也换回整图那一份。 */
  onResetView: () => void;
  resetNonce: number;
  onNavigateUp: () => void;
  canNavigateUp: boolean;
}) {
  const [hover, setHover] = createSignal<{ node: number; x: number; y: number } | null>(null);
  const hovered = () => {
    const current = hover();
    return current && current.node >= 0 ? props.factsOf(current.node) : undefined;
  };

  return (
    <div class="panel disk-usage-result">
      <div class="panel-header">
        <div class="optimization-heading">
          <h2>{props.layout.rootPath}</h2>
          <span>
            {uiText.diskUsage.scanKind[
              props.summary.scanKind as keyof typeof uiText.diskUsage.scanKind
            ] ?? props.summary.scanKind}
            {" · "}
            {uiText.diskUsage.scanTotals(
              formatBytes(props.layout.rootSizeBytes, "storage"),
              props.summary.fileCount,
              props.summary.directoryCount)}
          </span>
        </div>
        <div class="disk-usage-view-actions">
          <button
            class="secondary"
            type="button"
            onClick={props.onResetView}
          >
            {uiText.diskUsage.resetView}
          </button>
          <Show when={props.canNavigateUp}>
            <button class="secondary" type="button" onClick={props.onNavigateUp}>
              {uiText.diskUsage.navigateUp}
            </button>
          </Show>
        </div>
      </div>

      <Show when={props.summary.skipped.length > 0}>
        <div class="disk-usage-warning" role="note">
          <strong>{uiText.diskUsage.skipped}</strong>
          <ul>
            <For each={props.summary.skipped}>
              {(entry) => (
                <li>
                  {entry.target}
                  {" — "}
                  {uiText.diskUsage.skipReason[
                    entry.reason as keyof typeof uiText.diskUsage.skipReason
                  ] ?? entry.reason}
                </li>
              )}
            </For>
          </ul>
        </div>
      </Show>

      <DiskUsageTreemapPlane
        layout={props.layout}
        selectedNodeId={props.selectedNodeId}
        labelOf={props.labelOf}
        onSelect={props.onSelect}
        onActivate={props.onActivate}
        onContextMenu={props.onContextMenu}
        onViewChanged={props.onViewChanged}
        resetNonce={props.resetNonce}
        onHover={(node, x, y) => setHover(node >= 0 ? { node, x, y } : null)}
      />

      <Show when={hovered()}>
        {(node) => (
          <div
            class="disk-usage-hover-tip"
            role="status"
            style={{
              left: `${hover()!.x + 14}px`,
              top: `${hover()!.y + 16}px`
            }}
          >
            <strong>{node().name || node().fullPath}</strong>
            <span>{formatBytes(node().sizeBytes, "storage")}</span>
            <Show when={node().isDirectory}>
              <span>{uiText.diskUsage.fileCount(node().fileCount)}</span>
            </Show>

            <span class="disk-usage-hover-path">{node().fullPath}</span>
          </div>
        )}
      </Show>

      <Show when={props.layout.omittedCount > 0}>
        <p class="disk-usage-omitted">
          {uiText.diskUsage.omitted(props.layout.omittedCount)}
        </p>
      </Show>
    </div>
  );
}
