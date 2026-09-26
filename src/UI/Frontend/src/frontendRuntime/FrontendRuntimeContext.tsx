import { createContext, useContext, type ParentProps } from "solid-js";
import type { FrontendRuntime } from "./FrontendRuntime.ts";

const FrontendRuntimeContext = createContext<FrontendRuntime>();

export function FrontendRuntimeProvider(
  props: ParentProps<{ runtime: FrontendRuntime }>)
{
  return (
    <FrontendRuntimeContext.Provider value={props.runtime}>
      {props.children}
    </FrontendRuntimeContext.Provider>
  );
}

export function useFrontendRuntime(): FrontendRuntime {
  const runtime = useContext(FrontendRuntimeContext);
  if (!runtime) {
    throw new Error("FrontendRuntimeProvider is missing.");
  }
  return runtime;
}
