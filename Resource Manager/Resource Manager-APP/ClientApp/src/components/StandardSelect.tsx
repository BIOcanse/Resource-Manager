import { Check, ChevronDown, Search } from "lucide-solid";
import { createEffect, createMemo, createSignal, For, onCleanup, Show } from "solid-js";
import { Portal } from "solid-js/web";

export interface StandardSelectOption<T extends string = string> {
  value: T;
  label: string;
  group?: string;
  disabled?: boolean;
}

interface StandardSelectPosition {
  left: number;
  top: number;
  width: number;
  maxHeight: number;
}

let nextStandardSelectId = 0;

export function StandardSelect<T extends string>(props: {
  value: T;
  options: readonly StandardSelectOption<T>[];
  ariaLabel: string;
  disabled?: boolean;
  searchable?: boolean;
  searchPlaceholder?: string;
  class?: string;
  onChange: (value: T) => void;
}) {
  const instanceId = `standard-select-${++nextStandardSelectId}`;
  const listboxId = `${instanceId}-listbox`;
  let trigger: HTMLButtonElement | undefined;
  let popup: HTMLDivElement | undefined;
  let searchInput: HTMLInputElement | undefined;
  const [open, setOpen] = createSignal(false);
  const [query, setQuery] = createSignal("");
  const [activeIndex, setActiveIndex] = createSignal(0);
  const [position, setPosition] = createSignal<StandardSelectPosition>({ left: 8, top: 8, width: 220, maxHeight: 320 });
  const selected = createMemo(() => props.options.find((option) => option.value === props.value));
  const visibleOptions = createMemo(() => {
    const filter = query().trim().toLocaleLowerCase();
    return props.options.filter((option) => !filter
      || option.label.toLocaleLowerCase().includes(filter)
      || option.group?.toLocaleLowerCase().includes(filter));
  });
  const activeOptionId = createMemo(() => {
    const option = visibleOptions()[activeIndex()];
    return option && !option.disabled ? `${instanceId}-option-${activeIndex()}` : undefined;
  });

  createEffect(() => {
    if (!open()) {
      return;
    }

    updatePosition();
    const selectedIndex = visibleOptions().findIndex((option) => option.value === props.value && !option.disabled);
    setActiveIndex(selectedIndex >= 0 ? selectedIndex : firstEnabledIndex(visibleOptions()));
    const closeOnOutsidePointer = (event: PointerEvent) => {
      const target = event.target as Node | null;
      if (target && !trigger?.contains(target) && !popup?.contains(target)) {
        close();
      }
    };
    const closeOnWindowBlur = () => close();
    const reposition = () => updatePosition();
    document.addEventListener("pointerdown", closeOnOutsidePointer, true);
    window.addEventListener("resize", reposition);
    window.addEventListener("scroll", reposition, true);
    window.addEventListener("blur", closeOnWindowBlur);
    window.requestAnimationFrame(() => {
      if (props.searchable) {
        searchInput?.focus();
      } else {
        focusActiveOption();
      }
    });

    onCleanup(() => {
      document.removeEventListener("pointerdown", closeOnOutsidePointer, true);
      window.removeEventListener("resize", reposition);
      window.removeEventListener("scroll", reposition, true);
      window.removeEventListener("blur", closeOnWindowBlur);
    });
  });

  createEffect(() => {
    query();
    if (open()) {
      setActiveIndex(firstEnabledIndex(visibleOptions()));
    }
  });

  const openPopup = () => {
    if (props.disabled || open()) {
      return;
    }
    setQuery("");
    setOpen(true);
  };

  const close = () => {
    setOpen(false);
    setQuery("");
  };

  const choose = (option: StandardSelectOption<T>) => {
    if (option.disabled) {
      return;
    }
    props.onChange(option.value);
    close();
    trigger?.focus();
  };

  const moveActive = (direction: 1 | -1) => {
    const options = visibleOptions();
    if (options.length === 0) {
      return;
    }
    let index = activeIndex();
    if (index < 0) {
      index = direction === 1 ? -1 : 0;
    }
    for (let attempt = 0; attempt < options.length; attempt += 1) {
      index = (index + direction + options.length) % options.length;
      if (!options[index].disabled) {
        setActiveIndex(index);
        if (props.searchable) {
          scrollActiveOptionIntoView(index);
        } else {
          focusActiveOption();
        }
        return;
      }
    }
  };

  const moveToBoundary = (boundary: "first" | "last") => {
    const index = boundary === "first"
      ? firstEnabledIndex(visibleOptions())
      : lastEnabledIndex(visibleOptions());
    if (index < 0) {
      return;
    }

    setActiveIndex(index);
    if (props.searchable) {
      scrollActiveOptionIntoView(index);
    } else {
      focusActiveOption();
    }
  };

  const handlePopupKeyDown = (event: KeyboardEvent) => {
    if (event.key === "Escape") {
      event.preventDefault();
      close();
      trigger?.focus();
      return;
    }
    if (event.key === "Tab") {
      event.preventDefault();
      const backwards = event.shiftKey;
      close();
      queueMicrotask(() => focusAdjacentToTrigger(backwards));
      return;
    }
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      moveActive(event.key === "ArrowDown" ? 1 : -1);
      return;
    }
    if (event.key === "Home" || event.key === "End") {
      event.preventDefault();
      moveToBoundary(event.key === "Home" ? "first" : "last");
      return;
    }
    if (event.key === "Enter" && !event.isComposing) {
      const option = visibleOptions()[activeIndex()];
      if (option) {
        event.preventDefault();
        choose(option);
      }
    }
  };

  const updatePosition = () => {
    if (!trigger) {
      return;
    }
    const rect = trigger.getBoundingClientRect();
    const viewportPadding = 8;
    const gap = 6;
    const desiredWidth = Math.max(rect.width, props.searchable ? 300 : 180);
    const width = Math.min(desiredWidth, window.innerWidth - viewportPadding * 2);
    const below = window.innerHeight - rect.bottom - viewportPadding - gap;
    const above = rect.top - viewportPadding - gap;
    const openAbove = below < 220 && above > below;
    const maxHeight = Math.max(140, Math.min(420, openAbove ? above : below));
    const left = Math.min(
      Math.max(viewportPadding, rect.left),
      Math.max(viewportPadding, window.innerWidth - width - viewportPadding));
    const top = openAbove
      ? Math.max(viewportPadding, rect.top - maxHeight - gap)
      : Math.min(window.innerHeight - viewportPadding - maxHeight, rect.bottom + gap);
    setPosition({ left, top, width, maxHeight });
  };

  const focusActiveOption = () => {
    window.requestAnimationFrame(() => {
      popup?.querySelector<HTMLButtonElement>(`[data-standard-select-index="${activeIndex()}"]`)?.focus();
    });
  };

  const scrollActiveOptionIntoView = (index: number) => {
    window.requestAnimationFrame(() => {
      popup?.querySelector<HTMLElement>(`[data-standard-select-index="${index}"]`)
        ?.scrollIntoView({ block: "nearest" });
    });
  };

  const focusAdjacentToTrigger = (backwards: boolean) => {
    if (!trigger) {
      return;
    }

    const focusable = document.querySelectorAll<HTMLElement>(
      "button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), "
        + "textarea:not([disabled]), [tabindex]:not([tabindex='-1'])");
    const ordered = Array.from(focusable).filter((element) =>
      !element.inert
      && !element.closest("[inert]")
      && element.getClientRects().length > 0);
    const triggerIndex = ordered.indexOf(trigger);
    const target = triggerIndex < 0
      ? undefined
      : ordered[triggerIndex + (backwards ? -1 : 1)];
    (target ?? trigger).focus({ preventScroll: true });
  };

  return (
    <div class={`standard-select ${props.class ?? ""}`}>
      <button
        ref={trigger}
        type="button"
        class="standard-select-trigger"
        disabled={props.disabled}
        role={props.searchable ? undefined : "combobox"}
        aria-label={props.ariaLabel}
        aria-expanded={open()}
        aria-haspopup="listbox"
        aria-controls={listboxId}
        onClick={() => open() ? close() : openPopup()}
        onKeyDown={(event) => {
          if (open() && event.key === "Tab") {
            event.preventDefault();
            const backwards = event.shiftKey;
            close();
            queueMicrotask(() => focusAdjacentToTrigger(backwards));
            return;
          }
          if (open() && event.key === "Escape") {
            event.preventDefault();
            close();
            return;
          }
          if (open() && (event.key === "ArrowDown" || event.key === "ArrowUp")) {
            event.preventDefault();
            moveActive(event.key === "ArrowDown" ? 1 : -1);
            return;
          }
          if (event.key === "ArrowDown" || event.key === "ArrowUp" || event.key === "Enter" || event.key === " ") {
            event.preventDefault();
            openPopup();
          }
        }}
      >
        <span>{selected()?.label ?? props.value}</span>
        <ChevronDown size={16} aria-hidden="true" />
      </button>
      <Show when={open()}>
        <Portal>
          <div
            ref={popup}
            class="standard-select-popover"
            data-modal-focus-portal="true"
            style={{
              left: `${position().left}px`,
              top: `${position().top}px`,
              width: `${position().width}px`,
              "max-height": `${position().maxHeight}px`
            }}
            onKeyDown={handlePopupKeyDown}
          >
            <Show when={props.searchable}>
              <label class="standard-select-search">
                <Search size={15} aria-hidden="true" />
                <input
                  ref={searchInput}
                  role="combobox"
                  value={query()}
                  placeholder={props.searchPlaceholder ?? "搜索"}
                  aria-label={props.searchPlaceholder ?? "搜索选项"}
                  aria-expanded="true"
                  aria-autocomplete="list"
                  aria-controls={listboxId}
                  aria-activedescendant={activeOptionId()}
                  onInput={(event) => setQuery(event.currentTarget.value)}
                />
              </label>
            </Show>
            <div id={listboxId} class="standard-select-options" role="listbox" aria-label={props.ariaLabel}>
              <For each={visibleOptions()} fallback={<div class="standard-select-empty">没有匹配项</div>}>
                {(option, index) => {
                  const previousGroup = () => index() > 0 ? visibleOptions()[index() - 1]?.group : undefined;
                  return (
                    <>
                      <Show when={option.group && option.group !== previousGroup()}>
                        <div class="standard-select-group">{option.group}</div>
                      </Show>
                      <button
                        id={`${instanceId}-option-${index()}`}
                        type="button"
                        role="option"
                        data-standard-select-index={index()}
                        aria-selected={option.value === props.value}
                        tabIndex={!props.searchable && index() === activeIndex() ? 0 : -1}
                        classList={{ active: index() === activeIndex(), selected: option.value === props.value }}
                        disabled={option.disabled}
                        onFocus={() => {
                          if (!option.disabled) {
                            setActiveIndex(index());
                          }
                        }}
                        onPointerMove={() => {
                          if (!option.disabled) {
                            setActiveIndex(index());
                          }
                        }}
                        onClick={() => choose(option)}
                      >
                        <span>{option.label}</span>
                        <Show when={option.value === props.value}>
                          <Check size={16} aria-hidden="true" />
                        </Show>
                      </button>
                    </>
                  );
                }}
              </For>
            </div>
          </div>
        </Portal>
      </Show>
    </div>
  );
}

function firstEnabledIndex(options: readonly StandardSelectOption[]) {
  return options.findIndex((option) => !option.disabled);
}

function lastEnabledIndex(options: readonly StandardSelectOption[]) {
  for (let index = options.length - 1; index >= 0; index -= 1) {
    if (!options[index].disabled) {
      return index;
    }
  }
  return -1;
}
