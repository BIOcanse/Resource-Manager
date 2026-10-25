// 参数（协议第 5 节）：只有一套名字，按部件的嵌套一层层往里映射：来源 → 全局 → 最外层的母部件 → … → 母部件 → 本部件，
// 范围小的覆盖大的（用户 10-24：「选择某个部件后，能够看到这个部件视角下的配置区（映射）……一直到全局」）。
// 每个组件实例得到一份完整的值表；输入层的导出量、事实、取值、力场、通道和宿主映射都只读这一份。

export interface SystemReadings {
  doubleClickTimeMs: number;
  doubleClickWidthPx: number;
  doubleClickHeightPx: number;
  dragWidthPx: number;
  dragHeightPx: number;
  keyboardDelayStep: number | null;
  keyboardSpeedStep: number | null;
  wheelScrollLines: number | null;
}

/** 协议的参数（第 5 节的表）。场、手的映射和取值还可以声明自己的参数，名字是任意字符串（第 5 节“场和取值自己的参数”）。 */
export type BaseParamId =
  | "doubleClickTime"
  | "doubleClickWidth"
  | "doubleClickHeight"
  | "dragWidth"
  | "dragHeight"
  | "keyboardDelay"
  | "keyboardRate"
  | "holdRepeatDelay"
  | "holdRepeatInterval"
  | "wheelLines"
  | "velocityWindow"
  | "releaseAbsorb"
  | "fieldStrength"
  | "fieldLength"
  | "kineticFriction"
  | "staticFriction"
  | "springTime"
  | "pointAbsorb"
  | "boundAbsorb"
  | "settleTime"
  | "directionAngle";
export type ParamId = BaseParamId | (string & {});

type Source =
  | { kind: "system"; api: string; read: (s: SystemReadings) => number | null; fallback: number }
  | { kind: "token"; token: string; value: number }
  | { kind: "protocol"; value: number }
  /** 场、手的映射或取值自己声明的参数：by 是谁声明的，例如“场「滚动」”。 */
  | { kind: "declared"; by: string; value: number };

export interface ParamSpec {
  id: ParamId;
  label: string;
  unit: string;
  source: Source;
  readers: string;
}

// Windows 文档：键盘延迟 0–3 档对应约 250–1000 ms；速度 0–31 档对应约 2.5–30 次/秒。
const delayMs = (step: number | null) => (step === null ? null : 250 * (step + 1));
const rateHz = (step: number | null) => (step === null ? null : Math.round((2.5 + (step * 27.5) / 31) * 10) / 10);

/** 场、手的映射或取值声明一项自己的参数。 */
export const declared = (by: string, id: string, label: string, unit: string, value: number, readers: string): ParamSpec =>
  ({ id, label, unit, readers, source: { kind: "declared", by, value } });

