import { createMemo, createSignal, For } from "solid-js";
import { uiText } from "../../text.ts";
import { curveStops, percentAt } from "./fanCurve.ts";
import { curvePointDefinition } from "../../interactions/components/curvePoint.ts";
import { solidHost, useInteraction, useInteractionComposite } from "../../interactions/solid/lodestone.ts";
import type { InteractionHandle } from "../../interactions/solid/lodestone.ts";
import type { Instance, Presentation } from "../../interactions/protocol/runtime.ts";

/**
 * 风扇曲线图：温度对转速，点直接落在曲线上，上下拖。
 *
 * 交互沿用这类工具的通行做法（Afterburner / FanControl）：横轴温度、纵轴转速。
 * 点的温度由外面给定（每 5 度一个），这里只能上下动，不能横向移动。
 * 相邻点直线连接，与执行端一致。
 *
 * 拖动由交互协议的运行时处理（协议第 4.4 节第 2 处迁移）：曲线图是组合，每个温度一个曲线点。
 * 按在点上是抓住，不跳；按在图里的空白处交给横向最近的点，点跳到指针处接着拖（「点哪儿就调哪个，不用精确按中那个小圆」）。
 * Esc 或右键取消，回到这次拖动前的值；窗口失焦后丢失的松开由输入层校正。
 * 改动只改本地，什么时候写下去由外面的「应用」决定。
 *
 * 画面：点和曲线都按运行时画出的位置画（2026-10-26 用户裁定「线是前端，按照前端缓存值也就是动画的位置画」，
 * docs/ui_interaction_kit.md 第 4 节第 18 项）：Esc、右键取消和方向键时看得到点走回去、走过去，曲线跟着点走。
 * 提交、无障碍的值和名称仍按宿主值（外面给的 stops）。运行时还没画过的点按宿主值画。
 */

/** Fixed plot geometry; screen positions use the actual SVG transform. */
const viewWidth = 100;
const viewHeight = 56;
const padding = { left: 10, right: 5, top: 5, bottom: 8 };

const plotWidth = viewWidth - padding.left - padding.right;
const plotHeight = viewHeight - padding.top - padding.bottom;

const ticks = Array.from({ length: 11 }, (_, index) => index * 10);

function yOf(percent: number): number {
  return padding.top + ((1 - (percent / 100)) * plotHeight);
}

