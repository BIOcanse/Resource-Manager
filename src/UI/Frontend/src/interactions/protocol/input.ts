// 输入层（协议第 2 节）：历史的唯一持有者。把浏览器事件拆成原语变化，并提供导出量。
// 输入层只存历史；依赖参数的导出量（速度、连按次数）在读取时用读取方实例的值表计算（协议第 5 节）。
// 本文件不碰 DOM；DOM 事件由 domInput.ts 转成 RawInput 送进来。

import type { ParamTable } from "./params.ts";
import { where } from "./params.ts";
import { pushSample, windowRate } from "./samples.ts";

export type Edge = "↑" | "↓";

export const POINTER_BUTTONS = [
  { id: "pointer.left", bit: 1, label: "左键" },
  { id: "pointer.right", bit: 2, label: "右键" },
  { id: "pointer.middle", bit: 4, label: "中键" },
  { id: "pointer.back", bit: 8, label: "后退键" },
  { id: "pointer.forward", bit: 16, label: "前进键" },
] as const;

export type LossId = "loss.blur" | "loss.focus" | "loss.hidden" | "loss.visible" | "loss.pointercancel" | "loss.lostcapture" | "loss.leftzone";

export const LOSS_LABELS: Readonly<Record<LossId, string>> = {
  "loss.blur": "窗口失去焦点",
  "loss.focus": "窗口重新获得焦点",
  "loss.hidden": "页面隐藏",
  "loss.visible": "页面重新可见",
  "loss.pointercancel": "指针被系统取消",
  "loss.lostcapture": "意外失去指针捕获",
  "loss.leftzone": "指针离开响应区",
};

/**
 * 响应区（协议第 2.1 节，用户 10-24「叫响应区吧，这是真边界，再次之外会阻断鼠标输入读取」）：读指针的范围，视口 px，由平台给。
 * 指针在区外时输入层不读指针（位置、键都不读）；离开时记一次输入丢失“指针离开响应区”。
 */
export interface ResponseZone { left: number; top: number; right: number; bottom: number }

/**
 * 指针离开（协议第 4.2 节“指针离开”）：位置变化让事件目标离开了一个组件的元素。不是浏览器给的原语，由运行时按路由的包含关系推出，
 * 只交给离开的那个组件；只有写了它的模式读它。
 */
export const POINTER_LEAVE = "pointer.leave";

/**
 * 按住重复（协议第 2.7 节，用户 10-24「1,2可以，」）：照键盘的自动重复，由时间推出的输入变化 `pointer.repeat⟳`。不是浏览器给的原语，
 * 由运行时推出：左键按住、独占指针的组件进入读它的模式起，过“按住重复延迟”来第一次，之后每过“按住重复间隔”一次，时刻就是该来的时刻；
 * 只交给那个组件，只有写了它的模式读它。
 */
export const HOLD_REPEAT = "pointer.repeat";

/** 原语变化。key 是转移表里写的形式：电平 `id↑`/`id↓`，连续量 `id~`，自动重复 `id⟳`，请求和事件就是 id。 */
export type Change =
  | { kind: "level"; id: string; edge: Edge; corrected: boolean; key: string }
  | { kind: "repeat"; id: string; key: string }
  | { kind: "continuous"; id: "pointer.position" | "wheel"; delta: { x: number; y: number }; key: string }
  | { kind: "request"; id: "request.activate" | "request.contextmenu"; key: string }
  | { kind: "event"; id: LossId | typeof POINTER_LEAVE; key: string };

export interface Modifiers { shift: boolean; ctrl: boolean; alt: boolean; meta: boolean }

/** 浏览器事件转来的原始观测。target 是事件目标，供路由使用；event 用于框架输出“认领”。 */
export type RawInput =
  | { type: "pointer"; t: number; x: number; y: number; buttons: number; pointerId: number; dom: string; modifiers: Modifiers; target: unknown; event?: unknown;
      /** 这个浏览器事件带来的全部采样（按时间，最后一个就是 t、x、y）：都进历史，位置变化只算一次。没有就是只有 t、x、y 一个。 */
      samples?: readonly Sample[] }
  | { type: "wheel"; t: number; x: number; y: number; dx: number; dy: number; deltaMode: number; modifiers: Modifiers; target: unknown; event?: unknown }
  | { type: "key"; t: number; code: string; key: string; down: boolean; repeat: boolean; modifiers: Modifiers; target: unknown; event?: unknown }
  | { type: "activate"; t: number; target: unknown; event?: unknown }
  | { type: "contextmenu"; t: number; x: number; y: number; target: unknown; event?: unknown }
  | { type: "loss"; t: number; id: LossId; target: unknown; event?: unknown }
  /** 浏览器报告指针移到了窗口外（relatedTarget 为空的 pointerout）：窗口外的移动浏览器不报，靠这一条知道出了响应区。 */
  | { type: "pointerout"; t: number; target: unknown; event?: unknown }
  /** 由时间推出的变化（按住重复）：没有浏览器事件，t 是该来的时刻。 */
  | { type: "time"; t: number; target: unknown; event?: unknown };

