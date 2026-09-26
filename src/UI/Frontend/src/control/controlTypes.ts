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

/**
 * 调节权限的两档，从低到高。
 *
 * - normal：调错了机器会不稳定但硬件不受损（电压、频率偏移、温度墙）。重启能恢复。
 * - root：没有兜底（直接写电压、改基准时钟）。平时用不到，但确实存在。
 *
 * **不是安全闸，是标签。** 程序本来就要管理员才起得来，切档位就在设置里两下的事。
 * 它管的是别手滑，以及让用户看得出哪些项危险。
 */
export type ControlAccessLevel = "normal" | "root";

/** 从低到高。顺序就是包含关系。 */
export const controlAccessLevels: readonly ControlAccessLevel[] = ["normal", "root"];

/** 处在 current 这一档，够得着要 required 的项吗。 */
export function controlAccessLevelAllows(
  current: ControlAccessLevel,
  required: ControlAccessLevel
): boolean {
  return controlAccessLevels.indexOf(current) >= controlAccessLevels.indexOf(required);
}

/**
 * 一项能力用不了的种类。和后端 ControlUnavailableKinds 同一串字面量。
 *
 * - platform：这台机器或这条通道做不到，换档位装组件都没用 → 打叉
 * - access-level：档位不够，切一档就能用 → 挂锁
 * - component：缺个组件，装上就有
 * - prerequisite：机器上另一个条件没满足，满足了就能用 → 挂锁
 * - not-implemented：硬件做得到，是我们还没接
 */
export type ControlUnavailableKind =
  | "platform"
  | "access-level"
  | "component"
  | "firmware-owned"
  | "prerequisite"
  | "not-implemented";

export const controlUnavailableKinds: readonly ControlUnavailableKind[] = [
  "platform", "access-level", "component", "firmware-owned",
  "prerequisite", "not-implemented"
];

export interface ControlCapability {
  id: string;
  label: string;
  valueKind: "toggle" | "number" | "curve";
  /** 现在能不能用。不能用时一定带着原因。 */
  supported: boolean;
  unavailableReason: string | null;
  /**
   * 用不了属于哪一类。能用时为 null。
   *
   * **界面靠它选图标**：能解开的挂锁，解不开的打叉。先前全都画成锁，
   * "切个档位就能用"和"这台机器压根没这功能"长得一模一样。
   */
  unavailableKind: ControlUnavailableKind | null;
  requiredComponentId: string | null;
  requiredComponentName: string | null;
  range: ControlNumberRange | null;
  /**
   * 这一项实际走哪条链路（nvapi / nvml / oem-ec / amd-smu / fan-core …）。
   * 还没有写入器认领时为 null。
   *
   * **摆在每一行上**，因为同一块卡的不同项走的是完全不同的通道：
   * 频率偏移走 NVAPI、功耗上限走 NVML、cTGP 走整机厂的 EC。
   * 看到"这一项调不了"时，第一个该知道的就是是谁说不行。
   */
  channel: string | null;
  /**
   * 这一项要哪一档调节权限。
   *
   * **够得着也要显示。** 这个标记的作用之一就是让用户看出哪些项算危险，
   * 只在锁着时才冒出来就白设了。
   */
  requiredAccessLevel: ControlAccessLevel;
}

export interface ControlObject {
  id: string;
  kind: string;
  displayName: string;
  platform: ControlObjectPlatform;
  capabilities: readonly ControlCapability[];
  detail: string | null;
  /**
   * 这个对象属于哪几类。**词条，不是名字。**
   *
   * 例如笔记本上的一个风扇：名字是「风扇1」，词条是 portable。
   * 名字保持中立（我们只知道它是第几个），分类另说。
   */
  terms: readonly string[];
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
  /**
   * 这条曲线交给谁执行：`firmware` 写进固件、`software` 由本程序跑。
   *
   * **是用户的选择，所以跟着设定一起存。** 同一条曲线交给两边，行为完全不同：
   * 固件那条关掉程序还在跑，软件那条不在。
   */
  curveExecution?: string | null;
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
 * **配置绑定实例**：一份配置属于某一块卡、某一颗处理器，所以选中一个实例
 * 就能看到为它存过的几套方案，不必在混着别的设备的配置里挑。
 *
 * 点一份配置只是把内容载入草稿，落到硬件仍然要点应用。
 */
export interface ControlPreset {
  id: string;
  /** 这份配置属于哪个实例。 */
  objectId: string;
  name: string;
  settings: readonly ControlSetting[];
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
