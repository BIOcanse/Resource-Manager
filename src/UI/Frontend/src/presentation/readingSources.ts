import type { MetricFieldReading } from "../data/metricFields/metricFieldSource.ts";
import type { ReadingSourceDisplay } from "../types.ts";

/* Use backend reading metadata only. Pickers show preferred and pending sources; secondary sources appear only in source selection, unsupported sources are hidden. Mark only readings with multiple usable sources. Fields without metadata stay visible and unmarked. Existing cards retain their own values; scheduling source selection belongs to the backend. */

export type { ReadingSourceDisplay };
export type ReadingSourceMark = NonNullable<ReadingSourceDisplay["mark"]>;

type ReadingSourceRole = "preferred" | "alternate" | "pending" | "unsupported";

const plainField: ReadingSourceDisplay = Object.freeze({ listed: true, mark: null });

function sourceRole(reading: MetricFieldReading): ReadingSourceRole {
  if (reading.isPreferredSource) return "preferred";
  if (reading.sourceAvailability === "unsupported") return "unsupported";
  return reading.preferredDeviceIdentity !== null && reading.preferredFieldName !== null ? "alternate" : "pending";
}

/** Count usable sources by reading ID, then compute visibility and marks for each field in this catalog or subscription batch. */
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
