import { controlSliderDefinition, thumbHalfWidth } from "../../interactions/components/controlSlider.ts";
import type { Presentation } from "../../interactions/protocol/runtime.ts";
import { setInteractionComponentBox, solidHost, useInteraction } from "../../interactions/solid/lodestone.ts";
import { effectiveStep, snapControlNumber } from "./controlNumber.ts";
import type { ControlNumberBounds } from "./controlNumber.ts";

/** Native range rendering with runtime-owned preview, release-to-commit, and cancel behavior. Keyboard changes commit per step; unchanged values do not commit. */
export function ControlNumberSlider(props: {
  bounds: ControlNumberBounds;
  value: number;
  disabled: boolean;
  ariaLabel: string;
  /** Snapped drag preview, or null after release or cancellation. */
  onPreview: (value: number | null) => void;
  onCommit: (value: number) => void;
}) {
  let input!: HTMLInputElement;
  const span = () => props.bounds.maximum - props.bounds.minimum;
  const valueAt = (ratio: number) => snapControlNumber(props.bounds.minimum + ratio * span(), props.bounds);
  const commit = (next: number) => {
    props.onPreview(null);
    if (next !== props.value) props.onCommit(next);
  };

  const view = (): Presentation => {
    // Place the thumb at the host value before the runtime's first render.
    input.value = String(props.value);
    // The thumb bounds distinguish grabbing the thumb from pressing the track.
    // Derive geometry from the native input's snapped value.
    setInteractionComponentBox(input, () => {
      const rect = input.getBoundingClientRect();
      const ratio = span() > 0 ? (Number(input.value) - props.bounds.minimum) / span() : 0;
      const x = rect.left + thumbHalfWidth + ratio * (rect.width - 2 * thumbHalfWidth);
      return { left: x - thumbHalfWidth, top: rect.top, right: x + thumbHalfWidth, bottom: rect.bottom };
    });
    return {
      element: input,
      bindingLabel: "滑块位置",
      // Native range centers stop half a thumb width inside either track endpoint.
      geometry: () => {
        if (span() <= 0) return null;
        const rect = input.getBoundingClientRect();
        const y = (rect.top + rect.bottom) / 2;
        return { center: "滑块圆心", atMin: { x: rect.left + thumbHalfWidth, y }, atMax: { x: rect.right - thumbHalfWidth, y } };
      },
      // The native input snaps rendering to min and step.
      render: ({ x }) => { input.value = String(props.bounds.minimum + x * span()); },
      // The native range exposes aria-valuenow from input.value.
      aria: () => `value=${input.value}`
    };
  };

  useInteraction(`数值滑条「${props.ariaLabel}」`, controlSliderDefinition, view, solidHost<number>({
    value: () => (span() > 0 ? (props.value - props.bounds.minimum) / span() : 0),
    // A single-value range cannot drag and does not participate in routing.
    enabled: () => !props.disabled && span() > 0,
    call: (port, args) => {
      if (port === "select") {
        const value = valueAt(Number(args[0]));
        if (args[1] === true) commit(value);
        else props.onPreview(value);
      } else if (port === "step") {
        // Match native large steps: the greater of one step or one tenth of the range.
        const unit = effectiveStep(props.bounds.step);
        const by = args[1] === true ? Math.max(unit, span() / 10) : unit;
        commit(snapControlNumber(props.value + Number(args[0]) * by, props.bounds));
      } else if (port === "cancel") {
        props.onPreview(null);
      }
    }
  }));

  return (
    <input
      ref={input}
      type="range"
      aria-label={props.ariaLabel}
      min={props.bounds.minimum}
      max={props.bounds.maximum}
      step={props.bounds.step}
      disabled={props.disabled}
      // The runtime owns pointer and keyboard changes; native input events handle direct assistive-technology edits.
      onInput={(event) => commit(snapControlNumber(Number(event.currentTarget.value), props.bounds))}
    />
  );
}
