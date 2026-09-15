import { uiText } from "../text";

// 后端按稳定指标 id 提供事实，用户看到的名称由当前语言的文案包决定。
// 不认识的 id 保留后端标签，保证新增指标不会显示空白。
export function localizedMetricLabel(
  id: string | null | undefined,
  fallbackLabel?: string | null
): string {
  const normalized = String(id ?? "").trim();
  if (!normalized) {
    return fallbackLabel?.trim() || "";
  }

  const { pattern, index } = normalizeMetricId(normalized);
  const entry = (uiText.metricLabel as Record<string, unknown>)[pattern];
  if (typeof entry === "string") {
    return entry;
  }
  if (typeof entry === "function") {
    return (entry as (value: string) => string)(index ?? "");
  }

  return fallbackLabel?.trim() || normalized;
}

/**
 * 资源列表的表头。后端只发列 id，措辞全部在这里按当前语言取。
 * GPU 列的 id 形如 `gpu.0.usage` / `gpu.1.vram`，走指标文案那一套。
 */
export function resourceTableColumnLabel(id: string | null | undefined): string {
  const normalized = String(id ?? "").trim();
  if (!normalized) {
    return "";
  }

  const columns = uiText.resourceTable.columns as Record<string, string | undefined>;
  const known = columns[normalized];
  if (known) {
    return known;
  }

  const gpu = /^gpu\.(\d+)\.(usage|vram)$/i.exec(normalized);
  if (gpu) {
    return gpu[2].toLowerCase() === "vram"
      ? uiText.resourceTable.gpuVramLabel(gpu[1])
      : uiText.resourceTable.gpuUsageLabel(gpu[1]);
  }

  return localizedMetricLabel(normalized);
}

function normalizeMetricId(id: string): { pattern: string; index: string | null } {
  const segments = id.split(".");
  let index: string | null = null;
  const pattern = segments
    .map((segment) => {
      if (/^\d+$/.test(segment)) {
        index = segment;
        return "{index}";
      }

      const suffixed = /^([a-zA-Z]+)(\d+)$/.exec(segment);
      if (suffixed) {
        index = suffixed[2];
        return `${suffixed[1]}{index}`;
      }

      return segment;
    })
    .join(".");
  return { pattern, index };
}
