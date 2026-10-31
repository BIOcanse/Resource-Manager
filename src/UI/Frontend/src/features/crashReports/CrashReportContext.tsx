import { createContext, useContext, type JSX } from "solid-js";
import type { CrashReportStore } from "./crashReportStore.ts";

/** App 创建的崩溃记录使用方，设置 · 系统集成的崩溃记录列表从这里取。 */
const CrashReportContext = createContext<CrashReportStore>();

export function CrashReportProvider(props: { readonly store: CrashReportStore; readonly children: JSX.Element }) {
  return <CrashReportContext.Provider value={props.store}>{props.children}</CrashReportContext.Provider>;
}

export function useCrashReports(): CrashReportStore | undefined {
  return useContext(CrashReportContext);
}
