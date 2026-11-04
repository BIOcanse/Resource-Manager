import { For, Show } from "solid-js";
import { OptionsHelpHint } from "./HelpHint.tsx";
import { RadioGroupItem, RadioGroupRoot } from "./RadioGroup.tsx";
import { useSelectionIndicator } from "../../interactions/solid/selectionIndicator.ts";

export interface SegmentedControlOption<T extends string> {
  readonly id: T;
  readonly label: string;
  /** Option explanation listed in a shared adjacent help hint. */
  readonly description?: string;
  readonly disabled?: boolean;
  readonly classList?: Record<string, boolean | undefined>;
}

/** Single selection with an indicator driven by option order and one optional shared help hint. */
export function SegmentedControl<T extends string>(props: {
  readonly value: T;
  readonly options: readonly SegmentedControlOption<T>[];
  readonly ariaLabel: string;
  readonly class?: string;
  readonly itemClass?: string;
  readonly disabled?: boolean;
  readonly onChange: (value: T) => void;
}) {
  let group: HTMLDivElement | undefined;
  let indicator: HTMLSpanElement | undefined;
  useSelectionIndicator({
    name: `分段选择 ${props.ariaLabel}`,
    container: () => group,
    indicator: () => indicator,
    selected: () => group?.querySelector<HTMLElement>("[role='radio'][aria-checked='true']") ?? null,
    items: () => [...(group?.querySelectorAll<HTMLElement>("[role='radio']") ?? [])],
    order: () => Math.max(0, props.options.findIndex((option) => option.id === props.value))
  });
  const hints = () => props.options.flatMap((option) => option.description ? [{ label: option.label, text: option.description }] : []);
  const control = () => (
    <RadioGroupRoot
      ref={(element) => { group = element; }}
      value={props.value}
      ariaLabel={props.ariaLabel}
      class={props.class ? `segmented has-selection-indicator ${props.class}` : "segmented has-selection-indicator"}
      disabled={props.disabled}
      onChange={(value) => props.onChange(value as T)}
    >
      <span ref={indicator} class="selection-indicator segment-indicator" aria-hidden="true" />
      <For each={props.options}>
        {(option) => (
          <RadioGroupItem
            value={option.id}
            class={props.itemClass ? `segment ${props.itemClass}` : "segment"}
            classList={option.classList}
            disabled={option.disabled}
          >
            {option.label}
          </RadioGroupItem>
        )}
      </For>
    </RadioGroupRoot>
  );
  return (
    <Show when={hints().length > 0} fallback={control()}>
      <span class="with-help-hint">
        {control()}
        <OptionsHelpHint entries={hints()} />
      </span>
    </Show>
  );
}
