import { createComputed, createEffect, createUniqueId, on } from "solid-js";
import type { JSX } from "solid-js";
import { switchToggleDefinition } from "../../interactions/components/switchToggle.ts";
import type { Presentation } from "../../interactions/protocol/runtime.ts";
import { solidHost, useInteraction } from "../../interactions/solid/lodestone.ts";
import { enterSlot, exitInPlace } from "../../presentation/nodeAnimation.ts";

/** Controlled disclosure with immediate layout and inert updates. Entry fades live content; exit fades a noninteractive copy. The arrow uses the switch channel without dragging; peek remains visible while collapsed. */
export function Disclosure(props: {
  /** Runtime instance name for traces. */
  name: string;
  expanded: boolean;
  onToggle: () => void;
  header: JSX.Element;
  class?: string;
  headerClass?: string;
  peek?: JSX.Element;
  children: JSX.Element;
}) {
  const bodyId = createUniqueId();
  let root!: HTMLDivElement;
  let header!: HTMLButtonElement;
  let content!: HTMLDivElement;
  // Capture exit before collapse; enter after expansion.
  createComputed(on(() => props.expanded, (expanded) => {
    if (!expanded && content) exitInPlace(content, { slot: "disclosure", layer: root });
  }, { defer: true }));
  createEffect(on(() => props.expanded, (expanded) => {
    if (expanded) enterSlot(content, "disclosure");
  }, { defer: true }));
  const view = (): Presentation => ({
    element: header,
    bindingLabel: "箭头转角",
    geometry: () => null,
    render: ({ x }) => {
      header.style.setProperty("--disclosure-openness", x.toFixed(4));
    },
    // Solid owns aria-expanded through the host value.
    aria: (value) => `aria-expanded=${value >= 0.5}`
  });
  useInteraction(props.name, switchToggleDefinition, view, solidHost<number>({
    value: () => (props.expanded ? 1 : 0),
    enabled: () => true,
    // Only button activation changes expansion; this channel cannot drag.
    call: () => {}
  }));
  // Set the initial arrow angle once; runtime drawing avoids flashing the terminal angle.
  const initialOpenness = props.expanded ? 1 : 0;
  return (
    <div ref={root} class={props.class ? `ui-disclosure ${props.class}` : "ui-disclosure"}>
      <button ref={header} type="button" class={props.headerClass ? `ui-disclosure-header ${props.headerClass}` : "ui-disclosure-header"}
        aria-expanded={props.expanded} aria-controls={bodyId} onClick={() => props.onToggle()}
        style={{ "--disclosure-openness": initialOpenness }}>
        <span class="ui-disclosure-chevron" aria-hidden="true" />
        <span class="ui-disclosure-label">{props.header}</span>
      </button>
      {props.peek}
      <div id={bodyId} class="ui-disclosure-body" inert={props.expanded ? undefined : true} hidden={!props.expanded}>
        <div ref={content} class="ui-disclosure-content">{props.children}</div>
      </div>
    </div>
  );
}
