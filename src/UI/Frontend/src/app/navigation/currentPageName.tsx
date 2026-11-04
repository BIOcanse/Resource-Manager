import { createContext, useContext } from "solid-js";
import type { Accessor, JSX } from "solid-js";

/* The navigation tree owns page names. The shell supplies the selected subpage name, or the page name when no subpage exists. Duplicate content headings remain accessible but visually hidden. */
const CurrentPageNameContext = createContext<Accessor<string>>();

export function CurrentPageNameProvider(props: { readonly name: Accessor<string>; readonly children: JSX.Element }) {
  return <CurrentPageNameContext.Provider value={props.name}>{props.children}</CurrentPageNameContext.Provider>;
}

export function useCurrentPageName(): Accessor<string> {
  const name = useContext(CurrentPageNameContext);
  if (!name) throw new Error("useCurrentPageName must be used inside the application shell.");
  return name;
}

/** Visually hidden current-page heading for screen readers and heading navigation. */
export function CurrentPageHeading(props: { readonly id?: string }) {
  const name = useCurrentPageName();
  return <h2 id={props.id} class="sr-only">{name()}</h2>;
}
