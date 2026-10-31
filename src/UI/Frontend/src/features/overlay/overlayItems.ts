import type { PerformanceOverlayEditingConstraints, PerformanceOverlayItem } from "../../types.ts";
import { ResourceManager } from "../../preferences/generated/desktopPoints.values.ts";
import { itemListIssue, type ItemListIssue } from "../itemSelection/itemSelectionModel.ts";

/*
 * 浮层的项（docs/targeted_recording_views_2026-10-30.md §3.9）：每个软件一组 {kind, id, source, intervalMs, syncPrecision}，
 * 顺序就是绘制顺序。整份存在后端设置 performanceOverlay 里，后端写入前做同一份校验；默认的项也由后端给出。
 */

/**
 * 留空间隔的非帧项实际用的间隔：常驻进程订阅浮层时路径里的 intervalMs（构建点 overlay_sample_interval_ms，src/Development/DesktopBuild/desktop.points.json）。
 * 选择器把它写进间隔框的占位文字，每一项都可以填自己的间隔。
 */
export const overlayDefaultIntervalMs = ResourceManager.Desktop.DesktopPoints.overlay_sample_interval_ms;

/** 一组浮层项是否合规；合规返回 null。 */
export function overlayItemsIssue(items: readonly PerformanceOverlayItem[], constraints: Pick<PerformanceOverlayEditingConstraints, "maximumItems">): ItemListIssue | null {
  return itemListIssue(items, constraints.maximumItems);
}
