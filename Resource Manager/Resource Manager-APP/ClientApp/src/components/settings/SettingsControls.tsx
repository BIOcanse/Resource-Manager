import { createEffect, createSignal, For } from "solid-js";
import { SegmentedControl as PrimitiveSegmentedControl } from "../../ui/primitives/SegmentedControl.tsx";
import {
  formatNumberFieldValue,
  intersectNumberFieldRanges,
  resolveNumberFieldCommit,
  type NumberFieldRange
} from "./numberFieldModel";

export function SegmentedControl<T extends string>(props: {
  value: T;
  options: Array<{ id: T; label: string; description?: string }>;
  ariaLabel: string;
  disabled?: boolean;
  disabledTitle?: string;
  onChange: (value: T) => void;
}) {
  return (
    <PrimitiveSegmentedControl
      value={props.value}
      options={props.options}
      ariaLabel={props.ariaLabel}
      class="settings-segmented-control"
      itemClass="settings-segment"
      disabled={props.disabled}
      disabledTitle={props.disabledTitle}
      onChange={props.onChange}
    />
  );
}

export function MultiSegmentedControl<T extends string>(props: {
  values: readonly T[];
  options: Array<{ id: T; label: string; description?: string }>;
  ariaLabel: string;
  onChange: (values: T[]) => void;
}) {
  const hasValue = (id: T) => props.values.includes(id);
  const toggle = (id: T) => {
    const next = hasValue(id)
      ? props.values.filter((value) => value !== id)
      : [...props.values, id];
    props.onChange(next);
  };

  return (
    <div class="settings-segmented-control" role="group" aria-label={props.ariaLabel}>
      <For each={props.options}>
        {(option) => (
          <button
            type="button"
            class="settings-segment"
            role="checkbox"
            aria-checked={hasValue(option.id) ? "true" : "false"}
            classList={{ active: hasValue(option.id) }}
            title={option.description}
            onClick={() => toggle(option.id)}
          >
            {option.label}
          </button>
        )}
      </For>
    </div>
  );
}

export function NumberField(props: {
  label: string;
  value: number;
  min: number;
  max: number;
  step: number;
  allowedRanges?: readonly NumberFieldRange[];
  disabled?: boolean;
  onChange: (value: number) => void;
}) {
  const [editing, setEditing] = createSignal(false);
  const [draft, setDraft] = createSignal(formatNumberFieldValue(props.value, props.step));
  const effectiveRanges = () => intersectNumberFieldRanges(
    props.allowedRanges ?? [{ minimum: props.min, maximum: props.max }],
    props.min,
    props.max);

  createEffect(() => {
    if (!editing()) {
      setDraft(formatNumberFieldValue(props.value, props.step));
    }
  });

  const commit = () => {
    const next = resolveNumberFieldCommit(
      draft(),
      props.value,
      props.step,
      effectiveRanges());
    setEditing(false);
    setDraft(formatNumberFieldValue(next, props.step));
    if (next !== props.value) props.onChange(next);
  };

  return (
    <label class="settings-number-field">
      <span>{props.label}</span>
      <input
        type="text"
        inputMode={props.step < 1 ? "decimal" : "numeric"}
        value={draft()}
        disabled={props.disabled}
        onFocus={() => setEditing(true)}
        onInput={(event) => setDraft(event.currentTarget.value)}
        onBlur={commit}
        onKeyDown={(event) => {
          if (event.key === "Enter") event.currentTarget.blur();
          if (event.key === "Escape") {
            setDraft(formatNumberFieldValue(props.value, props.step));
            event.currentTarget.blur();
          }
        }}
      />
    </label>
  );
}
