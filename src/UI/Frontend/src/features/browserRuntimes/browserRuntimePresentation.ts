import type { BrowserRuntimeEntry } from "./browserRuntimeTypes.ts";
import { uiText } from "../../text.ts";

export function browserRuntimeKindLabel(kind: BrowserRuntimeEntry["kind"]) {
  return ({
    web_view2_runtime: uiText.browserRuntime.kind.webView2Runtime,
    chromium_browser: uiText.browserRuntime.kind.chromiumBrowser,
    gecko_browser: uiText.browserRuntime.kind.geckoBrowser
  } as const)[kind];
}

export function browserRuntimeSourceLabel(source: string) {
  return ({
    system_machine: uiText.browserRuntime.source.systemMachine,
    system_user: uiText.browserRuntime.source.systemUser,
    managed: uiText.browserRuntime.source.managed,
    browser_registration: uiText.browserRuntime.source.browserRegistration,
    known_install: uiText.browserRuntime.source.knownInstall
  } as Record<string, string>)[source] ?? uiText.browserRuntime.source.local;
}
