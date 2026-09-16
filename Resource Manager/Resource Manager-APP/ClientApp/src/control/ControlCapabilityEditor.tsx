import { createMemo, For, Show } from "solid-js";
import { uiText } from "../text.ts";
import { defaultCurve, normalizeCurve, percentAt } from "./fanCurve.ts";
import type { ControlCapability, ControlSetting } from "./controlTypes.ts";

/**
 * 一项能力的编辑器。
 *
 * **控不了的项也能编。** 这不是将就：期望状态和写入器是分开的 ——
 * 用户现在就可以把曲线编好，等 NBFC 装上、写入器接进来，
 * 后台重新施加那一轮就会把它写下去。所以编辑不需要等硬件就位。
 */
export function ControlCapabilityEditor(props: {
  capability: ControlCapability;
  setting: ControlSetting | undefined;
  onChange: (setting: ControlSetting | null) => void;
}) {
  return (
    <Show when={props.capability.valueKind === "curve"} fallback={
      <Show when={props.capability.valueKind === "toggle"} fallback={
        <NumberEditor
          capability={props.capability}
          setting={props.setting}
          onChange={props.onChange}
        />
      }>
        <ToggleEditor setting={props.setting} capability={props.capability}
          onChange={props.onChange} />
      </Show>
    }>
      <CurveEditor
        capability={props.capability}
        setting={props.setting}
        onChange={props.onChange}
      />
    </Show>
  );
}

function NumberEditor(props: {
  capability: ControlCapability;
  setting: ControlSetting | undefined;
  onChange: (setting: ControlSetting | null) => void;
}) {
  const range = () => props.capability.range;
  // 没设过就显示默认值，但**不写进期望状态** —— 显示什么和设了什么是两回事。
  const shown = () => props.setting?.number ?? range()?.defaultValue ?? 0;

  return (
    <div class="control-editor">
      <Show when={range()}>
        {(bounds) => (
          <input
            type="range"
            min={bounds().minimum}
            max={bounds().maximum}
            step={bounds().step}
            value={shown()}
            onInput={(event) => props.onChange({
              capabilityId: props.capability.id,
              number: Number(event.currentTarget.value)
            })}
          />
        )}
      </Show>
      <span class="control-editor-value">
        {shown()}{range()?.unit ? ` ${range()!.unit}` : ""}
      </span>
      <Show when={props.setting}>
        <button
          class="secondary"
          type="button"
          onClick={() => props.onChange(null)}
        >
          {uiText.control.clear}
        </button>
      </Show>
    </div>
  );
}

function ToggleEditor(props: {
  capability: ControlCapability;
  setting: ControlSetting | undefined;
  onChange: (setting: ControlSetting | null) => void;
}) {
  return (
    <div class="control-editor">
      <label class="control-editor-toggle">
        <input
          type="checkbox"
          checked={props.setting?.toggle === true}
          onChange={(event) => props.onChange(
            event.currentTarget.checked
              ? { capabilityId: props.capability.id, toggle: true }
              // 关掉就是不设它，而不是"设成关" —— 后者会一直压着硬件。
              : null)}
        />
        <span>{uiText.control.on}</span>
      </label>
    </div>
  );
}

/** 曲线编辑：一行一个点，外加一条预览。 */
function CurveEditor(props: {
  capability: ControlCapability;
  setting: ControlSetting | undefined;
  onChange: (setting: ControlSetting | null) => void;
}) {
  const points = createMemo(() => normalizeCurve(props.setting?.curve ?? []));

  const write = (next: { temperatureCelsius: number; percent: number }[]) =>
    props.onChange(next.length === 0
      ? null
      : { capabilityId: props.capability.id, curve: normalizeCurve(next) });

  return (
    <div class="control-curve">
      <Show
        when={points().length > 0}
        fallback={
          <button class="secondary" type="button" onClick={() => write(defaultCurve())}>
            {uiText.control.createCurve}
          </button>
        }
      >
        <CurvePreview points={points()} />
        <ul class="control-curve-points">
          <For each={points()}>
            {(point, index) => (
              <li>
                <input
                  type="number"
                  class="control-curve-number"
                  value={point.temperatureCelsius}
                  onChange={(event) => {
                    const next = [...points()];
                    next[index()] = {
                      ...point,
                      temperatureCelsius: Number(event.currentTarget.value)
                    };
                    write(next);
                  }}
                />
                <span class="control-curve-unit">°C</span>
                <input
                  type="number"
                  class="control-curve-number"
                  value={point.percent}
                  onChange={(event) => {
                    const next = [...points()];
                    next[index()] = { ...point, percent: Number(event.currentTarget.value) };
                    write(next);
                  }}
                />
                <span class="control-curve-unit">%</span>
                <button
                  class="secondary"
                  type="button"
                  onClick={() => write(points().filter((_, at) => at !== index()))}
                >
                  ×
                </button>
              </li>
            )}
          </For>
        </ul>
        <button class="secondary" type="button" onClick={() => props.onChange(null)}>
          {uiText.control.clear}
        </button>
      </Show>
    </div>
  );
}

/** 曲线预览。按 5 度一格采样，直接用插值结果，和写下去的是同一套算法。 */
function CurvePreview(props: { points: { temperatureCelsius: number; percent: number }[] }) {
  const path = createMemo(() => {
    const samples: string[] = [];
    for (let temperature = 30; temperature <= 100; temperature += 5) {
      const x = ((temperature - 30) / 70) * 100;
      const y = 100 - percentAt(props.points, temperature);
      samples.push(`${samples.length === 0 ? "M" : "L"}${x.toFixed(1)},${y.toFixed(1)}`);
    }
    return samples.join(" ");
  });

  return (
    <svg class="control-curve-preview" viewBox="0 0 100 100" preserveAspectRatio="none"
      role="img" aria-label={uiText.control.curvePreview}>
      <path d={path()} fill="none" stroke="currentColor" stroke-width="2"
        vector-effect="non-scaling-stroke" />
    </svg>
  );
}
