import {
  Show,
  createEffect,
  createContext,
  createUniqueId,
  useContext,
  type JSX
} from "solid-js";
import { moveRovingFocus } from "./RovingFocus.ts";

interface TabsContextValue {
  readonly value: () => string;
  readonly setValue: (value: string) => void;
  readonly idPrefix: string;
}

const TabsContext = createContext<TabsContextValue>();

export function TabsRoot(props: {
  readonly value: string;
  readonly onChange: (value: string) => void;
  readonly id?: string;
  readonly class?: string;
  readonly children: JSX.Element;
}) {
  const generatedId = createUniqueId();
  const context: TabsContextValue = {
    value: () => props.value,
    setValue: props.onChange,
    idPrefix: props.id ?? `tabs-${generatedId}`
  };
  return (
    <TabsContext.Provider value={context}>
      <div class={props.class}>{props.children}</div>
    </TabsContext.Provider>
  );
}

export function TabsList(props: {
  readonly ariaLabel: string;
  readonly class?: string;
  readonly children: JSX.Element;
}) {
  const context = requireTabsContext();
  let listElement: HTMLDivElement | undefined;
  createEffect(() => {
    const value = context.value();
    const trigger = listElement?.querySelector<HTMLElement>(
      `[role='tab'][data-tab-value='${CSS.escape(value)}']`);
    if (trigger && listElement) {
      ensureTabVisible(trigger, listElement);
    }
  });
  return (
    <div
      ref={listElement}
      class={props.class}
      role="tablist"
      aria-label={props.ariaLabel}
      aria-orientation="horizontal"
      onKeyDown={(event) => moveRovingFocus(event, {
        selector: "[role='tab']:not(:disabled)",
        previousKeys: ["ArrowLeft"],
        nextKeys: ["ArrowRight"],
        ensureVisible: ensureTabVisible,
        activate: (element) => element.click()
      })}
    >
      {props.children}
    </div>
  );
}

function ensureTabVisible(element: HTMLElement, container: HTMLElement): void {
  const elementRect = element.getBoundingClientRect();
  const containerRect = container.getBoundingClientRect();
  if (elementRect.left < containerRect.left) {
    container.scrollLeft -= containerRect.left - elementRect.left;
  } else if (elementRect.right > containerRect.right) {
    container.scrollLeft += elementRect.right - containerRect.right;
  }
}

export function TabsTrigger(props: {
  readonly value: string;
  readonly id?: string;
  readonly controls?: string;
  readonly class?: string;
  readonly disabled?: boolean;
  readonly children: JSX.Element;
}) {
  const context = requireTabsContext();
  const value = normalizeTabValue(props.value);
  const selected = () => context.value() === value;
  return (
    <button
      id={props.id ?? `${context.idPrefix}-tab-${value}`}
      class={props.class}
      classList={{ active: selected() }}
      type="button"
      role="tab"
      aria-selected={selected()}
      aria-controls={props.controls ?? `${context.idPrefix}-panel-${value}`}
      tabIndex={selected() ? 0 : -1}
      disabled={props.disabled}
      data-tab-value={value}
      onClick={() => context.setValue(value)}
    >
      {props.children}
    </button>
  );
}

export function TabsPanel(props: {
  readonly value: string;
  readonly class?: string;
  readonly children: JSX.Element;
}) {
  const context = requireTabsContext();
  const value = normalizeTabValue(props.value);
  const selected = () => context.value() === value;
  return (
    <div
      id={`${context.idPrefix}-panel-${value}`}
      class={props.class}
      role="tabpanel"
      hidden={!selected()}
      tabIndex={selected() ? 0 : -1}
      aria-labelledby={`${context.idPrefix}-tab-${value}`}
    >
      <Show when={selected()}>
        {props.children}
      </Show>
    </div>
  );
}

function requireTabsContext(): TabsContextValue {
  const context = useContext(TabsContext);
  if (!context) {
    throw new Error("Tabs components must be nested inside TabsRoot.");
  }
  return context;
}

function normalizeTabValue(value: string): string {
  const normalized = value.trim();
  if (!normalized || !/^[a-z0-9_-]+$/iu.test(normalized)) {
    throw new Error("A stable tab value is required.");
  }
  return normalized;
}
