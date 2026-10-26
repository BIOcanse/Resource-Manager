import { createContext, useContext } from "solid-js";
import type { Accessor, JSX } from "solid-js";

/*
 * 当前所在处的名字（docs/navigation_tree_2026-10-25.md“页名”）：分页树是名字的唯一所有者。
 * 外壳提供：选中的二级分页的名字，没有二级分页时是一级页的名字。
 * 页面里和选中的分页同名的标题只给读屏和标题导航用，不显示（顶上的分页条已经高亮着这个名字）。
 */
const CurrentPageNameContext = createContext<Accessor<string>>();

export function CurrentPageNameProvider(props: { readonly name: Accessor<string>; readonly children: JSX.Element }) {
  return <CurrentPageNameContext.Provider value={props.name}>{props.children}</CurrentPageNameContext.Provider>;
}

export function useCurrentPageName(): Accessor<string> {
  const name = useContext(CurrentPageNameContext);
  if (!name) throw new Error("useCurrentPageName must be used inside the application shell.");
  return name;
}

/** 页面区块的标题：就是当前分页的名字，只给读屏和标题导航，不显示。 */
export function CurrentPageHeading(props: { readonly id?: string }) {
  const name = useCurrentPageName();
  return <h2 id={props.id} class="sr-only">{name()}</h2>;
}
