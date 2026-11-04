import { createContext, useContext, type JSX } from "solid-js";
import type { CrashReportStore } from "./crashReportStore.ts";

/** Share the app-owned crash-record consumer with the report list. */
const CrashReportContext = createContext<CrashReportStore>();

export function CrashReportProvider(props: { readonly store: CrashReportStore; readonly children: JSX.Element }) {
  return <CrashReportContext.Provider value={props.store}>{props.children}</CrashReportContext.Provider>;
}

export function useCrashReports(): CrashReportStore | undefined {
  return useContext(CrashReportContext);
}
