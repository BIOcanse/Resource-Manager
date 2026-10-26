/*
 * 资源事实帧（/api/resource-facts，version 2）里一个指标的 round-4 事实键，形状同后端 ResourceMetricFacts
 * （System.Text.Json 写全部键）。总量、之和、残差、可加性和时刻由调用方写明；其余键默认是后端没有值时写的 null。
 */
export interface FactsMetricKeys {
  totalMetricId: string | null;
  processSumValue: number | null;
  residualValue: number | null;
  additiveAcrossProcesses: boolean;
  capturedAt: string | null;
  residualSource?: "counters" | null;
  quantity?: string | null;
  unavailableReason?: string | null;
  windowMilliseconds?: number | null;
  gpuEngine?: { adapterLuid: number; physicalIndex: number; engineIndex: number } | null;
  engineClass?: string | null;
}

export function factsMetricKeys(keys: FactsMetricKeys) {
  return { residualSource: null, quantity: null, unavailableReason: null, windowMilliseconds: null, gpuEngine: null, engineClass: null, ...keys };
}

/** 进程明细（version 1）里一个指标的事实键，形状同 ResourceProcessDetailsWireSnapshotJsonConverter。 */
export function detailsMetricKeys(keys: { totalMetricId: string | null; totalValue: number | null; capacityValue: number | null;
  processSumValue: number | null; residualValue: number | null; additiveAcrossProcesses: boolean; capturedAt: string | null }) {
  return keys;
}
