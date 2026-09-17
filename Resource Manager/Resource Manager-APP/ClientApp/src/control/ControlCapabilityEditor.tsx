import { createMemo, Show } from "solid-js";
import { uiText } from "../text.ts";
import {
  curveFromStops,
  defaultStops,
  stopsFromCurve
} from "./fanCurve.ts";
import { FanCurveChart } from "./FanCurveChart";
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
  /** 这个对象现在归固件管，设了也不生效。 */
  held?: boolean;
  onChange: (setting: ControlSetting | null) => void;
}) {
  // 控不了、或者这块硬件现在归固件管，就置灰。
  // 摆一个能拖但拖了没用的控件是骗人。
  const disabled = () => !props.capability.supported || props.held === true;
  return (
    <div class="control-editor-shell" classList={{ "control-editor-off": disabled() }}>
      <Show when={props.capability.valueKind === "curve"} fallback={
        <Show when={props.capability.valueKind === "toggle"} fallback={
          <NumberEditor
            capability={props.capability}
            setting={props.setting}
            disabled={disabled()}
            onChange={props.onChange}
          />
        }>
          <ToggleEditor setting={props.setting} capability={props.capability}
            disabled={disabled()} onChange={props.onChange} />
        </Show>
      }>
        <CurveEditor
          capability={props.capability}
          setting={props.setting}
          disabled={disabled()}
          onChange={props.onChange}
        />
      </Show>
    </div>
  );
}

function NumberEditor(props: {
  capability: ControlCapability;
  setting: ControlSetting | undefined;
  disabled: boolean;
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
            disabled={props.disabled}
            onInput={(event) => props.onChange({
              capabilityId: props.capability.id,
              number: Number(event.currentTarget.value),
              // 单位跟着值走，别让后端去猜这个数字是瓦还是档。
              unit: bounds().unit
            })}
          />
        )}
      </Show>
      <span class="control-editor-value">
        {shown()}{range()?.unit ? ` ${range()!.unit}` : ""}
      </span>
    </div>
  );
}

function ToggleEditor(props: {
  capability: ControlCapability;
  setting: ControlSetting | undefined;
  disabled: boolean;
  onChange: (setting: ControlSetting | null) => void;
}) {
  return (
    <div class="control-editor">
      <label class="control-editor-toggle">
        <input
          type="checkbox"
          disabled={props.disabled}
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

/**
 * 曲线编辑：曲线上的点，上下拖。
 *
 * 交互沿用这类工具的通行做法（Afterburner / FanControl）：横轴温度、纵轴转速，
 * 点就落在曲线上。点的温度固定在每 5 度，只能上下动 —— 所以每次调整都落在
 * 同一组温度上，可预期、可对比，也不会拖出密度不可控的点。圆滑交给平滑算法。
 */
function CurveEditor(props: {
  capability: ControlCapability;
  setting: ControlSetting | undefined;
  disabled: boolean;
  onChange: (setting: ControlSetting | null) => void;
}) {
  const stops = createMemo(() => stopsFromCurve(props.setting?.curve));
  const configured = () => props.setting?.curve != null;

  const write = (next: number[]) =>
    props.onChange({ capabilityId: props.capability.id, curve: curveFromStops(next) });

  return (
    <div class="control-curve">
      <Show
        when={configured()}
        fallback={
          <button class="secondary" type="button" disabled={props.disabled}
            onClick={() => write(defaultStops())}>
            {uiText.control.createCurve}
          </button>
        }
      >
        <FanCurveChart stops={stops()} disabled={props.disabled} onChange={write} />
      </Show>
    </div>
  );
}
