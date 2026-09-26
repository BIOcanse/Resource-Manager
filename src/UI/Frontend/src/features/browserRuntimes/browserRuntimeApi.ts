import { getJson } from "../../api";
import type { BrowserRuntimeSnapshot } from "./browserRuntimeTypes";

export function getBrowserRuntimeSnapshot(forceRefresh = false) {
  const query = forceRefresh ? "?refresh=true" : "";
  return getJson<BrowserRuntimeSnapshot>(`/api/browser-runtimes${query}`);
}
