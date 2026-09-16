/**
 * 控制面的线上形状。
 *
 * 一个可控对象就是一块卡、一个风扇、一颗 CPU。身份沿用监控侧的标识，
 * 所以同一块卡在监控页和控制页是同一个东西。
 */
export interface ControlObjectPlatform {
  operatingSystem: string;
  vendor: string;
}

export interface ControlNumberRange {
  minimum: number;
  maximum: number;
  step: number;
  unit: string;
  /** 厂商/系统默认值，「恢复默认」回到这里。读不到就是 null。 */
  defaultValue: number | null;
}

export interface ControlCapability {
  id: string;
  label: string;
  valueKind: "toggle" | "number" | "curve";
  /** 现在能不能用。不能用时一定带着原因。 */
  supported: boolean;
  unavailableReason: string | null;
  requiredComponentId: string | null;
  requiredComponentName: string | null;
  range: ControlNumberRange | null;
}

export interface ControlObject {
  id: string;
  kind: string;
  displayName: string;
  platform: ControlObjectPlatform;
  capabilities: readonly ControlCapability[];
  detail: string | null;
  /**
   * 显卡的接法：integrated（核显）/ discrete（独显）。非显卡为 null。
   * 核显的频率和功耗由 CPU 封装管，能调的比独显少。
   */
  gpuAttachment: string | null;
  /** 一项都控不了。界面据此整体标灰。 */
  isControllable: boolean;
}

export interface ControlObjectCatalog {
  objects: readonly ControlObject[];
  readAt: string;
}

/** 风扇曲线上的一个点。 */
export interface ControlCurvePoint {
  temperatureCelsius: number;
  percent: number;
}

/** 用户对某一项能力的设定。三种取值按能力的 valueKind 三选一。 */
export interface ControlSetting {
  capabilityId: string;
  number?: number | null;
  toggle?: boolean | null;
  curve?: readonly ControlCurvePoint[] | null;
}

export interface ControlObjectDesiredState {
  objectId: string;
  settings: readonly ControlSetting[];
}

/**
 * 期望状态：用户想让这台机器保持成什么样。
 * 后端持久化并反复维持 —— 设一次就一直是那样，不会因为重启或驱动重置就回到默认。
 */
export interface ControlDesiredState {
  objects: readonly ControlObjectDesiredState[];
}

export interface ControlApplyOutcome {
  objectId: string;
  capabilityId: string;
  status: "applied" | "unsupported" | "failed";
  message: string | null;
}

export interface ControlApplyReport {
  outcomes: readonly ControlApplyOutcome[];
  appliedAt: string;
}

/** 期望状态 + 最近一次施加的回执。实际状态以回执为准，前端不自己推断。 */
export interface ControlStateView {
  desired: ControlDesiredState;
  lastApply: ControlApplyReport;
}
