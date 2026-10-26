import { For } from "solid-js";
import { RadioGroupItem, RadioGroupRoot } from "./RadioGroup.tsx";
import { useSelectionIndicator } from "../../interactions/solid/selectionIndicator.ts";

export interface SegmentedControlOption<T extends string> {
  readonly id: T;
  readonly label: string;
  readonly description?: string;
  readonly disabled?: boolean;
  readonly classList?: Record<string, boolean | undefined>;
}

/**
 * 分段选择（单选）。选中的样子画在一块会滑的指示条上（磁石选中指示条，docs/page_motion_2026-10-25.md），
 * 序号是选项的次序；样子在 styles/segmented.css。
 */
export function SegmentedControl<T extends string>(props: {
  readonly value: T;
  readonly options: readonly SegmentedControlOption<T>[];
  readonly ariaLabel: string;
  readonly class?: string;
  readonly itemClass?: string;
  readonly disabled?: boolean;
  readonly disabledTitle?: string;
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
  return (
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
            title={props.disabled ? props.disabledTitle : option.description}
          >
            {option.label}
          </RadioGroupItem>
        )}
      </For>
    </RadioGroupRoot>
  );
}
