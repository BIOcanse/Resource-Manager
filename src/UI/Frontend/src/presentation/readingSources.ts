import type { MetricFieldReading } from "../data/metricFields/metricFieldSource.ts";
import type { ReadingSourceDisplay } from "../types.ts";

/*
 * 读数来源怎么列、加什么标记。合同见 docs/metric_reading_source_selection.md。
 *
 * 首选、来源能力都由后端核定，这里只读字段自己的 reading，不按数值、字段名或身份字符串挑选。
 * 一个带 reading 的字段按后端给的事实归入一类：
 *   首选：isPreferredSource；
 *   不支持：不是首选，且 sourceAvailability 是 unsupported；
 *   次选：不是首选，读取项有首选引用（preferredDeviceIdentity 和 preferredFieldName）；
 *   待确认：读取项还没有首选引用。
 * 项目列表只列首选和待确认，次选只在第二步来源列表里出现；不支持的来源不列。
 * 标记只用在这批字段里能列出两个以上来源的读取项上，单来源的读取项没有可选的，不标：
 * 还没有首选的，各来源标「待确认」；第二步来源列表按后端事实标出首选。
 * 真实目录里大多数字段是单来源、在首次核定前都是 unverified，标出来只是噪音。
 * reading 为 null 的字段照原样列出，不加标记。
 * 已经放到卡片上的字段一直显示它自己的原值，不因项目列表筛选隐藏或换成别的来源，只按同样规则加标记。
 * 调度用的来源由后端决定，不读这里。
 */

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
 * 一批字段（一份目录、一次订阅结果）的来源显示。先按读取项 id 数出每个读取项的可用来源，
 * 再逐个字段给出是否在项目列表列出和标记。
 */
export function readingSourceDisplays(
  readings: Iterable<MetricFieldReading | null>
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
      listed: role === "preferred" || role === "pending",
      mark: (listedSources.get(reading.id) ?? 0) < 2
        ? null
        : role === "pending" ? "pending" : null
    };
  };
}