export interface InputChange {
  seq: number;
  t: number;
  change: Change;
  raw: RawInput;
  /** 这条变化来自哪个浏览器事件，日志用。 */
  source: string;
}

/** 一次按下：时刻、位置，和按下时的事件目标（事实“在组件上按住”用）。 */
interface Press { t: number; x: number; y: number; target: unknown }
interface ClickCount { count: number; explain: string }
interface Sample { t: number; x: number; y: number }

// 历史的资源上限（工程不变量，不是手感参数）：采样的上限见 samples.ts；每个键保留最近 8 次按下。
const PRESS_KEEP = 8;

const MODIFIER_CODES: Readonly<Record<keyof Modifiers, readonly string[]>> = {
  shift: ["ShiftLeft", "ShiftRight"],
  ctrl: ["ControlLeft", "ControlRight"],
  alt: ["AltLeft", "AltRight"],
  meta: ["MetaLeft", "MetaRight"],
};

export function changeLabel(c: Change): string {
  const button = POINTER_BUTTONS.find((b) => b.id === c.id);
  const name = button ? button.label
    : c.id === "pointer.position" ? "位置"
    : c.id === "wheel" ? "滚轮"
    : c.id === "request.activate" ? "激活请求"
    : c.id === "request.contextmenu" ? "上下文菜单请求"
    : c.id.startsWith("key.") ? `键 ${c.id.slice(4)}`
    : c.id === POINTER_LEAVE ? "指针离开"
    : c.id === HOLD_REPEAT ? "按住重复"
    : LOSS_LABELS[c.id as LossId] ?? c.id;
  if (c.kind === "level") return `${name} ${c.edge}${c.corrected ? "（校正）" : ""}`;
  if (c.kind === "repeat") return c.id === HOLD_REPEAT ? name : `${name} 自动重复`;
  if (c.kind === "continuous") return `${name} ~`;
  return name;
}

export class InputLayer {
  private seq = 0;
  private readonly down = new Map<string, Press>();
  /** 每个键最近几次按下，新的在后。 */
  private readonly presses = new Map<string, Press[]>();
  private readonly samples: Sample[] = [];
  private readonly wheelSamples: Sample[] = [];
  private pos = { x: 0, y: 0 };
  private wheelTotal = { x: 0, y: 0 };
  private lossPending = false;
  /** 指针在响应区外：不读指针，直到回到区里。 */
  private outside = false;
  private readonly zone: () => ResponseZone;

  constructor(zone: () => ResponseZone) {
    this.zone = zone;
  }

