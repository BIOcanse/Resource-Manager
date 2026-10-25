// 机器检查（协议第 4.3 节）：完备、确定、活性、手的模式、取值、停住点、落点和跳到指针处的用法、几何、输出映射点和推出的吸附范围、
// 自己声明的参数；组件都加好后再查嵌套的拖动框和不同组件声明的同名参数（checkInstances）。
// 守卫只读真假事实，所以对每个格子穷举它涉及的事实的全部组合，不靠抽样。
// 每个模式读哪些输入由它的行决定（definition.ts 的 readsOf），独占模式另外读全部指针类变化和输入丢失事件。

import { BUILTIN_VALUES, declarationsOf, LANDING, primitiveOf, readsOf, REST, STOP_POINT } from "./definition.ts";
import { PARAM_SPECS } from "./params.ts";
import type { ParamSpec } from "./params.ts";
import type { Definition, FactId, Geometry, Mode, Row } from "./definition.ts";
import { HOLD_REPEAT, LOSS_LABELS, POINTER_BUTTONS, POINTER_LEAVE } from "./input.ts";
import type { ParamTable } from "./params.ts";
import { frameOf, isPlane, restRanges } from "./physics.ts";
import { len, sub, vec } from "./vec.ts";
import type { Value } from "./vec.ts";

export interface CheckResult {
  ok: boolean;
  lines: string[];
  counts: { determined: number; missing: number; conflict: number; passed: number };
}

/** 一个模式可能遇到的全部输入变化：指针五键、位置、滚轮、行里提到的键、两种请求、输入丢失事件、指针离开、按住重复。 */
function universe(def: Definition<Value>): { key: string; id: string; pointerish: boolean; event: boolean }[] {
  const out: { key: string; id: string; pointerish: boolean; event: boolean }[] = [];
  for (const b of POINTER_BUTTONS) for (const e of ["↑", "↓"]) out.push({ key: `${b.id}${e}`, id: b.id, pointerish: true, event: false });
  out.push({ key: "pointer.position~", id: "pointer.position", pointerish: true, event: false });
  out.push({ key: "wheel~", id: "wheel", pointerish: false, event: false });
  const keys = new Set<string>();
  for (const r of def.rows) for (const c of r.on) if (c.startsWith("key.")) keys.add(primitiveOf(c));
  for (const k of keys) for (const e of ["↑", "↓", "⟳"]) out.push({ key: `${k}${e}`, id: k, pointerish: false, event: false });
  out.push({ key: "request.activate", id: "request.activate", pointerish: false, event: false });
  out.push({ key: "request.contextmenu", id: "request.contextmenu", pointerish: false, event: false });
  for (const id of Object.keys(LOSS_LABELS)) out.push({ key: id, id, pointerish: false, event: true });
  // 指针离开：独占指针时指针移出组件也不断，不会离开，所以只有写了它的模式读它
  out.push({ key: POINTER_LEAVE, id: POINTER_LEAVE, pointerish: false, event: false });
  out.push({ key: `${HOLD_REPEAT}⟳`, id: HOLD_REPEAT, pointerish: false, event: false });
  return out;
}

function claims(def: Definition<Value>, mode: Mode, u: { key: string; id: string; pointerish: boolean; event: boolean }): boolean {
  if (readsOf(def, mode).has(u.id)) return true;
  return def.exclusive.includes(mode) && (u.pointerish || u.event);
}

function impossible(def: Definition<Value>, mode: Mode, key: string): boolean {
  const held = def.held?.[mode] ?? [];
  // 按住重复只推给独占指针、左键按住、读它的模式（运行时只在这时推出），别的模式收不到
  if (key === `${HOLD_REPEAT}⟳`) return !(def.exclusive.includes(mode) && held.includes("pointer.left") && readsOf(def, mode).has(HOLD_REPEAT));
  return held.some((id) => key === `${id}↑`);
}

const passes = (r: Row) => (r.out ?? []).some((o) => o.tag === "框架" && o.act === "交给外层");

const specText = (p: ParamSpec) => `${p.source.kind === "declared" ? p.source.by : "协议"}：${p.label} ${"value" in p.source ? p.source.value : ""}${p.unit ? ` ${p.unit}` : ""}`;
const sameSpec = (a: ParamSpec, b: ParamSpec) =>
  a === b || (a.label === b.label && a.unit === b.unit && a.readers === b.readers && JSON.stringify(a.source) === JSON.stringify(b.source));

