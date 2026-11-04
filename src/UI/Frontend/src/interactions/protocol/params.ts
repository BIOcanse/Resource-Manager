// Cascade one parameter namespace from source through global and ancestor layers to the component.
// Narrower scopes override wider scopes.
// Each instance shares one resolved table across input, facts, values, fields, channels, and host mapping.

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

/** Built-in parameters; fields, hand maps, and values may declare additional names. */
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
  | "outputSteps"
  | "directionAngle";
export type ParamId = BaseParamId | (string & {});

type Source =
  | { kind: "system"; api: string; read: (s: SystemReadings) => number | null; fallback: number }
  | { kind: "token"; token: string; value: number }
  | { kind: "protocol"; value: number }
  /** Custom parameter declaration, including its declaring owner. */
  | { kind: "declared"; by: string; value: number };

export interface ParamSpec {
  id: ParamId;
  label: string;
  unit: string;
  source: Source;
  readers: string;
}

// Windows keyboard delay levels map to roughly 250-1000ms; speed levels map to roughly 2.5-30 repeats per second.
const delayMs = (step: number | null) => (step === null ? null : 250 * (step + 1));
const rateHz = (step: number | null) => (step === null ? null : Math.round((2.5 + (step * 27.5) / 31) * 10) / 10);

/** Declare a custom parameter for a field, hand map, or value. */
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
  { id: "outputSteps", label: "输出步数", unit: "步", readers: "运行时：力场带着物体时，画面把这一段的推进时长分成这么多步，每步结束时跳一次（和 CSS 的 steps(n) 一样）；0 是连续。手带着时不分，状态、落点和宿主值不受影响",
    source: { kind: "protocol", value: 0 } },
  { id: "directionAngle", label: "方向角", unit: "°", readers: "事实：沿运动方向。指针离按下点的位移和运动方向（朝前朝后都算）的夹角不超过它",
    source: { kind: "protocol", value: 45 } },
];

export type ParamOverrides = Partial<Record<ParamId, number>>;

/** Global or component layer. Overrides beat definition values within the layer; component layers also affect descendants. */
export interface ParamLayer {
  /** Global or component layer name. */
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
  /** Source value from system settings, motion tokens, defaults, or a custom declaration. */
  source: "系统" | "动效令牌" | "协议默认" | "系统读取失败" | "声明";
  sourceDetail: string;
  sourceValue: number;
  /** Winning layer and whether it was a definition or override; null denotes the source. */
  by: { layer: number; name: string; global: boolean; part: "定义" | "覆盖" } | null;
  /** Overridden values from broadest to narrowest scope. */
  covered: { where: string; value: number }[];
}

export type ParamTable = Readonly<Record<ParamId, ResolvedParam>>;

/** Format infinity and six significant digits without changing the value. */
export const show = (v: number): string => (v === Infinity ? "∞" : String(Number(v.toPrecision(6))));

const layerName = (l: { name: string; global: boolean; part: "定义" | "覆盖" }) => `${l.global ? "全局" : `部件「${l.name}」`}${l.part}`;

/** Shared source labels for traces and parameter views. */
export function sourceText(p: Pick<ResolvedParam, "source" | "sourceDetail">): string {
  if (p.source === "系统") return `系统 ${p.sourceDetail}`;
  if (p.source === "动效令牌") return `动效令牌 ${p.sourceDetail}`;
  return p.sourceDetail;
}

/** Apply layers from broadest to narrowest scope, with overrides beating definitions. Custom declarations cascade like built-ins. */
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

/** Compact description of the winning parameter source. */
export function where(p: ResolvedParam): string {
  return p.by ? layerName(p.by) : sourceText(p);
}

/** Consistent trace formatting for a value, its source, and overridden sources. */
export function cite(p: ResolvedParam): string {
  const covered = p.covered.length ? `；盖住 ${p.covered.map((c) => `${c.where} ${show(c.value)}`).join("、")}` : "";
  return `${p.label} ${show(p.value)}${p.unit ? ` ${p.unit}` : ""}（${where(p)}${covered}）`;
}
