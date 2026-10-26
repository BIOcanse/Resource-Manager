import {
  Show,
  createEffect,
  createContext,
  createSignal,
  createUniqueId,
  onCleanup,
  useContext,
  type Accessor,
  type JSX
} from "solid-js";
import { moveRovingFocus } from "./RovingFocus.ts";
import { useSelectionIndicator } from "../../interactions/solid/selectionIndicator.ts";
import { useContentTransition } from "../../interactions/solid/contentTransition.ts";

/*
 * 页签组：一组页签（TabsList / TabsTrigger）和它们的面板（TabsPanel）。
 * 动效（docs/page_motion_2026-10-25.md）：页签组自带选中指示条，面板换的时候旧面板出去、新面板从序号增大的一侧进来，
 * 都是磁石组件，跟手感和动画档位走。页签组的序号就是页签的次序。
 */

interface TabsContextValue {
  readonly value: () => string;
  readonly setValue: (value: string) => void;
  readonly idPrefix: string;
  /** 页签按渲染次序登记：序号 = 在这里的位置。 */
  readonly order: Accessor<number>;
  readonly registerTrigger: (value: string) => void;
  readonly registerPanel: (value: string, element: HTMLElement) => void;
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
  const [triggers, setTriggers] = createSignal<readonly string[]>([]);
  const panels = new Map<string, HTMLElement>();
  let root: HTMLDivElement | undefined;
  const order = () => Math.max(0, triggers().indexOf(props.value));
  const context: TabsContextValue = {
    value: () => props.value,
    setValue: props.onChange,
    idPrefix: props.id ?? `tabs-${generatedId}`,
    order,
    registerTrigger: (value) => {
      setTriggers((current) => [...current, value]);
      onCleanup(() => setTriggers((current) => current.filter((item) => item !== value)));
    },
    registerPanel: (value, element) => {
      panels.set(value, element);
      onCleanup(() => {
        if (panels.get(value) === element) panels.delete(value);
      });
    }
  };
  // 面板换的时候：旧面板的副本出去，新面板进来。没有面板的页签组（例如外壳的二级分页条）什么也不做。
  useContentTransition({
    name: "页签面板",
    key: () => props.value,
    order,
    stage: () => panels.get(props.value),
    layer: () => root,
    snapshot: () => "clone",
    distance: 16
  });
  return (
    <TabsContext.Provider value={context}>
      <div ref={root} class={props.class ? `ui-tabs-root ${props.class}` : "ui-tabs-root"}>{props.children}</div>
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
  let indicatorElement: HTMLSpanElement | undefined;
  createEffect(() => {
    const value = context.value();
    const trigger = listElement?.querySelector<HTMLElement>(
      `[role='tab'][data-tab-value='${CSS.escape(value)}']`);
    if (trigger && listElement) {
      ensureTabVisible(trigger, listElement);
    }
  });
  useSelectionIndicator({
    name: `页签组 ${context.idPrefix}`,
    container: () => listElement,
    indicator: () => indicatorElement,
    selected: () => listElement?.querySelector<HTMLElement>("[role='tab'][aria-selected='true']") ?? null,
    items: () => [...(listElement?.querySelectorAll<HTMLElement>("[role='tab']") ?? [])],
    order: context.order
  });
  return (
    <div
      ref={listElement}
      class={props.class ? `${props.class} has-selection-indicator` : "has-selection-indicator"}
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
      <span ref={indicatorElement} class="selection-indicator page-tab-indicator" aria-hidden="true" />
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
  context.registerTrigger(value);
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
      ref={(element) => context.registerPanel(value, element)}
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
