export const sharedBrowserRuntimeComponentId = "shared-webview2-runtime";

export interface BrowserRuntimeEntry {
  id: string;
  name: string;
  kind: "WebView2Runtime" | "ChromiumBrowser" | "GeckoBrowser";
  version: string;
  executablePath: string;
  runtimeDirectory: string;
  source: string;
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