export const PARAM_SPECS: readonly ParamSpec[] = [
  { id: "doubleClickTime", label: "双击时间", unit: "ms", readers: "输入层：连按次数",
    source: { kind: "system", api: "GetDoubleClickTime", read: (s) => s.doubleClickTimeMs, fallback: 500 } },
  { id: "doubleClickWidth", label: "双击范围宽", unit: "px", readers: "输入层：连按次数",
    source: { kind: "system", api: "GetSystemMetricsForDpi(SM_CXDOUBLECLK, 96)", read: (s) => s.doubleClickWidthPx, fallback: 4 } },
  { id: "doubleClickHeight", label: "双击范围高", unit: "px", readers: "输入层：连按次数",
    source: { kind: "system", api: "GetSystemMetricsForDpi(SM_CYDOUBLECLK, 96)", read: (s) => s.doubleClickHeightPx, fallback: 4 } },
  { id: "dragWidth", label: "拖动框宽", unit: "px", readers: "事实：离开拖动框",
    source: { kind: "system", api: "GetSystemMetricsForDpi(SM_CXDRAG, 96)", read: (s) => s.dragWidthPx, fallback: 4 } },
  { id: "dragHeight", label: "拖动框高", unit: "px", readers: "事实：离开拖动框",
    source: { kind: "system", api: "GetSystemMetricsForDpi(SM_CYDRAG, 96)", read: (s) => s.dragHeightPx, fallback: 4 } },
  { id: "keyboardDelay", label: "键盘重复延迟", unit: "ms", readers: "只用于日志解释（重复脉冲由浏览器按它产生）",
    source: { kind: "system", api: "SPI_GETKEYBOARDDELAY", read: (s) => delayMs(s.keyboardDelayStep), fallback: 500 } },
  { id: "keyboardRate", label: "键盘重复速度", unit: "次/秒", readers: "只用于日志解释（重复脉冲由浏览器按它产生）",
    source: { kind: "system", api: "SPI_GETKEYBOARDSPEED", read: (s) => rateHz(s.keyboardSpeedStep), fallback: 30 } },
  { id: "holdRepeatDelay", label: "按住重复延迟", unit: "ms", readers: "运行时：独占指针的组件进入读“按住重复”的模式后，过它推出第一次按住重复",
    source: { kind: "protocol", value: 400 } },
  { id: "holdRepeatInterval", label: "按住重复间隔", unit: "ms", readers: "运行时：第一次按住重复之后，每过它推出一次",
    source: { kind: "protocol", value: 60 } },
  { id: "wheelLines", label: "滚轮每格行数", unit: "行", readers: "只用于日志解释，和实测的每格 px 对照",
    source: { kind: "system", api: "SPI_GETWHEELSCROLLLINES", read: (s) => s.wheelScrollLines, fallback: 3 } },
  { id: "velocityWindow", label: "速度窗口", unit: "ms", readers: "指针速度；手带着时位置的速度和加速度",
    source: { kind: "protocol", value: 80 } },
  { id: "releaseAbsorb", label: "松手吸能", unit: "", readers: "手放开时吸掉的动能比例，速度乘 √(1 − 它)：0 带着速度，1 不带",
    source: { kind: "protocol", value: 1 } },
  { id: "fieldStrength", label: "吸附场强", unit: "px/s²", readers: "力场「吸附」：点上的大小",
    source: { kind: "protocol", value: 15000 } },
  { id: "fieldLength", label: "吸附场衰减长度", unit: "px", readers: "力场「吸附」：每远离这么多 px，大小乘 1/e；∞ 不衰减",
    source: { kind: "protocol", value: 3 } },
  { id: "kineticFriction", label: "动摩擦", unit: "px/s²", readers: "力场「摩擦」：运动时的减速度；只有摩擦时滑行距离 = 速度² ÷ (2·它)",
    source: { kind: "protocol", value: 1000 } },
  { id: "staticFriction", label: "静摩擦", unit: "px/s²", readers: "力场「摩擦」：静止时场的其余部分不超过它就不动；和吸附力一起推出静止松手的吸附范围",
    source: { kind: "protocol", value: 2000 } },
  { id: "springTime", label: "回点时间", unit: "ms", readers: "力场「回点弹簧」：从静止出发正好这么久到最近的点",
    source: { kind: "protocol", value: 180 } },
  { id: "pointAbsorb", label: "点吸能", unit: "", readers: "特例点：到达输出映射点时吸掉的动能比例；1 停在点上",
    source: { kind: "protocol", value: 1 } },
  { id: "boundAbsorb", label: "边界吸能", unit: "", readers: "特例点：碰到边界时吸掉的动能比例；1 停在边界上",
    source: { kind: "protocol", value: 1 } },
  { id: "settleTime", label: "到位时间", unit: "ms", readers: "到终态：缓存值和物体将停的值不同时，正好这么久带到缓存值",
    source: { kind: "token", token: "--motion-medium（styles/motion.css）", value: 180 } },
  { id: "directionAngle", label: "方向角", unit: "°", readers: "事实：沿运动方向。指针离按下点的位移和运动方向（朝前朝后都算）的夹角不超过它",
    source: { kind: "protocol", value: 45 } },
];

export type ParamOverrides = Partial<Record<ParamId, number>>;

/**
 * 一层：全局，或页面上一个部件（组件或组合）这一层。只写要改的项。
 * 部件这一层有两部分：定义写的（例如开关的松手吸能 0）和配置区里覆盖的，覆盖的盖住定义写的；它作用于这个部件和它里面的部件。
 * 全局这一层只有覆盖的。
 */
export interface ParamLayer {
  /** 全局，或部件的名字。 */
  name: string;
  global: boolean;
  defined: ParamOverrides;
  overrides: ParamOverrides;
}

export const globalLayer = (overrides: ParamOverrides = {}): ParamLayer => ({ name: "全局", global: true, defined: {}, overrides });
export const partLayer = (name: string, defined: ParamOverrides = {}, overrides: ParamOverrides = {}): ParamLayer => ({ name, global: false, defined, overrides });

