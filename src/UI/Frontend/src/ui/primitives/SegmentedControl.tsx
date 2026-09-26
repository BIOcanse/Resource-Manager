import { For } from "solid-js";
import { RadioGroupItem, RadioGroupRoot } from "./RadioGroup.tsx";

export interface SegmentedControlOption<T extends string> {
  readonly id: T;
  readonly label: string;
  readonly description?: string;
  readonly disabled?: boolean;
  readonly classList?: Record<string, boolean | undefined>;
}

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
  return (
    <RadioGroupRoot
      value={props.value}
      ariaLabel={props.ariaLabel}
      class={props.class}
      disabled={props.disabled}
      onChange={(value) => props.onChange(value as T)}
    >
      <For each={props.options}>
        {(option) => (
          <RadioGroupItem
            value={option.id}
            class={props.itemClass}
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
