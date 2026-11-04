import { StableLabel } from "../../ui/primitives/StableLabel.tsx";
import { HelpHint } from "../../ui/primitives/HelpHint.tsx";
import { createEffect, createMemo, createSignal, Show, untrack } from "solid-js";
import { onPageLeave } from "../../app/navigation/pageHost.tsx";
import { uiText } from "../../text.ts";
import { controlCapabilityLabel, controlDisplayText } from "./controlPresentation.ts";
import {
  curveFromStops,
  curveTemperatures,
  stopsFromCurve
} from "./fanCurve.ts";
import { FanCurveChart } from "./FanCurveChart";
import { GpuVfCurveEditor } from "./GpuVfCurveEditor.tsx";
import { ControlNumberSlider } from "./ControlNumberSlider";
import { SegmentedControl } from "../../ui/primitives/SegmentedControl";
import { curveSettingFromRead } from "./controlInitialSetting.ts";
import { getControlObjectCurve } from "./controlApi.ts";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import type { ControlCapability, ControlSetting } from "./controlTypes.ts";
/* Re-export the shared numeric conversions for editor consumers. */
import {
  decimalsForStep,
  actualRepeatsNumberEditor,
  formatActualNumber,
  formatControlNumber,
  snapControlNumber,
  snapToStep,
  sliderThumbValue
} from "./controlNumber.ts";

export {
  actualRepeatsNumberEditor,
  decimalsForStep,
  formatActualNumber,
  formatControlNumber,
  snapControlNumber,
  snapToStep,
  sliderThumbValue
};


/** Initialize from an observed value or declared default; otherwise require user input. */
export { initialSettingFor } from "./controlInitialSetting.ts";

/** Edit desired state independently of writer availability. Unselected capabilities return to firmware on apply. */
export function ControlCapabilityEditor(props: {
  capability: ControlCapability;
  /** Object identity used to read the active firmware curve. */
  objectId: string;
  /** The selected management mode controls execution; firmware shape controls fixed-step guidance. */
  firmwareCurveKind: string | null;
  curveExecution: string | null;
  /** Other fans affected by channel-wide software ownership; unconfigured fans may hold their current speed. */
  takenOverWith?: readonly string[];
  setting: ControlSetting | undefined;
  /** Observed value, or null when unavailable, shown on the same scale as the setting. */
  actual?: number | null;
  /** Firmware ownership prevents these settings from taking effect. */
  held?: boolean;
  unavailableReason?: string;
  onChange: (setting: ControlSetting | null) => void;
}) {
  // Disable controls that cannot currently affect the draft.
  // Disabled appearance reflects the current ownership and selection.
  const disabled = () =>
    !props.capability.supported || props.held === true || props.setting === undefined;
  return (
    <div
      class="control-editor-shell"
      classList={{
        "control-editor-off": props.capability.valueKind !== "gpu-vf-curve-reference" && disabled(),
        // Curves need horizontal space; scalar sliders stay compact.
        "control-editor-shell-wide": props.capability.valueKind === "curve" || props.capability.valueKind === "gpu-vf-curve-reference"
      }}
    >
      {/* Toggle states map to absent, true, or false settings without a separate checkbox. */}
      <Show when={props.capability.valueKind === "toggle"}>
        <ToggleEditor
          capability={props.capability}
          setting={props.setting}
          disabled={!props.capability.supported || props.held === true}
          onChange={props.onChange}
        />
      </Show>
      <Show when={props.capability.valueKind === "number"}>
        <NumberEditor
          capability={props.capability}
          setting={props.setting}
          actual={props.actual}
          disabled={disabled()}
          onChange={props.onChange}
        />
      </Show>
      <Show when={props.capability.valueKind === "curve"}>
        <CurveEditor
          capability={props.capability}
          objectId={props.objectId}
          firmwareCurveKind={props.firmwareCurveKind}
          curveExecution={props.curveExecution}
          takenOverWith={props.takenOverWith}
          setting={props.setting}
          disabled={disabled()}
          onChange={props.onChange}
        />
      </Show>
      <Show when={props.capability.valueKind === "gpu-vf-curve-reference"}>
        <GpuVfCurveEditor objectId={props.objectId} capabilityId={props.capability.id}
          setting={props.setting} disabled={!props.capability.supported || props.held === true}
          unavailableReason={props.unavailableReason} onChange={props.onChange} />
      </Show>
    </div>
  );
}