/** 同一个名字只能有一种声明（协议第 4.3 节第 10 项）：和协议的参数同名，或者两处声明不同，都报出。 */
function declarationConflicts(specs: readonly ParamSpec[]): string[] {
  const byId = new Map<string, ParamSpec[]>();
  for (const p of specs) byId.set(p.id, [...(byId.get(p.id) ?? []), p]);
  const out: string[] = [];
  for (const [id, list] of byId) {
    const base = PARAM_SPECS.find((p) => p.id === id);
    const kinds = list.filter((p, i) => list.findIndex((q) => sameSpec(p, q)) === i);
    if (base) out.push(`${id}：和协议的参数“${base.label}”同名（${kinds.map(specText).join("；")}）`);
    else if (kinds.length > 1) out.push(`${id}：${kinds.length} 种声明（${kinds.map(specText).join("；")}）`);
  }
  return out;
}

export function checkDefinition(def: Definition<Value>, geometry: Geometry | null, params: ParamTable): CheckResult {
  const lines: string[] = [];
  const counts = { determined: 0, missing: 0, conflict: 0, passed: 0 };
  for (const mode of def.modes) {
    const missing: string[] = [];
    const conflict: string[] = [];
    let passed = 0;
    for (const u of universe(def)) {
      if (impossible(def, mode, u.key)) continue;
      if (!claims(def, mode, u)) { passed++; continue; }
      const rows = def.rows.filter((r) => (r.from.includes(mode) || r.from.includes("*")) && r.on.includes(u.key));
      const facts = [...new Set(rows.flatMap((r) => Object.keys(r.when ?? {}) as FactId[]))];
      let bad = false;
      for (let mask = 0; mask < 1 << facts.length; mask++) {
        const assign = new Map(facts.map((f, i) => [f, (mask & (1 << i)) !== 0]));
        const hit = rows.filter((r) => Object.entries(r.when ?? {}).every(([k, v]) => assign.get(k as FactId) === v));
        const at = facts.length ? `（${facts.map((f) => `${f}=${assign.get(f) ? "是" : "否"}`).join("，")}）` : "";
        if (hit.length === 0) { missing.push(u.key + at); bad = true; }
        if (hit.length > 1) { conflict.push(u.key + at); bad = true; }
      }
      if (bad) continue;
      // 命中的都是“交给外层”的行：这一格交给外层
      if (rows.length > 0 && rows.every(passes)) passed++;
      else counts.determined++;
    }
    counts.missing += missing.length;
    counts.conflict += conflict.length;
    counts.passed += passed;
    if (missing.length) lines.push(`缺失 @${mode}：${missing.join("、")}`);
    if (conflict.length) lines.push(`冲突 @${mode}：${conflict.join("、")}`);
  }

  // 活性：每个模式都能经由某一行回到静止。丢失的松开会在下一次观测时校正出来，所以松开总能观测到。
  const reach = new Set<Mode>([REST]);
  for (let grew = true; grew;) {
    grew = false;
    for (const r of def.rows) {
      const to = r.to;
      for (const from of r.from) {
        if (from === "*" || reach.has(from)) continue;
        if ((to === "=" ? from : to) !== from && reach.has(to === "=" ? from : to)) { reach.add(from); grew = true; }
      }
    }
  }
  const stuck = def.modes.filter((m) => !reach.has(m));
  if (stuck.length) lines.push(`活性：${stuck.join("、")} 回不到静止`);

  // 手的模式：手带着时要独占指针，否则指针移出组件就断
  const undeclared = def.channel.hand.filter((m) => !def.modes.includes(m) || !def.exclusive.includes(m));
  if (undeclared.length) lines.push(`手：${undeclared.join("、")} 不是独占指针的模式`);

  // 交给外层：这一行不收变化，所以模式不变、没有别的输出，也不能在独占模式里（独占时内外层都不收）
  const badPass = def.rows.filter((r) => passes(r) && (r.to !== "=" || (r.out ?? []).length !== 1 || r.from.some((m) => m === "*" || def.exclusive.includes(m))))
    .map((r) => `#${def.rows.indexOf(r) + 1}`);
  if (badPass.length) lines.push(`交给外层：${badPass.join("、")} 要模式不变、没有别的输出、不在独占模式里`);

  // 取值：行里用到的取值都要有定义；落点、当前值、起点的值由运行时提供。
  // “落点”只能是手放开的行（从手模式回到非手模式）的停住点；“停住点”只能写在停住点的调用里
  const hand = (m: Mode) => def.channel.hand.includes(m);
  const defined = (v: string) => (BUILTIN_VALUES.includes(v) && v !== LANDING) || v in def.values;
  const badValues: string[] = [];
  for (const r of def.rows) {
    const letGo = r.to !== "=" && !hand(r.to) && r.from.every((m) => m !== "*" && hand(m));
    for (const o of r.out ?? []) {
      const args = o.tag === "业务" ? o.args : o.tag === "停住点" ? [o.value] : [];
      for (const a of args) {
        if (!("value" in a)) continue;
        const ok = a.value === LANDING ? o.tag === "停住点" && letGo : defined(a.value);
        if (!ok) badValues.push(`#${def.rows.indexOf(r) + 1} ${a.value}`);
      }
    }
  }
  const stopCall = def.channel.stopCall;
  for (const a of stopCall?.args ?? []) if ("value" in a && a.value !== STOP_POINT && !defined(a.value)) badValues.push(`停住点的调用 ${a.value}`);
  if (badValues.length) lines.push(`取值：${badValues.join("、")} 没有定义（“${LANDING}”只能是手放开的行的停住点，“${STOP_POINT}”只能写在停住点的调用里）`);

  // 停住点：一行最多一个；物体停在那里，所以行要去不由手带的模式；通道要写停住点的调用，调用里要带停住点
  const badStops: string[] = [];
  for (const r of def.rows) {
    const n = (r.out ?? []).filter((o) => o.tag === "停住点").length;
    if (n === 0) continue;
    const at = `#${def.rows.indexOf(r) + 1}`;
    if (n > 1) badStops.push(`${at} 写了 ${n} 个停住点`);
    const toHand = r.to === "=" ? r.from.some((m) => m === "*" || hand(m)) : hand(r.to);
    if (toHand) badStops.push(`${at} 去由手带的模式`);
  }
  if (def.rows.some((r) => r.out?.some((o) => o.tag === "停住点")) && !stopCall) badStops.push("通道没有写停住点的调用");
  if (stopCall && !stopCall.args.some((a) => "value" in a && a.value === STOP_POINT)) badStops.push(`停住点的调用 ${stopCall.port} 没带“${STOP_POINT}”`);
  if (badStops.length) lines.push(`停住点：${badStops.join("、")}`);

  // 跳到指针处：按下时手所在处在进入独占模式时记下，所以只能写在从非独占模式进入独占模式的行上，而且要有几何
  const badJumps: string[] = [];
  for (const r of def.rows) {
    if (!(r.out ?? []).some((o) => o.tag === "动画" && o.signal === "跳到指针处")) continue;
    const enters = r.to !== "=" && def.exclusive.includes(r.to) && r.from.every((m) => m !== "*" && !def.exclusive.includes(m));
    if (!enters) badJumps.push(`#${def.rows.indexOf(r) + 1} 不是从非独占模式进入独占模式的行`);
    else if (!geometry) badJumps.push(`#${def.rows.indexOf(r) + 1} 呈现层没有几何`);
  }
  if (badJumps.length) lines.push(`跳到指针处：${badJumps.join("、")}`);

  // 几何：值最小、最大时逻辑中心的两点不能重合（平面：横向、纵向都不能重合），否则换算不出每单位多少 px
  const plane = isPlane(def.channel);
  const frame = geometry ? frameOf(def.channel, geometry) : null;
  const badGeometry = frame !== null && !(frame.ppu.x > 0 && (!plane || frame.ppu.y > 0));
  if (badGeometry) lines.push(plane ? "几何：值最小、最大时逻辑中心的两点横向或纵向重合，换算不出位移" : "几何：值最小、最大时逻辑中心的两点重合，换算不出位移");

  // 输出映射点和推出的静止吸附范围（在其中静止松手会停到这个点）：只报告数值
  const targets = def.channel.mappingPoints;
  if (targets && targets.length > 0 && frame && !badGeometry) {
    const px = targets.map((t) => ({ x: vec(t).x * frame.ppu.x, y: vec(t).y * frame.ppu.y }));
    let minGap = Infinity;
    for (let i = 0; i < px.length; i++) for (let j = i + 1; j < px.length; j++) minGap = Math.min(minGap, len(sub(px[i], px[j])));
    // 点在边界上时，朝边界那一侧没有范围可言，不计
    const widths = restRanges(def.channel, frame, params, def.channel.field).flatMap((r) => r.sides.filter((s) => s.open).map((s) => s.px));
    const lo = Math.min(...widths);
    const hi = Math.max(...widths);
    const range = lo.toFixed(2) === hi.toFixed(2) ? `${lo.toFixed(2)} px` : `${lo.toFixed(2)}–${hi.toFixed(2)} px`;
    lines.push(`输出映射点：${targets.length} 个${targets.length > 1 ? `，最小间距 ${minGap.toFixed(1)} px` : ""}；推出的静止吸附范围：`
      + (plane ? `点沿每条轴两侧各 ${range}（到和相邻点的中垂线或边界为止）` : `点两侧各 ${range}（到相邻两点的中点或边界为止）`));
  }

  // 自己的参数：力场、手的映射和取值声明的，同一个名字只能有一种声明
  const declared = declarationsOf(def);
  const badDeclared = declarationConflicts(declared);
  if (badDeclared.length) lines.push(`自己的参数：${badDeclared.join("、")}`);
  else if (declared.length) lines.push(`自己的参数：${new Set(declared.map((p) => p.id)).size} 项（${[...new Set(declared.map((p) => p.label))].join("、")}）`);

  const ok = counts.missing === 0 && counts.conflict === 0 && stuck.length === 0 && undeclared.length === 0 && badPass.length === 0 && badValues.length === 0
    && badStops.length === 0 && badJumps.length === 0 && !badGeometry && badDeclared.length === 0;
  lines.unshift(`${def.name}：确定 ${counts.determined}，缺失 ${counts.missing}，冲突 ${counts.conflict}，交给外层 ${counts.passed}；活性 ${stuck.length ? "✗" : "✓"}`);
  return { ok, lines, counts };
}

