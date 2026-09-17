import { createMemo, createSignal, For, onCleanup } from "solid-js";
import { uiText } from "../text.ts";
import { curveStops, percentAt } from "./fanCurve.ts";

/**
 * 风扇曲线图：温度对转速，点直接落在曲线上，上下拖。
 *
 * 交互沿用这类工具的通行做法（Afterburner / FanControl）：横轴温度、纵轴转速，
 * 曲线上每 5 度一个控制点。点的温度固定，只能上下动 ——
 * 所以每次调整都落在同一组温度上，可预期、可对比，也不会拖出密度不可控的点。
 * 曲线的圆滑交给平滑算法，不靠手稳。
 *
 * 改动只改本地，什么时候写下去由外面的「应用」决定。
 */

/** 画布坐标系。固定视框等比缩放，指针换算只要一次 getBoundingClientRect。 */
const viewWidth = 100;
const viewHeight = 56;
const padding = { left: 7, right: 3, top: 4, bottom: 8 };

const plotWidth = viewWidth - padding.left - padding.right;
const plotHeight = viewHeight - padding.top - padding.bottom;

const firstStop = curveStops[0];
const lastStop = curveStops[curveStops.length - 1];

function xOf(temperature: number): number {
  const ratio = (temperature - firstStop) / (lastStop - firstStop);
  return padding.left + (ratio * plotWidth);
}

function yOf(percent: number): number {
  return padding.top + ((1 - (percent / 100)) * plotHeight);
}

export function FanCurveChart(props: {
  stops: readonly number[];
  disabled?: boolean;
  onChange: (stops: number[]) => void;
}) {
  let surface: SVGSVGElement | undefined;
  const [dragging, setDragging] = createSignal(-1);

  const path = createMemo(() => {
    const points = curveStops.map((stop, index) => ({
      temperatureCelsius: stop,
      percent: props.stops[index] ?? 0
    }));
    const samples: string[] = [];
    for (let temperature = firstStop; temperature <= lastStop; temperature += 1) {
      samples.push(
        `${samples.length === 0 ? "M" : "L"}`
        + `${xOf(temperature).toFixed(2)},${yOf(percentAt(points, temperature)).toFixed(2)}`);
    }
    return samples.join(" ");
  });

  /** 客户端坐标 → 视框坐标。视框等比铺满，两个方向各自按比例换算。 */
  function toView(clientX: number, clientY: number) {
    const rect = surface!.getBoundingClientRect();
    return {
      x: ((clientX - rect.left) / rect.width) * viewWidth,
      y: ((clientY - rect.top) / rect.height) * viewHeight
    };
  }

  /** 离这个横坐标最近的控制点。点哪儿就调哪个，不用精确按中那个小圆。 */
  function nearestStop(x: number): number {
    let best = 0;
    let bestDistance = Number.POSITIVE_INFINITY;
    curveStops.forEach((temperature, index) => {
      const distance = Math.abs(xOf(temperature) - x);
      if (distance < bestDistance) {
        bestDistance = distance;
        best = index;
      }
    });
    return best;
  }

  function write(index: number, y: number) {
    const ratio = 1 - ((y - padding.top) / plotHeight);
    const percent = Math.round(Math.min(100, Math.max(0, ratio * 100)));
    const next = curveStops.map((_, at) => props.stops[at] ?? 0);
    next[index] = percent;
    props.onChange(next);
  }

  // 拖动期间在 window 上听：指针会跑出图外，只在 svg 上听会中途断掉。
  const onWindowMove = (event: PointerEvent) => {
    const index = dragging();
    if (index < 0 || !surface) {
      return;
    }
    write(index, toView(event.clientX, event.clientY).y);
  };
  const onWindowUp = () => setDragging(-1);

  window.addEventListener("pointermove", onWindowMove);
  window.addEventListener("pointerup", onWindowUp);
  window.addEventListener("pointercancel", onWindowUp);
  onCleanup(() => {
    window.removeEventListener("pointermove", onWindowMove);
    window.removeEventListener("pointerup", onWindowUp);
    window.removeEventListener("pointercancel", onWindowUp);
  });

  return (
    <svg
      class="fan-curve-chart"
      classList={{ "fan-curve-chart-off": props.disabled === true }}
      viewBox={`0 0 ${viewWidth} ${viewHeight}`}
      ref={(element) => { surface = element; }}
      role="group"
      aria-label={uiText.control.curvePreview}
      onPointerDown={(event) => {
        if (props.disabled) {
          return;
        }
        event.preventDefault();
        const point = toView(event.clientX, event.clientY);
        const index = nearestStop(point.x);
        setDragging(index);
        write(index, point.y);
      }}
    >
      {/* 横向刻度：0 / 50 / 100 %，给拖动一个参照。 */}
      <For each={[0, 50, 100]}>
        {(percent) => (
          <>
            <line class="fan-curve-grid" x1={padding.left} x2={viewWidth - padding.right}
              y1={yOf(percent)} y2={yOf(percent)} />
            <text class="fan-curve-axis" x={padding.left - 1.5} y={yOf(percent) + 1.2}
              text-anchor="end">{percent}</text>
          </>
        )}
      </For>

      {/* 温度只标首尾和中间，十个全标会糊成一片。 */}
      <For each={[firstStop, curveStops[Math.floor(curveStops.length / 2)], lastStop]}>
        {(temperature) => (
          <text class="fan-curve-axis" x={xOf(temperature)} y={viewHeight - 1.5}
            text-anchor="middle">{temperature}°</text>
        )}
      </For>

      <path class="fan-curve-line" d={path()} />

      <For each={curveStops}>
        {(temperature, index) => {
          const percent = () => props.stops[index()] ?? 0;
          return (
            <g
              class="fan-curve-handle"
              classList={{ "fan-curve-handle-active": dragging() === index() }}
              tabindex={0}
              role="slider"
              aria-label={`${temperature} °C`}
              aria-valuemin={0}
              aria-valuemax={100}
              aria-valuenow={percent()}
              onKeyDown={(event) => {
                if (props.disabled) {
                  return;
                }
                // 键盘也能调。按住 Shift 走大步。
                const step = event.shiftKey ? 10 : 1;
                const next = curveStops.map((_, at) => props.stops[at] ?? 0);
                if (event.key === "ArrowUp") {
                  event.preventDefault();
                  next[index()] = Math.min(100, percent() + step);
                  props.onChange(next);
                } else if (event.key === "ArrowDown") {
                  event.preventDefault();
                  next[index()] = Math.max(0, percent() - step);
                  props.onChange(next);
                }
              }}
            >
              <circle class="fan-curve-point" cx={xOf(temperature)} cy={yOf(percent())} r={1.6} />
            </g>
          );
        }}
      </For>
    </svg>
  );
}
