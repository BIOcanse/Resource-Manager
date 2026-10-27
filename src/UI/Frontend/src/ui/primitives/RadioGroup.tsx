import {
  createContext,
  createMemo,
  createSignal,
  createUniqueId,
  onCleanup,
  useContext,
  type Accessor,
  type JSX
} from "solid-js";
import { moveRovingFocus } from "./RovingFocus.ts";

interface RadioGroupItemEntry {
  readonly value: string;
  readonly disabled: Accessor<boolean>;
}

interface RadioGroupContextValue {
  readonly value: () => string;
  readonly setValue: (value: string) => void;
  readonly disabled: () => boolean;
  readonly idPrefix: string;
  // 组内唯一的 Tab 停靠项：选中且可用的项；没有选中项或选中项不可用时是第一个可用项，组才能被 Tab 到。
  readonly tabStopValue: Accessor<string | null>;
  readonly registerItem: (entry: RadioGroupItemEntry) => void;
}

const RadioGroupContext = createContext<RadioGroupContextValue>();

export function RadioGroupRoot(props: {
  readonly value: string;
  readonly onChange: (value: string) => void;
  readonly ariaLabel: string;
  readonly id?: string;
  readonly class?: string;
  readonly disabled?: boolean;
  readonly orientation?: "horizontal" | "vertical";
  /** 组元素（分段选择的指示条按它量）。 */
  readonly ref?: (element: HTMLDivElement) => void;
  readonly children: JSX.Element;
}) {
  const generatedId = createUniqueId();
  const orientation = () => props.orientation ?? "horizontal";
  const [items, setItems] = createSignal<readonly RadioGroupItemEntry[]>([]);
  const tabStopValue = createMemo(() => {
    const enabled = items().filter((item) => !item.disabled());
    return (enabled.find((item) => item.value === props.value) ?? enabled[0])?.value ?? null;
  });
  const context: RadioGroupContextValue = {
    value: () => props.value,
    setValue: props.onChange,
    disabled: () => Boolean(props.disabled),
    idPrefix: props.id ?? `radio-group-${generatedId}`,
    tabStopValue,
    registerItem: (entry) => {
      setItems((current) => [...current, entry]);
      onCleanup(() => setItems((current) => current.filter((item) => item !== entry)));
    }
  };

  return (
    <RadioGroupContext.Provider value={context}>
      <div
        ref={(element) => props.ref?.(element)}
        class={props.class}
        role="radiogroup"
        aria-label={props.ariaLabel}
        aria-disabled={props.disabled ? "true" : undefined}
        aria-orientation={orientation()}
        onKeyDown={(event) => {
          if (props.disabled) {
            return;
          }
          moveRovingFocus(event, {
            selector: "[role='radio']:not(:disabled)",
            previousKeys: ["ArrowLeft", "ArrowUp"],
            nextKeys: ["ArrowRight", "ArrowDown"],
            activate: (element) => element.click()
          });
        }}
      >
        {props.children}
      </div>
    </RadioGroupContext.Provider>
  );
}

export function RadioGroupItem(props: {
  readonly value: string;
  readonly class?: string;
  readonly classList?: Record<string, boolean | undefined>;
  readonly disabled?: boolean;
  readonly children: JSX.Element;
}) {
  const context = requireRadioGroupContext();
  const value = normalizeRadioValue(props.value);
  const selected = () => context.value() === value;
  const disabled = () => context.disabled() || Boolean(props.disabled);
  context.registerItem({ value, disabled });

  return (
    <button
      id={`${context.idPrefix}-radio-${value}`}
      class={props.class}
      classList={{ ...props.classList, active: selected() }}
      type="button"
      role="radio"
      aria-checked={selected()}
      tabIndex={context.tabStopValue() === value ? 0 : -1}
      disabled={disabled()}
      onClick={() => context.setValue(value)}
    >
      {props.children}
    </button>
  );
}

function requireRadioGroupContext(): RadioGroupContextValue {
  const context = useContext(RadioGroupContext);
  if (!context) {
    throw new Error("RadioGroup components must be nested inside RadioGroupRoot.");
  }
  return context;
}

function normalizeRadioValue(value: string) {
  const normalized = value.trim();
  if (!normalized || !/^[a-z0-9_-]+$/iu.test(normalized)) {
    throw new Error("A stable radio value is required.");
  }
  return normalized;
}
