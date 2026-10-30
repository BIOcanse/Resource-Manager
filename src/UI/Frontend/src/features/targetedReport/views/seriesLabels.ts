import { localizedMetricLabel } from "../../../presentation/metricLabels.ts";
import { uiText } from "../../../text.ts";
import type { SeriesLabels } from "./reportSeriesAdapter.ts";

/** 序列名称用当前界面语言的文案（报告页建序列、视图上写“没有这条序列”时都用它）。 */
export function localizedSeriesLabels(): SeriesLabels {
  return {
    frameTime: uiText.targetedReport.views.series.frameTime,
    frameRate: uiText.targetedReport.views.series.frameRate,
    software: uiText.targetedReport.software,
    system: uiText.targetedReport.system,
    metric: localizedMetricLabel
  };
}