/** Firmware removes the setting and restores ownership; explicit false remains an off command. */
function ToggleEditor(props: {
  capability: ControlCapability;
  setting: ControlSetting | undefined;
  disabled: boolean;
  onChange: (setting: ControlSetting | null) => void;
}) {
  const choice = () => props.setting === undefined
    ? "firmware"
    : props.setting.toggle === false ? "off" : "on";
  return (
    <SegmentedControl
      class="settings-segmented-control control-toggle-choice"
      itemClass="settings-segment"
      ariaLabel={controlCapabilityLabel(props.capability)}
      value={choice()}
      disabled={props.disabled}
      options={[
        {
          id: "firmware",
          label: uiText.control.toggleChoice.firmware,
          description: uiText.control.toggleChoice.firmwareHint
        },
        { id: "on", label: uiText.control.presentation.toggleOn },
        { id: "off", label: uiText.control.presentation.toggleOff }
      ]}
      onChange={(next) => props.onChange(next === "firmware"
        ? null
        : { capabilityId: props.capability.id, toggle: next === "on" })}
    />
  );
}

function NumberEditor(props: {
  capability: ControlCapability;
  setting: ControlSetting | undefined;
  actual?: number | null;
  disabled: boolean;
  onChange: (setting: ControlSetting | null) => void;
}) {
  const range = () => props.capability.range;
  /* Observed or declared initial values are display-only until selected; missing values remain empty. */
  const shown = () =>
    props.setting?.number ?? props.actual ?? range()?.defaultValue ?? null;

  /* Keep partial numeric input as text until commit; null means no active edit. */
  const [draft, setDraft] = createSignal<string | null>(null);
  /* Dragging previews a value until release; cancel restores the setting and null ends preview. */
  const [preview, setPreview] = createSignal<number | null>(null);
  // Discard partial input and drag previews when leaving the page.
  onPageLeave(() => {
    setDraft(null);
    setPreview(null);
  });

  const commit = (text: string) => {
    const bounds = range();
    setDraft(null);
    if (!bounds) {
      return;
    }
    const parsed = Number(text.trim());
    if (text.trim() === "" || !Number.isFinite(parsed)) {
      // Invalid input reverts to the current value without submitting.
      return;
    }
    /* Typed values may exceed the slider range; firmware readback reports any actual clamp. */
    props.onChange({
      capabilityId: props.capability.id,
      number: snapToStep(parsed, bounds),
      unit: bounds.unit
    });
  };

  /** Hide unavailable or out-of-range observations rather than pinning them to a misleading endpoint. */
  const actualPercent = () => {
    const bounds = range();
    const value = props.actual;
    if (!bounds || value === null || value === undefined || !Number.isFinite(value)) {
      return null;
    }
    const span = bounds.maximum - bounds.minimum;
    if (span <= 0 || value < bounds.minimum || value > bounds.maximum) {
      return null;
    }
    return ((value - bounds.minimum) / span) * 100;
  };

  return (
    <div class="control-editor">
      <Show when={range()}>
        {(bounds) => (
          <div class="control-editor-track">
            {/* The slider owns preview, commit, and cancel behavior; its clamped thumb does not change typed values. */}
            <ControlNumberSlider
              bounds={bounds()}
              value={sliderThumbValue(shown() ?? bounds().minimum, bounds())}
              disabled={props.disabled}
              ariaLabel={controlCapabilityLabel(props.capability)}
              onPreview={setPreview}
              onCommit={(number) => props.onChange({
                capabilityId: props.capability.id,
                number,
                // Store the unit with the value so its meaning is explicit.
                unit: bounds().unit
              })}
            />
            {/* The tick shows the observed position; accessible text supplies the equivalent reading. */}
            <Show when={actualPercent()}>
              {(percent) => (
                <span
                  class="control-editor-actual-mark"
                  // Use a unitless value for the track-length calculation.
                  // A percent suffix would invalidate the multiplication.
                  style={{ "--control-actual-percent": String(percent()) }}
                  role="img"
                  aria-label={`${uiText.control.actual}${formatActualNumber(
                    props.actual!)} ${controlDisplayText(bounds().unit)}`}
                />
              )}
            </Show>
          </div>
        )}
      </Show>
      {/* Native numeric input supports precise entry and shares keyboard step size with the slider. */}
      <Show when={range()} fallback={
        <span class="control-editor-value">{shown() === null ? "—" : formatControlNumber(shown()!, undefined)}</span>
      }>
        {(bounds) => (
          <label class="control-editor-value">
            <input
              class="control-editor-value-input"
              type="number"
              inputmode="decimal"
              /* Omit min/max so typed values and native steppers can exceed the displayed slider range. */
              step={bounds().step}
              value={draft() ?? (preview() !== null
                ? formatControlNumber(preview()!, bounds().step)
                : shown() === null ? "" : formatControlNumber(shown()!, bounds().step))}
              disabled={props.disabled}
              aria-label={controlCapabilityLabel(props.capability)}
              onInput={(event) => setDraft(event.currentTarget.value)}
              // Stepper actions and blur both reach change.
              onChange={(event) => commit(event.currentTarget.value)}
              onBlur={(event) => commit(event.currentTarget.value)}
              onKeyDown={(event) => {
                if (event.key === "Enter") {
                  commit(event.currentTarget.value);
                }
              }}
            />
            <Show when={bounds().unit}>
              {(unit) => <span class="control-editor-value-unit">{controlDisplayText(unit())}</span>}
            </Show>
          </label>
        )}
      </Show>
    </div>
  );
}

