import { For, Show } from "solid-js";
import { OptionsHelpHint } from "./HelpHint.tsx";
import { RadioGroupItem, RadioGroupRoot } from "./RadioGroup.tsx";
import { useSelectionIndicator } from "../../interactions/solid/selectionIndicator.ts";

export interface SegmentedControlOption<T extends string> {
  readonly id: T;
  readonly label: string;
  /** 这一项的说明：控件旁一个问号，里面逐项列出（OptionsHelpHint），不挂在选项上。 */
  readonly description?: string;
  readonly disabled?: boolean;
  readonly classList?: Record<string, boolean | undefined>;
}

/**
 * 分段选择（单选）。选中的样子画在一块会滑的指示条上（磁石选中指示条，docs/page_motion_2026-10-25.md），
 * 序号是选项的次序；样子在 styles/segmented.css。
 * 有选项带说明时，控件后面跟一个问号，说明里逐项列出（和控件包在一个 .with-help-hint 里，在所在的行里仍只算一项）。
 */
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
