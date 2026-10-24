import { createContext, useContext, type Accessor, type ParentProps } from "solid-js";
import type { FrontendReadIntervals, FrontendRefreshIntervals } from "../types.ts";

interface FrontendReadIntervalView {
  readonly configuration: Accessor<FrontendRefreshIntervals | null>;
  readonly current: Accessor<FrontendReadIntervals | null>;
}
const FrontendReadIntervalsContext = createContext<FrontendReadIntervalView>();

export function FrontendReadIntervalsProvider(props: ParentProps<FrontendReadIntervalView>) {
  return <FrontendReadIntervalsContext.Provider value={props}>{props.children}</FrontendReadIntervalsContext.Provider>;
}

export function useFrontendReadIntervals(): FrontendReadIntervalView {
  const view = useContext(FrontendReadIntervalsContext);
  if (!view) throw new Error("FrontendReadIntervalsProvider is missing.");
  return view;
}