/** Edit speeds at fixed temperature points, with linear interpolation between neighbors. */
function CurveEditor(props: {
  capability: ControlCapability;
  /** Object identity used to read its active firmware curve. */
  objectId: string;
  /** Use catalog firmware shape and the selected execution mode directly. */
  firmwareCurveKind: string | null;
  curveExecution: string | null;
  /** Other fans affected by software ownership, if any. */
  takenOverWith?: readonly string[];
  setting: ControlSetting | undefined;
  disabled: boolean;
  onChange: (setting: ControlSetting | null) => void;
}) {
  const runtime = useFrontendRuntime();
  const [reading, setReading] = createSignal(false);
  const [readFailed, setReadFailed] = createSignal(false);
  // Invalidate late curve reads when leaving the page so they cannot restore discarded drafts.
  let visit = 0;
  onPageLeave(() => {
    visit++;
    setReadFailed(false);
  });

  const configured = () => (props.setting?.curve?.length ?? 0) > 0;

  const execution = () => props.curveExecution!;

  /* Fixed-step firmware temperatures are display coordinates, not editable firmware breakpoints. Software execution uses real temperature coordinates. */
  const fixedSteps = () =>
    props.firmwareCurveKind === "table" && execution() === "firmware";

  /* Temperature coordinates stay fixed; firmware execution limits the point count. */
  const temperaturesFor = (mode: string | null) => curveTemperatures(
    props.firmwareCurveKind === "table" && mode === "firmware",
    mode === "firmware" ? props.capability.curvePointLimit : null);
  const temperatures = createMemo(() => temperaturesFor(execution()));
  const stops = createMemo(() => stopsFromCurve(props.setting?.curve, temperatures()));

  const write = (next: number[]) =>
    props.onChange({
      capabilityId: props.capability.id,
      curve: curveFromStops(next, temperatures()),
      curveExecution: execution()
    });

  /* Initialize from the active curve on explicit request. Slow serialized EC reads must not block catalog loading; failed reads stay unavailable. */
  const start = async () => {
    if (reading()) {
      return;
    }
    setReading(true);
    setReadFailed(false);
    const requestedSetting = props.setting;
    const requestedVisit = visit;
    const stillSelected = () => requestedVisit === visit && props.setting === requestedSetting && !props.disabled;
    try {
      const firmware = await getControlObjectCurve(runtime.requestClient, props.objectId);
      if (stillSelected()) {
        const setting = curveSettingFromRead(props.capability.id, firmware, temperatures(), execution());
        if (setting) props.onChange(setting);
        else setReadFailed(true);
      }
    } catch {
      if (stillSelected()) setReadFailed(true);
    } finally {
      setReading(false);
    }
  };

  /* Read a missing curve when the capability is explicitly selected, avoiding eager firmware reads on mount. */
  createEffect(() => {
    if (!props.disabled && props.setting !== undefined && props.setting.curve == null) {
      void untrack(start);
    }
  });

  return (
    <div class="control-curve">
      <Show when={readFailed()}><p class="control-curve-note" role="alert">{uiText.control.curveUnavailable}</p></Show>
      {/* Explain that fixed firmware temperature coordinates do not change its breakpoints. */}
      <Show when={fixedSteps()}>
        <HelpHint label={uiText.control.curvePreview}>
          {uiText.control.fixedStepsCurveNote}
        </HelpHint>
      </Show>
      <Show
        when={configured()}
        fallback={
          <button class="secondary" type="button"
            disabled={props.disabled || reading()}
            onClick={start}>
            <StableLabel labels={{ idle: uiText.control.createCurve, busy: uiText.control.readingFirmwareCurve }} current={reading() ? "busy" : "idle"} />
          </button>
        }
      >
        <FanCurveChart temperatures={temperatures()} stops={stops()} disabled={props.disabled} onChange={write} />
      </Show>
    </div>
  );
}
