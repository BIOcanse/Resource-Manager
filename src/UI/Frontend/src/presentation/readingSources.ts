import { createSignal } from "solid-js";
import type { MetricFieldReading } from "../data/metricFields/metricFieldSource.ts";
import type { AppReadingSourceMode, ReadingSourceDisplay } from "../types.ts";

/*
 * 读数来源怎么列、加什么标记。合同见 docs/metric_reading_source_selection.md。
 *
 * 首选、来源能力都由后端核定，这里只读字段自己的 reading，不按数值、字段名或身份字符串挑选。
 * 一个带 reading 的字段按后端给的事实归入一类：
 *   首选：isPreferredSource；
 *   不支持：不是首选，且 sourceAvailability 是 unsupported；
 *   次选：不是首选，读取项有首选引用（preferredDeviceIdentity 和 preferredFieldName）；
 *   待确认：读取项还没有首选引用。
 * 只显示首选（设置 preferred，默认）：列出首选和待确认；显示所有可用来源（all）：再列出次选。
 * 不支持的来源两种模式都不列。
 * 标记：待确认的来源两种模式都标「待确认」；显示所有可用来源时，同一读取项在这批字段里
 * 还列出了别的来源，首选标「首选」（只有一个来源的读取项不标）。
 * reading 为 null 的字段照原样列出，不加标记。
 * 已经放到卡片上的字段一直显示它自己的原值，不因模式隐藏或换成别的来源，只按同样规则加标记。
 * 调度用的来源由后端决定，不读这里。
 */

// 当前模式的唯一运行时所有者：设置里的选择是唯一来源，由 App 应用到这里（和 byteUnits 同一做法）。
const [activeMode, setActiveMode] = createSignal<AppReadingSourceMode>("preferred");

export function currentReadingSourceMode(): AppReadingSourceMode {
  return activeMode();
}

export function applyReadingSourceMode(mode?: string | null) {
  setActiveMode(normalizeReadingSourceMode(mode));
}

export function normalizeReadingSourceMode(value: unknown): AppReadingSourceMode {
  return value === "all" ? "all" : "preferred";
}

export type { ReadingSourceDisplay };
export type ReadingSourceMark = NonNullable<ReadingSourceDisplay["mark"]>;

type ReadingSourceRole = "preferred" | "alternate" | "pending" | "unsupported";

const plainField: ReadingSourceDisplay = Object.freeze({ listed: true, mark: null });

function sourceRole(reading: MetricFieldReading): ReadingSourceRole {
  if (reading.isPreferredSource) return "preferred";
  if (reading.sourceAvailability === "unsupported") return "unsupported";
  return reading.preferredDeviceIdentity !== null && reading.preferredFieldName !== null ? "alternate" : "pending";
}

/**
 * 一批字段（一份目录、一次订阅结果）的来源显示。先按读取项 id 数出「显示所有可用来源」时
 * 每个读取项能列出几个来源，再逐个字段给出是否列出和标记。
 */
export function readingSourceDisplays(
  readings: Iterable<MetricFieldReading | null>,
  mode: AppReadingSourceMode = activeMode()
): (reading: MetricFieldReading | null) => ReadingSourceDisplay {
  const listedSources = new Map<string, number>();
  for (const reading of readings) {
    if (reading && sourceRole(reading) !== "unsupported") {
      listedSources.set(reading.id, (listedSources.get(reading.id) ?? 0) + 1);
    }
  }
  return (reading) => {
    if (!reading) return plainField;
    const role = sourceRole(reading);
    return {
      listed: role === "preferred" || role === "pending" || (role === "alternate" && mode === "all"),
      mark: role === "pending"
        ? "pending"
        : role === "preferred" && mode === "all" && (listedSources.get(reading.id) ?? 0) > 1 ? "preferred" : null
    };
  };
}
