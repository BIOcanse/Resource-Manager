// The input layer owns history, emits primitive changes, and derives values.
// Derive parameter-dependent velocity and press counts using the reading instance parameter table.
// DOM adaptation supplies RawInput; this layer does not access the DOM.

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

/** Platform response area in viewport pixels. Outside it, ignore pointer position and buttons; record one input loss on departure. */
export interface ResponseZone { left: number; top: number; right: number; bottom: number }

/** Runtime-derived component leave from routing containment; deliver only to that component and modes reading it. */
export const POINTER_LEAVE = "pointer.leave";

/** Runtime-timed pointer repeat starts after the delay in a reading mode with left held and pointer ownership, then repeats at scheduled intervals. */
export const HOLD_REPEAT = "pointer.repeat";

/** Primitive change key: level rise/fall, continuous change, repeat, request, or event. */
export type Change =
  | { kind: "level"; id: string; edge: Edge; corrected: boolean; key: string }
  | { kind: "repeat"; id: string; key: string }
  | { kind: "continuous"; id: "pointer.position" | "wheel"; delta: { x: number; y: number }; key: string }
  | { kind: "request"; id: "request.activate" | "request.contextmenu"; key: string }
  | { kind: "event"; id: LossId | typeof POINTER_LEAVE; key: string };

export interface Modifiers { shift: boolean; ctrl: boolean; alt: boolean; meta: boolean }

/** Raw browser observation; target routes input and event supports claiming. */
export type RawInput =
  | { type: "pointer"; t: number; x: number; y: number; buttons: number; pointerId: number; dom: string; modifiers: Modifiers; target: unknown; event?: unknown;
      /** Ordered coalesced samples ending at t/x/y; retain all history but emit one position change. */
      samples?: readonly Sample[] }
  | { type: "wheel"; t: number; x: number; y: number; dx: number; dy: number; deltaMode: number; modifiers: Modifiers; target: unknown; event?: unknown }
  | { type: "key"; t: number; code: string; key: string; down: boolean; repeat: boolean; modifiers: Modifiers; target: unknown; event?: unknown }
  | { type: "activate"; t: number; target: unknown; event?: unknown }
  | { type: "contextmenu"; t: number; x: number; y: number; target: unknown; event?: unknown }
  | { type: "loss"; t: number; id: LossId; target: unknown; event?: unknown }
  /** Viewport exit observation when pointerout has no related target. */
  | { type: "pointerout"; t: number; target: unknown; event?: unknown }
  /** Timed change without a browser event; t is its scheduled time. */
  | { type: "time"; t: number; target: unknown; event?: unknown };

export interface InputChange {
  seq: number;
  t: number;
  change: Change;
  raw: RawInput;
  /** Browser event source for traces. */
  source: string;
}

/** Press time, position, and event target. */
interface Press { t: number; x: number; y: number; target: unknown }
interface ClickCount { count: number; explain: string }
interface Sample { t: number; x: number; y: number }

// Bound press history to eight per key; sample limits live in samples.ts.
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
  /** Recent presses per key, oldest first. */
  private readonly presses = new Map<string, Press[]>();
  private readonly samples: Sample[] = [];
  private readonly wheelSamples: Sample[] = [];
  private pos = { x: 0, y: 0 };
  private wheelTotal = { x: 0, y: 0 };
  private lossPending = false;
  /** Ignore pointer input outside the response area until reentry. */
  private outside = false;
  private readonly zone: () => ResponseZone;

  constructor(zone: () => ResponseZone) {
    this.zone = zone;
  }

  /** Emit changes synchronously; state remains at each change until sink returns. */
  feed(raw: RawInput, sink: (c: InputChange) => void): void {
    const emit = (change: Change, source: string) => sink({ seq: ++this.seq, t: raw.t, change, raw, source });
    switch (raw.type) {
      case "pointer": {
        const z = this.zone();
        if (!(raw.x >= z.left && raw.x < z.right && raw.y >= z.top && raw.y < z.bottom)) {
          this.leaveZone(emit, `${raw.dom}：(${raw.x.toFixed(0)}, ${raw.y.toFixed(0)}) 在响应区 (${z.left}, ${z.top})–(${z.right}, ${z.bottom}) 外`);
          return;
        }
        // On reentry, resume reads and correct held buttons against actual levels.
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
          // Changes discovered by pointermove may be simultaneous button changes.
          // After input loss, treat discovered changes as corrections.
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
            // A nonrepeat press on an already-held key implies a missing release; correct it first.
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

  /** Record departure once and ignore pointer reads until reentry corrects button levels. */
  private leaveZone(emit: (c: Change, source: string) => void, source: string): void {
    if (this.outside) return;
    this.outside = true;
    this.lossPending = true;
    emit({ kind: "event", id: "loss.leftzone", key: "loss.leftzone" }, source);
  }

  /** Number runtime-timed repeats without retaining history. */
  holdRepeat(t: number, source: string): InputChange {
    return { seq: ++this.seq, t, change: { kind: "repeat", id: HOLD_REPEAT, key: `${HOLD_REPEAT}⟳` }, raw: { type: "time", t, target: null }, source };
  }



  position(): { x: number; y: number } {
    return { ...this.pos };
  }

  isDown(id: string): boolean {
    return this.down.has(id);
  }

  press(id: string): Press | null {
    return this.down.get(id) ?? null;
  }

  /** Latest press, including after release when its position is still needed. */
  lastPress(id: string): Press | null {
    return this.presses.get(id)?.at(-1) ?? null;
  }

  holdTime(id: string, t: number): number | null {
    const p = this.down.get(id);
    return p ? t - p.t : null;
  }

  /** Count consecutive presses within the reading instance double-click time and area, comparing adjacent presses. */
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

  /** Window velocity excludes time before the latest pointer press, including on release; use the reading instance parameters. */
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
