// 事实库：守卫读的真假值。每个事实的算法、边界和参数来源只在这里写一次。

import type { FactContext, FactId } from "./definition.ts";
import { where } from "./params.ts";

export interface FactResult { value: boolean; explain: string }

const yes = (b: boolean) => (b ? "是" : "否");

export const FACTS: Readonly<Record<FactId, (ctx: FactContext) => FactResult>> = {
  "离开拖动框": (ctx) => {
    const press = ctx.input.lastPress("pointer.left");
    const w = ctx.params.dragWidth;
    const h = ctx.params.dragHeight;
    if (!press) return { value: false, explain: "离开拖动框=否（没有按下点）" };
    const pos = ctx.input.position();
    const dx = Math.abs(pos.x - press.x);
    const dy = Math.abs(pos.y - press.y);
    const value = dx > w.value / 2 || dy > h.value / 2;
    return {
      value,
      explain: `离开拖动框=${yes(value)}：距按下点 (${dx.toFixed(1)}, ${dy.toFixed(1)}) px，拖动框 ${w.value}×${h.value} px（${where(w)}），半宽高 (${w.value / 2}, ${h.value / 2})`,
    };
  },
  "手过了最大值": (ctx) => {
    const h = ctx.handUnbounded;
    const max = ctx.def.channel.max;
    if (h === null || typeof h !== "number" || typeof max !== "number") return { value: false, explain: `手过了最大值=否（${h === null ? "手不在带" : "只看一条轴的通道"}）` };
    const value = h > max;
    return { value, explain: `手过了最大值=${yes(value)}：手经手的映射在 ${h.toFixed(1)}，通道最大值 ${max}` };
  },
  "指针在组件内": (ctx) => {
    const pos = ctx.input.position();
    const r = ctx.rect;
    const value = r !== null && pos.x >= r.left && pos.x <= r.right && pos.y >= r.top && pos.y <= r.bottom;
    return {
      value,
      explain: `指针在组件内=${yes(value)}：指针 (${pos.x.toFixed(0)}, ${pos.y.toFixed(0)})，组件 ${r ? `x ${r.left.toFixed(0)}–${r.right.toFixed(0)}，y ${r.top.toFixed(0)}–${r.bottom.toFixed(0)}` : "无几何"}`,
    };
  },
  "连按≥2": (ctx) => {
    const c = ctx.input.clickCount("pointer.left", ctx.params);
    const value = c !== null && c.count >= 2;
    return { value, explain: `连按≥2=${yes(value)}：${c ? c.explain : "没有按过"}` };
  },
  "Shift 按住": (ctx) => {
    const value = ctx.input.isDown("key.ShiftLeft") || ctx.input.isDown("key.ShiftRight");
    return { value, explain: `Shift 按住=${yes(value)}` };
  },
  "在组件上按住": (ctx) => {
    const held = ctx.input.isDown("pointer.left");
    const value = held && ctx.pressedOn;
    return {
      value,
      explain: `在组件上按住=${yes(value)}：左键${held ? "按住" : "没按住"}，最近一次按下${ctx.pressedOn ? "在" : "不在"}这个组件上`,
    };
  },
  "沿运动方向": (ctx) => {
    const press = ctx.input.lastPress("pointer.left");
    const g = ctx.geometry;
    const lim = ctx.params.directionAngle;
    if (!press || !g) return { value: false, explain: `沿运动方向=否（${press ? "没有几何" : "没有按下点"}）` };
    // 平面通道的运动方向是整个平面，夹角总是 0（协议第 4.2 节）
    if (typeof ctx.def.channel.min !== "number") return { value: true, explain: "沿运动方向=是：平面通道，运动方向是整个平面" };
    const pos = ctx.input.position();
    const dx = pos.x - press.x;
    const dy = pos.y - press.y;
    const ax = g.atMax.x - g.atMin.x;
    const ay = g.atMax.y - g.atMin.y;
    const len = Math.hypot(dx, dy) * Math.hypot(ax, ay);
    if (len === 0) return { value: false, explain: "沿运动方向=否：指针还在按下点" };
    // 朝前朝后都算：取夹角的余弦的绝对值
    const angle = (Math.acos(Math.min(1, Math.abs(dx * ax + dy * ay) / len)) * 180) / Math.PI;
    const value = angle <= lim.value;
    return {
      value,
      explain: `沿运动方向=${yes(value)}：距按下点 (${dx.toFixed(1)}, ${dy.toFixed(1)}) px，和运动方向夹角 ${angle.toFixed(1)}°，方向角 ${lim.value}°（${where(lim)}）`,
    };
  },
  "可拖": (ctx) => {
    const g = ctx.geometry;
    return {
      value: g !== null,
      explain: g
        ? `可拖=是：绑定为${ctx.bindingLabel}，逻辑中心是${g.center}，能从位置反推值`
        : `可拖=否：绑定为${ctx.bindingLabel}，对应不到屏幕位置，没有逻辑中心`,
    };
  },
};
