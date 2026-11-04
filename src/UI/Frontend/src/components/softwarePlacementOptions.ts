import { uiText } from "../text.ts";

export function runtimeSwitchMethodOptions() {
  return [
    ["future_frame_takeover", uiText.softwareDetail.option.futureFrameTakeover],
    ["window_rerender", uiText.softwareDetail.option.windowRerender]
  ] as const;
}

export function cpuMaximumOccupancyOptions() {
  return [
    ["single_ccd", uiText.softwareDetail.option.singleCcd],
    ["all_cores", uiText.softwareDetail.option.allCores]
  ] as const;
}