  /** 送入一次观测。每产生一个原语变化，立即调用 sink；sink 返回前输入层状态停在这一变化上。 */
  feed(raw: RawInput, sink: (c: InputChange) => void): void {
    const emit = (change: Change, source: string) => sink({ seq: ++this.seq, t: raw.t, change, raw, source });
    switch (raw.type) {
      case "pointer": {
        const z = this.zone();
        if (!(raw.x >= z.left && raw.x < z.right && raw.y >= z.top && raw.y < z.bottom)) {
          this.leaveZone(emit, `${raw.dom}：(${raw.x.toFixed(0)}, ${raw.y.toFixed(0)}) 在响应区 (${z.left}, ${z.top})–(${z.right}, ${z.bottom}) 外`);
          return;
        }
        // 回到响应区：照常读，按键按实际值校正（离开时已记下丢失）
        this.outside = false;
        const corrected = this.lossPending;
        this.lossPending = false;
        this.correctModifiers(raw.t, raw.modifiers, emit, raw.dom);
        const before = this.pos;
        for (const s of raw.samples ?? [{ t: raw.t, x: raw.x, y: raw.y }]) {
          if (this.samples.length > 0 && s.x === this.pos.x && s.y === this.pos.y) continue;
          pushSample(this.samples, { t: s.t, x: s.x, y: s.y });
          this.pos = { x: s.x, y: s.y };
        }
        this.pos = { x: raw.x, y: raw.y };
        if (raw.x !== before.x || raw.y !== before.y) {
          emit({ kind: "continuous", id: "pointer.position", delta: { x: raw.x - before.x, y: raw.y - before.y }, key: "pointer.position~" }, raw.dom);
        }
        for (const b of POINTER_BUTTONS) {
          const isDown = (raw.buttons & b.bit) !== 0;
          const wasDown = this.down.has(b.id);
          if (isDown === wasDown) continue;
          // 按下和松开都由自己的事件报告；在 pointermove 里才发现的变化是同时按多个键（正常），
          // 但如果此前发生过输入丢失，就是丢失后的校正。
          const fix = corrected && raw.dom === "pointermove";
          this.setLevel(b.id, isDown, raw.t, raw.target);
          emit({ kind: "level", id: b.id, edge: isDown ? "↑" : "↓", corrected: fix, key: `${b.id}${isDown ? "↑" : "↓"}` }, raw.dom);
        }
        return;
      }
      case "wheel": {
        const line = 16;
        const scale = raw.deltaMode === 1 ? line : raw.deltaMode === 2 ? 800 : 1;
        const delta = { x: raw.dx * scale, y: raw.dy * scale };
        this.wheelTotal = { x: this.wheelTotal.x + delta.x, y: this.wheelTotal.y + delta.y };
        pushSample(this.wheelSamples, { t: raw.t, ...this.wheelTotal });
        this.correctModifiers(raw.t, raw.modifiers, emit, "wheel");
        emit({ kind: "continuous", id: "wheel", delta, key: "wheel~" }, `wheel deltaMode=${raw.deltaMode}`);
        return;
      }
      case "key": {
        const id = `key.${raw.code}`;
        const isModifier = Object.values(MODIFIER_CODES).some((codes) => codes.includes(raw.code));
        if (!isModifier) this.correctModifiers(raw.t, raw.modifiers, emit, raw.down ? "keydown" : "keyup");
        const wasDown = this.down.has(id);
        if (raw.down) {
          if (raw.repeat && wasDown) {
            emit({ kind: "repeat", id, key: `${id}⟳` }, "keydown repeat");
            return;
          }
          if (wasDown) {
            // 按住的键又来一次非重复按下：中间的松开丢了，先校正出松开。
            this.setLevel(id, false, raw.t);
            emit({ kind: "level", id, edge: "↓", corrected: true, key: `${id}↓` }, "keydown");
          }
          this.setLevel(id, true, raw.t);
          emit({ kind: "level", id, edge: "↑", corrected: false, key: `${id}↑` }, "keydown");
        } else if (wasDown) {
          this.setLevel(id, false, raw.t);
          emit({ kind: "level", id, edge: "↓", corrected: false, key: `${id}↓` }, "keyup");
        }
        return;
      }
      case "activate":
        emit({ kind: "request", id: "request.activate", key: "request.activate" }, "click（detail=0，不是指针产生的）");
        return;
      case "contextmenu":
        emit({ kind: "request", id: "request.contextmenu", key: "request.contextmenu" }, "contextmenu");
        return;
      case "pointerout":
        this.leaveZone(emit, "pointerout：浏览器报告指针移到了窗口外（relatedTarget 为空）");
        return;
      case "time":
        return;
      case "loss":
        if (raw.id === "loss.blur" || raw.id === "loss.hidden" || raw.id === "loss.pointercancel" || raw.id === "loss.lostcapture") {
          this.lossPending = true;
        }
        emit({ kind: "event", id: raw.id, key: raw.id }, raw.id.slice(5));
        return;
    }
  }

  /** 指针出了响应区：记一次丢失，之后不读指针，回来时按键按实际值校正。已经在区外就不再记。 */
  private leaveZone(emit: (c: Change, source: string) => void, source: string): void {
    if (this.outside) return;
    this.outside = true;
    this.lossPending = true;
    emit({ kind: "event", id: "loss.leftzone", key: "loss.leftzone" }, source);
  }

  /** 按住重复（由运行时按时刻推出）：输入层只给它编号，不存历史。 */
  holdRepeat(t: number, source: string): InputChange {
    return { seq: ++this.seq, t, change: { kind: "repeat", id: HOLD_REPEAT, key: `${HOLD_REPEAT}⟳` }, raw: { type: "time", t, target: null }, source };
  }

  // ===== 导出量 =====

  position(): { x: number; y: number } {
    return { ...this.pos };
  }

  isDown(id: string): boolean {
    return this.down.has(id);
  }

  press(id: string): Press | null {
    return this.down.get(id) ?? null;
  }

