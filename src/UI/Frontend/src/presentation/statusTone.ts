import type { ControlItemStatus } from "../features/control/controlStateMachine.ts";

/**
 * 状态标签的严重度。标签把它写进 data-tone，颜色由 styles/status-tone.css 统一给出；
 * 调用处只决定"这是哪一级"，不写颜色。强调色不是任何一级：它只表示选中和可操作。
 */
export type StatusTone = "normal" | "info" | "warning" | "error" | "neutral";

/**
 * 软件问题标签。后端的严重度是 info / warning / critical；
 * 不认识的取值按中性显示，不往任何一级上靠。
 */
export function softwareIssueTone(severity: string): StatusTone {
  switch (severity) {
    case "critical": return "error";
    case "warning": return "warning";
    case "info": return "info";
    default: return "neutral";
  }
}

/** 控制项的应用状态。未设定的项不挂标签，这里仍给出中性，类型上完整。 */
export function controlItemTone(status: ControlItemStatus): StatusTone {
  switch (status) {
    case "applied": return "normal";
    case "edited":
    case "applying": return "info";
    case "failed":
    case "unsupported": return "error";
    case "unset": return "neutral";
  }
}

/** 组件卡片上由组件状态给出的标签：安装包缺失要留意，有更新只是提示。 */
export function componentStateTone(state: string): StatusTone {
  switch (state) {
    case "payload_missing": return "warning";
    case "update_available": return "info";
    default: return "neutral";
  }
}

/** 任务中心每条任务的状态文字：成功是正常，失败和状态不确定是错误，其余（排队、进行、取消）是中性。 */
export function taskStatusTone(status: string, stateUncertain: boolean): StatusTone {
  if (status === "failed" || stateUncertain) return "error";
  return status === "succeeded" ? "normal" : "neutral";
}