export interface ResolvedParam {
  id: ParamId;
  label: string;
  unit: string;
  readers: string;
  value: number;
  /** 来源层：系统设置、动效令牌、协议默认、系统读取失败后的默认值，或声明它的场、取值给的默认值。 */
  source: "系统" | "动效令牌" | "协议默认" | "系统读取失败" | "声明";
  sourceDetail: string;
  sourceValue: number;
  /** 最终取自哪一层（layer 是层表里的下标）、那一层定义写的还是覆盖的；都没写时为 null，即来源。 */
  by: { layer: number; name: string; global: boolean; part: "定义" | "覆盖" } | null;
  /** 被盖住的值，按范围从大到小。 */
  covered: { where: string; value: number }[];
}

export type ParamTable = Readonly<Record<ParamId, ResolvedParam>>;

/** 参数值的写法：无穷写作 ∞，其余 6 位有效数字（换算出来的值例如 Dock 弹簧的响应时间是无理数）；值本身不变。 */
export const show = (v: number): string => (v === Infinity ? "∞" : String(Number(v.toPrecision(6))));

const layerName = (l: { name: string; global: boolean; part: "定义" | "覆盖" }) => `${l.global ? "全局" : `部件「${l.name}」`}${l.part}`;

/** 来源的写法：系统、动效令牌带上来源名，其余（协议默认、声明、读取失败）细节里已经写清。日志和配置区共用。 */
export function sourceText(p: Pick<ResolvedParam, "source" | "sourceDetail">): string {
  if (p.source === "系统") return `系统 ${p.sourceDetail}`;
  if (p.source === "动效令牌") return `动效令牌 ${p.sourceDetail}`;
  return p.sourceDetail;
}

/**
 * 按层级联：layers 从范围大到小排列（全局 → 最外层的母部件 → … → 本部件），后面的覆盖前面的；同一层里覆盖的盖住定义写的。
 * extra：组件用到的场、手的映射和取值声明的参数，和协议的参数一样级联。
 */
export function resolveParams(system: SystemReadings | null, layers: readonly ParamLayer[] = [], extra: readonly ParamSpec[] = []): ParamTable {
  const table = {} as Record<ParamId, ResolvedParam>;
  for (const spec of [...PARAM_SPECS, ...extra]) {
    let value: number;
    let source: ResolvedParam["source"];
    let sourceDetail: string;
    const s = spec.source;
    if (s.kind === "system") {
      const read = system ? s.read(system) : null;
      if (read === null || !Number.isFinite(read)) {
        value = s.fallback;
        source = "系统读取失败";
        sourceDetail = `${s.api} 读取失败，用默认值 ${s.fallback}`;
      } else {
        value = read;
        source = "系统";
        sourceDetail = s.api;
      }
    } else if (s.kind === "token") {
      value = s.value;
      source = "动效令牌";
      sourceDetail = s.token;
    } else if (s.kind === "declared") {
      value = s.value;
      source = "声明";
      sourceDetail = `${s.by}给的默认`;
    } else {
      value = s.value;
      source = "协议默认";
      sourceDetail = "协议默认";
    }
    const sourceValue = value;
    let by: ResolvedParam["by"] = null;
    const covered: ResolvedParam["covered"] = [];
    for (let i = 0; i < layers.length; i++) {
      const l = layers[i];
      for (const part of ["定义", "覆盖"] as const) {
        const v = (part === "定义" ? l.defined : l.overrides)[spec.id];
        if (v === undefined) continue;
        covered.push({ where: by ? layerName(by) : sourceText({ source, sourceDetail }), value });
        value = v;
        by = { layer: i, name: l.name, global: l.global, part };
      }
    }
    table[spec.id] = { id: spec.id, label: spec.label, unit: spec.unit, readers: spec.readers, value, source, sourceDetail, sourceValue, by, covered };
  }
  return Object.freeze(table);
}

/** 这一项的值取自哪里，简短写法：例如“系统 GetDoubleClickTime”“部件「开关」定义”“全局覆盖”。 */
export function where(p: ResolvedParam): string {
  return p.by ? layerName(p.by) : sourceText(p);
}

/** 日志里引用参数的统一写法，例如“松手吸能 0（部件「开关」定义；盖住 协议默认 1）”。 */
export function cite(p: ResolvedParam): string {
  const covered = p.covered.length ? `；盖住 ${p.covered.map((c) => `${c.where} ${show(c.value)}`).join("、")}` : "";
  return `${p.label} ${show(p.value)}${p.unit ? ` ${p.unit}` : ""}（${where(p)}${covered}）`;
}