/** 组件都加好以后的一个实例：名字、定义和级联后的参数。 */
export interface CheckedInstance {
  name: string;
  def: Definition<Value>;
  params: ParamTable;
}

/**
 * 组件都加好后的检查（协议第 4.3 节第 10、11 项）。这两条看的是实例，不是单份定义能查的：
 * - 嵌套：内层把位置变化交给外层时（有读位置变化、写了“交给外层”的行），外层的拖动框宽、高都不小于内层的，
 *   否则内层还在拖动框里、还没判断方向，外层先离开拖动框接手。nested 是外层元素包含内层元素的每一对（由运行时按包含关系找出）。
 * - 不同组件声明的同名参数只能是同一种声明（全局层的覆盖对它们一起生效）。
 */
export function checkInstances(instances: readonly CheckedInstance[], nested: readonly { outer: CheckedInstance; inner: CheckedInstance }[]): { ok: boolean; lines: string[] } {
  const lines: string[] = [];
  let ok = true;
  // 嵌套：结果相同的几对合成一行（例如一个列表里的 40 行）
  const groups = new Map<string, { inners: string[]; text: (inners: string[]) => string; bad: boolean }>();
  for (const { outer, inner } of nested) {
    if (!inner.def.rows.some((r) => r.on.includes("pointer.position~") && passes(r))) continue;
    const ow = outer.params.dragWidth.value;
    const oh = outer.params.dragHeight.value;
    const iw = inner.params.dragWidth.value;
    const ih = inner.params.dragHeight.value;
    const small = [...(ow < iw ? [`宽 ${ow} px 小于内层的 ${iw} px`] : []), ...(oh < ih ? [`高 ${oh} px 小于内层的 ${ih} px`] : [])];
    const key = `${outer.name}|${inner.def.name}|${ow}|${oh}|${iw}|${ih}`;
    const g = groups.get(key) ?? {
      inners: [],
      bad: small.length > 0,
      text: (inners: string[]) => `嵌套：${outer.name} ⊃ ${inners.length === 1 ? inners[0] : `${inners[0]} 等 ${inners.length} 个`}（${inner.def.name}，把位置变化交给外层）：`
        + (small.length ? `外层拖动框${small.join("、")}，内层还在拖动框里时外层就先接手 ✗` : `外层拖动框 ${ow}×${oh} px 不小于内层的 ${iw}×${ih} px ✓`),
    };
    g.inners.push(inner.name);
    groups.set(key, g);
  }
  for (const g of groups.values()) {
    lines.push(g.text(g.inners));
    if (g.bad) ok = false;
  }
  if (groups.size === 0) lines.push("嵌套：没有把位置变化交给外层的内层组件");
  // 不同组件声明的同名参数
  const defs = [...new Set(instances.map((i) => i.def))];
  const bad = declarationConflicts(defs.flatMap(declarationsOf).filter((p, i, all) => all.indexOf(p) === i));
  if (bad.length) { ok = false; lines.push(`自己的参数：不同组件的声明不同：${bad.join("、")}`); }
  else lines.push(`自己的参数：${defs.length} 种组件的声明没有冲突`);
  return { ok, lines };
}
