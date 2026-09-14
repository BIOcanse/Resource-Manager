import {
  createContext,
  createUniqueId,
  useContext,
  type JSX
} from "solid-js";
import { moveRovingFocus } from "./RovingFocus.ts";

interface RadioGroupContextValue {
  readonly value: () => string;
  readonly setValue: (value: string) => void;
  readonly disabled: () => boolean;
  readonly idPrefix: string;
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
  readonly children: JSX.Element;
}) {
  const generatedId = createUniqueId();
  const orientation = () => props.orientation ?? "horizontal";
  const context: RadioGroupContextValue = {
    value: () => props.value,
    setValue: props.onChange,
    disabled: () => Boolean(props.disabled),
    idPrefix: props.id ?? `radio-group-${generatedId}`
  };

  return (
    <RadioGroupContext.Provider value={context}>
      <div
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
  readonly title?: string;
  readonly children: JSX.Element;
}) {
  const context = requireRadioGroupContext();
  const value = normalizeRadioValue(props.value);
  const selected = () => context.value() === value;
  const disabled = () => context.disabled() || Boolean(props.disabled);

  return (
    <button
      id={`${context.idPrefix}-radio-${value}`}
      class={props.class}
      classList={{ ...props.classList, active: selected() }}
      type="button"
      role="radio"
      aria-checked={selected()}
      tabIndex={selected() && !disabled() ? 0 : -1}
      disabled={disabled()}
      title={props.title}
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
