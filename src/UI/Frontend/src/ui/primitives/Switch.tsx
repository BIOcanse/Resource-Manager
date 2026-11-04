import { Dynamic } from "solid-js/web";
import { switchToggleDefinition } from "../../interactions/components/switchToggle.ts";
import type { Presentation } from "../../interactions/protocol/runtime.ts";
import { solidHost, useInteraction } from "../../interactions/solid/lodestone.ts";

/** Native checkbox owns activation and accessibility; the runtime drives the draggable thumb. Use span when nested in another label. */
export function SwitchControl(props: {
  checked: boolean;
  disabled?: boolean;
  ariaLabel?: string;
  as?: "label" | "span";
  onChange: (checked: boolean) => void;
}) {
  let root!: HTMLElement;
  let track!: HTMLSpanElement;
  let knob!: HTMLSpanElement;
  // Thumb travel excludes thumb width and padding.
  const travel = () => root.clientWidth - knob.offsetWidth - 2 * knob.offsetLeft;
  const view = (): Presentation => ({
    element: root,
    bindingLabel: "位移",
    // The logical center moves one travel distance between openness 0 and 1.
    geometry: () => {
      const rect = root.getBoundingClientRect();
      const atMin = { x: rect.left + knob.offsetLeft + knob.offsetWidth / 2, y: rect.top + knob.offsetTop + knob.offsetHeight / 2 };
      return { center: "滑块圆心", atMin, atMax: { x: atMin.x + travel(), y: atMin.y } };
    },
    // Track color follows openness continuously while dragging.
    render: ({ x }) => {
      knob.style.translate = `${(x * travel()).toFixed(2)}px 0`;
      track.style.setProperty("--switch-openness", x.toFixed(4));
    },
    // Solid owns checked and its native accessible state.
    aria: (value) => `checked=${value >= 0.5}`
  });
  useInteraction("设置开关", switchToggleDefinition, view, solidHost<number>({
    value: () => (props.checked ? 1 : 0),
    enabled: () => props.disabled !== true,
    // Releasing at the original value is not a change.
    call: (port, args) => {
      const next = Number(args[0]) >= 0.5;
      if (port === "select" && next !== props.checked) props.onChange(next);
    }
  }));
  return (
    <Dynamic component={props.as ?? "label"} ref={(element: HTMLElement) => { root = element; }} class="settings-switch">
      <input
        type="checkbox"
        aria-label={props.ariaLabel}
        checked={props.checked}
        disabled={props.disabled}
        onChange={(event) => props.onChange(event.currentTarget.checked)}
      />
      <span ref={track} class="settings-switch-track">
        <span ref={knob} class="settings-switch-knob" />
      </span>
    </Dynamic>
  );
}
