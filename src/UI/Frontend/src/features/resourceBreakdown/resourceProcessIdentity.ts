import type { ResourceProcessSegment } from "../../types.ts";

/**
 * 系统/驱动保留：后端用 displayKind 标明整机量与软件之和的差额，它不是软件。
 * 资源条按它画灰色斜纹，展开后它下面的每一项也一样（见 isReservedProcess）。
 */
export function isReservedSegment(segment: { displayKind?: string | null }) {
  return segment.displayKind === "system-residual";
}

/**
 * 进程分解里按“保留”画的项：系统/驱动保留条目下面的每一项（物理内存的计数器叶子：PID -1、attributionKind 是
 * system-memory/... 的分项，见 docs/runtime_process_attribution_and_io_network_telemetry.md），
 * 以及后端标成 system-residual 的未拆分残差。按所属条目和 attributionKind 判断，不按分项标识的前缀。
 */
export function isReservedProcess(software: { displayKind?: string | null }, process: ResourceProcessSegment) {
  return isReservedSegment(software) || process.attributionKind === "system-residual";
}

/**
 * 进程分解里一项的标识（键、悬停、选中、配色散列都用它）：PID、启动标识、attributionKind 和名字一起。
 * 只用 PID 不够：多个残差叶子都是 PID -1、名字为空，靠 attributionKind（分项种类）区分。
 */
export function resourceProcessKey(process: ResourceProcessSegment) {
  return [process.processId, process.processStartKey ?? "", process.attributionKind ?? "", process.name].join("|");
}
