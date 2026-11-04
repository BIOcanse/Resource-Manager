import { localizedMetricLabel } from "../../../presentation/metricLabels.ts";
import { uiText } from "../../../text.ts";
import { itemName } from "../../itemSelection/itemNames.ts";
import type { SeriesLabels } from "./reportSeriesAdapter.ts";

/** Share localized series names between construction and missing-series messages. */
export function localizedSeriesLabels(): SeriesLabels {
  return {
    frameTime: uiText.targetedReport.views.series.frameTime,
    frameRate: uiText.targetedReport.views.series.frameRate,
    software: uiText.targetedReport.software,
    system: uiText.targetedReport.system,
    metric: localizedMetricLabel,
    item: itemName
  };
}
