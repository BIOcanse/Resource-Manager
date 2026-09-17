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

/**
 * 用户对某一项能力的设定。三种取值按能力的 valueKind 三选一。
 *
 * 数值型自带单位：这条记录要能单独看懂 —— 它会被存下来、保存成配置、
 * 在机器之间搬。单位挂在能力描述上的话，换台机器同一条记录的含义就变了。
 */
export interface ControlSetting {
  capabilityId: string;
  number?: number | null;
  toggle?: boolean | null;
  curve?: readonly ControlCurvePoint[] | null;
  /** number 的单位，取自能力的 range.unit。非数值型不填。 */
  unit?: string | null;
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

/**
 * 登记过的一台设备。
 * 实例一旦登记就不再消失，只在"在场/不在场"之间切换 —— 配置挂在它上面，
 * 所以拔掉一块卡不会让它的设定变成孤儿。
 */
export interface ControlInstance {
  id: string;
  kind: string;
  displayName: string;
  platform: ControlObjectPlatform;
  gpuAttachment: string | null;
  /** 这个身份能不能唯一认出这台设备。不能的话如实标出来，不假装。 */
  identityIsUnique: boolean;
  firstSeenAt: string;
  lastSeenAt: string;
  /** 登记时生成的默认配置，「恢复默认」用它。 */
  defaultSettings: readonly ControlSetting[];
}

export interface ControlInstanceView {
  instance: ControlInstance;
  isPresent: boolean;
}

export interface ControlInstanceCatalog {
  instances: readonly ControlInstanceView[];
  readAt: string;
}

/**
 * 一份存下来的配置：用户给它起了名字的一整套设定。
 *
 * 配置和期望状态是两回事：期望状态只有一份，是"机器现在该保持成什么样"；
 * 配置可以有很多份，是"我攒下来的几套方案"。点一份配置只是把内容载入草稿，
 * 落到硬件仍然要点应用。
 */
export interface ControlPreset {
  id: string;
  name: string;
  desired: ControlDesiredState;
  updatedAt: string;
}

export interface ControlPresetCatalog {
  presets: readonly ControlPreset[];
}

/**
 * 某一项能力**现在实际**是什么值。
 *
 * 和期望值并排显示，所以一样自带单位。读不到时给原因 ——
 * 读不到和"读到 0"是两回事。
 */
export interface ControlActualValue {
  objectId: string;
  capabilityId: string;
  number?: number | null;
  toggle?: boolean | null;
  curve?: readonly ControlCurvePoint[] | null;
  unit?: string | null;
  unreadableReason?: string | null;
}

/**
 * 实际状态：这台机器现在实际是什么样。
 *
 * **不是期望状态的回声** —— 固件会按温度自己调度，用户也可能用别的软件改过。
 * 两者对不上是常态，而且正是用户需要看见的信息。
 */
export interface ControlActualState {
  values: readonly ControlActualValue[];
  readAt: string;
}