  /** 最近一次按下（键可能已松开）：按下时的位置在松开那一刻仍要用。 */
  lastPress(id: string): Press | null {
    return this.presses.get(id)?.at(-1) ?? null;
  }

  holdTime(id: string, t: number): number | null {
    const p = this.down.get(id);
    return p ? t - p.t : null;
  }

  /**
   * 连按次数（最近一次按下算起）：往前逐次比较，相邻两次按下的间隔不超过双击时间、
   * 且落在以前一次按下点为中心的双击范围内，就加 1，否则停。参数用读取方的值表。
   */
  clickCount(id: string, params: ParamTable): ClickCount | null {
    const list = this.presses.get(id);
    if (!list?.length) return null;
    const T = params.doubleClickTime;
    const w = params.doubleClickWidth;
    const h = params.doubleClickHeight;
    let count = 1;
    let i = list.length - 1;
    let explain = "连按次数 1（此前没有按过）";
    while (i > 0) {
      const cur = list[i];
      const prev = list[i - 1];
      const dt = cur.t - prev.t;
      const dx = Math.abs(cur.x - prev.x);
      const dy = Math.abs(cur.y - prev.y);
      const inTime = dt <= T.value;
      const inRect = dx <= w.value / 2 && dy <= h.value / 2;
      if (i === list.length - 1) {
        explain = `距上次按下 ${dt.toFixed(0)} ms ${inTime ? "≤" : ">"} 双击时间 ${T.value} ms（${where(T)}），偏移 (${dx.toFixed(1)}, ${dy.toFixed(1)}) px ${inRect ? "在" : "超出"}双击范围 ${w.value}×${h.value} px（${where(w)}）的半宽高内`;
      }
      if (!inTime || !inRect) break;
      count++;
      i--;
    }
    return { count, explain: `连按次数 ${count}：${explain}` };
  }

  wheelAmount(): { x: number; y: number } {
    return { ...this.wheelTotal };
  }

  /** 速度：窗口 W 内的位移 ÷ 窗口时长。窗口不早于最近一次指针按下（松开那一刻也算），不把按下前的静止算进来。参数用读取方的值表。 */
  velocity(t: number, params: ParamTable): { x: number; y: number; explain: string } {
    const W = params.velocityWindow.value;
    let from = t - W;
    for (const b of POINTER_BUTTONS) {
      const p = this.lastPress(b.id);
      if (p && p.t > from) from = p.t;
    }
    const rx = windowRate(this.samples, (s) => s.x, from, t, this.pos.x);
    const ry = windowRate(this.samples, (s) => s.y, from, t, this.pos.y);
    const span = rx.span;
    if (span <= 0 || this.samples.length === 0) return { x: 0, y: 0, explain: "速度 0（窗口时长为 0）" };
    const at = { x: this.pos.x - rx.delta, y: this.pos.y - ry.delta };
    const v = { x: rx.rate, y: ry.rate };
    const inWindow = this.samples.filter((s) => s.t > from && s.t <= t).length;
    const clipped = span < W ? `，窗口从本次按下开始，只有 ${span.toFixed(1)} ms` : "";
    return {
      ...v,
      explain: `速度 (${v.x.toFixed(0)}, ${v.y.toFixed(0)}) px/s = 位移 (${(this.pos.x - at.x).toFixed(1)}, ${(this.pos.y - at.y).toFixed(1)}) px ÷ ${span.toFixed(1)} ms（速度窗口 ${W} ms，${where(params.velocityWindow)}${clipped}；窗口内 ${inWindow} 个采样）`,
    };
  }

  // ===== 内部 =====

  private setLevel(id: string, isDown: boolean, t: number, target: unknown = null): void {
    if (!isDown) {
      this.down.delete(id);
      return;
    }
    const press = { t, x: this.pos.x, y: this.pos.y, target };
    this.down.set(id, press);
    const list = this.presses.get(id) ?? [];
    list.push(press);
    if (list.length > PRESS_KEEP) list.shift();
    this.presses.set(id, list);
  }

  private correctModifiers(t: number, m: Modifiers, emit: (c: Change, source: string) => void, dom: string): void {
    for (const [name, codes] of Object.entries(MODIFIER_CODES) as [keyof Modifiers, readonly string[]][]) {
      if (m[name]) continue;
      for (const code of codes) {
        const id = `key.${code}`;
        if (!this.down.has(id)) continue;
        this.setLevel(id, false, t);
        emit({ kind: "level", id, edge: "↓", corrected: true, key: `${id}↓` }, `${dom} 自带的修饰键状态`);
      }
    }
  }
}
