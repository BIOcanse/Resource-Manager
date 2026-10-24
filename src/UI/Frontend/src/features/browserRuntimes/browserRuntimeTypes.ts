export const sharedBrowserRuntimeComponentId = "shared-webview2-runtime";

export interface BrowserRuntimeEntry {
  id: string;
  name: string;
  kind: "web_view2_runtime" | "chromium_browser" | "gecko_browser";
  version: string;
  executablePath: string;
  runtimeDirectory: string;
  source: "system_machine" | "system_user" | "managed" | "browser_registration" | "known_install";
  nativeWebView2Compatible: boolean;
  selected: boolean;
}

export interface BrowserRuntimeSnapshot {
  sharedRuntime: BrowserRuntimeEntry | null;
  browserFallback: BrowserRuntimeEntry | null;
  candidates: BrowserRuntimeEntry[];
  installComponentId: string;
  capturedAt: string;
}