export function FanCurveChart(props: {
  stops: readonly number[];
  temperatures?: readonly number[];
  disabled?: boolean;
  onChange: (stops: number[]) => void;
}) {
  let surface!: SVGSVGElement;
  const temperatures = () => props.temperatures ?? curveStops;
  const temperatureRange = createMemo(() => temperatures().reduce(
    (range, temperature) => ({
      minimum: Math.min(range.minimum, temperature),
      maximum: Math.max(range.maximum, temperature)
    }), { minimum: 0, maximum: 100 }));
  const temperatureTicks = createMemo(() => {
    const range = temperatureRange();
    return ticks.map(percent => Number((range.minimum
      + (percent / 100) * (range.maximum - range.minimum)).toFixed(1)));
  });

  function xOf(temperature: number): number {
    const range = temperatureRange();
    const ratio = (temperature - range.minimum) / (range.maximum - range.minimum);
    return padding.left + (ratio * plotWidth);
  }

  /** 运行时最近一次画出的转速（按温度）：点和曲线都按它画（path 是立即求值的 memo，要先声明）。 */
  const [drawn, setDrawn] = createSignal<ReadonlyMap<number, number>>(new Map());
  const drawnPercent = (temperature: number, index: number) => drawn().get(temperature) ?? props.stops[index] ?? 0;
  const path = createMemo(() => {
    const points = temperatures().map((stop, index) => ({
      temperatureCelsius: stop,
      percent: drawnPercent(stop, index)
    }));
    const range = temperatureRange();
    // The curve is piecewise linear. Draw its actual points and the endpoint
    // plateaus instead of sampling only the former fixed temperature range.
    return [
      { temperatureCelsius: range.minimum, percent: percentAt(points, range.minimum) },
      ...points,
      { temperatureCelsius: range.maximum, percent: percentAt(points, range.maximum) }
    ].map((point, index) => `${index === 0 ? "M" : "L"}`
      + `${xOf(point.temperatureCelsius).toFixed(2)},${yOf(point.percent).toFixed(2)}`).join(" ");
  });

  /** Includes borders, scaling, and any SVG letterboxing. */
  function toScreen(x: number, y: number) {
    return new DOMPoint(x, y).matrixTransform(surface.getScreenCTM()!);
  }
  function toView(clientX: number, clientY: number) {
    return new DOMPoint(clientX, clientY).matrixTransform(surface.getScreenCTM()!.inverse());
  }

  /** 每个温度一个曲线点，按温度找。 */
  const points = new Map<number, InteractionHandle<number>>();

  // 区域路由：落在绘图区里、不在任何点上的按下交给横向最近的点；绘图区外不交。
  useInteractionComposite("风扇曲线图", () => surface,
    () => [...points.values()].flatMap((point) => point.instance ? [point.instance as Instance] : []),
    (position) => {
      const point = toView(position.x, position.y);
      if (point.x < padding.left || point.x > viewWidth - padding.right
        || point.y < padding.top || point.y > viewHeight - padding.bottom) return null;
      let best: { temperature: number; child: Instance } | null = null;
      let bestDistance = Number.POSITIVE_INFINITY;
      for (const temperature of temperatures()) {
        const child = points.get(temperature)?.instance;
        const distance = Math.abs(xOf(temperature) - point.x);
        if (child && distance < bestDistance) {
          bestDistance = distance;
          best = { temperature, child: child as Instance };
        }
      }
      return best ? { child: best.child, why: `横向最近的是 ${best.temperature} °C 的点` } : null;
    });

  return (
    <svg
      class="fan-curve-chart"
      classList={{ "fan-curve-chart-off": props.disabled === true }}
      viewBox={`0 0 ${viewWidth} ${viewHeight}`}
      ref={surface}
      role="group"
      aria-label={uiText.control.curvePreview}
    >
      <text class="fan-curve-axis" x={padding.left - 2} y={3} text-anchor="end">%</text>
      <text class="fan-curve-axis" x={viewWidth - padding.right} y={viewHeight - 0.5} text-anchor="end">°C</text>
      <For each={ticks}>
        {(percent) => (
          <>
            <line class="fan-curve-grid" x1={padding.left} x2={viewWidth - padding.right}
              y1={yOf(percent)} y2={yOf(percent)} />
            <text class="fan-curve-axis" x={padding.left - 1.5} y={yOf(percent) + 1.2}
              text-anchor="end">{percent}</text>
          </>
        )}
      </For>

      <For each={temperatureTicks()}>
        {(temperature) => (
          <>
            <line class="fan-curve-grid" x1={xOf(temperature)} x2={xOf(temperature)}
              y1={padding.top} y2={viewHeight - padding.bottom} />
            <text class="fan-curve-axis" x={xOf(temperature)} y={viewHeight - 3.5}
              text-anchor="middle">{temperature}</text>
          </>
        )}
      </For>

      <path class="fan-curve-line" d={path()} />

      <For each={temperatures()}>
        {(temperature, index) => {
          const percent = () => props.stops[index()] ?? 0;
          let handle!: SVGGElement;
          const view = (): Presentation => ({
            element: handle,
            bindingLabel: "位移",
            // 逻辑中心是点的圆心：转速 0、100 时圆心在屏幕上的位置。
            geometry: () => {
              const atMin = toScreen(xOf(temperature), yOf(0));
              const atMax = toScreen(xOf(temperature), yOf(100));
              return { center: "点的圆心", atMin: { x: atMin.x, y: atMin.y }, atMax: { x: atMax.x, y: atMax.y } };
            },
            // 点和曲线按运行时画出的位置画；按住的点另外标出来。
            render: ({ x, mode }) => {
              setDrawn((current) => new Map(current).set(temperature, x as number));
              handle.classList.toggle("fan-curve-handle-active", mode !== "静止");
            },
            aria: (value) => {
              handle.setAttribute("aria-valuenow", String(value));
              handle.setAttribute("aria-valuetext", uiText.control.curvePointValue(value));
              return `aria-valuenow=${value}`;
            }
          });
          points.set(temperature, useInteraction(`曲线点「${temperature} °C」`, curvePointDefinition, view, solidHost<number>({
            value: percent,
            enabled: () => props.disabled !== true,
            call: (port, args) => {
              if (port !== "select") return;
              const next = temperatures().map((_, at) => props.stops[at] ?? 0);
              next[index()] = Math.round(Math.min(100, Math.max(0, Number(args[0]))));
              props.onChange(next);
            }
          })));
          return (
            <g
              ref={handle}
              class="fan-curve-handle"
              tabindex={props.disabled ? -1 : 0}
              role="slider"
              aria-disabled={props.disabled === true}
              aria-orientation="vertical"
              // 名称同时说温度和转速，读屏在点之间移动时不必再等数值。
              aria-label={uiText.control.curvePointName(temperature, percent())}
              aria-valuemin={0}
              aria-valuemax={100}
            >
              <title>{uiText.control.curvePointName(temperature, percent())}</title>
              <circle class="fan-curve-hit" cx={xOf(temperature)} cy={yOf(drawnPercent(temperature, index()))} r={2} />
              <circle class="fan-curve-point" cx={xOf(temperature)} cy={yOf(drawnPercent(temperature, index()))} r={1} />
            </g>
          );
        }}
      </For>
    </svg>
  );
}
