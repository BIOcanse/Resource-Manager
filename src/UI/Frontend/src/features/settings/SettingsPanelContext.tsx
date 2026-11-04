import { createContext, useContext } from "solid-js";
import type { JSX } from "solid-js";
import type { SettingsPanelBindings } from "./settingsPanelBindings.ts";

/* Provide one app-level settings binding so all page panels share the same draft. */
const SettingsPanelContext = createContext<SettingsPanelBindings>();

export function SettingsPanelProvider(props: { readonly bindings: SettingsPanelBindings; readonly children: JSX.Element }) {
  return <SettingsPanelContext.Provider value={props.bindings}>{props.children}</SettingsPanelContext.Provider>;
}

export function useSettingsPanelBindings(): SettingsPanelBindings {
  const bindings = useContext(SettingsPanelContext);
  if (!bindings) throw new Error("useSettingsPanelBindings must be used inside SettingsPanelProvider.");
  return bindings;
}
