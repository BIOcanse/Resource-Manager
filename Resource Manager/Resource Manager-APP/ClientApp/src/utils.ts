import type { ManagedComponent, MigrationRoots, SoftwareRecord } from "./types";
import { getHostMessageTransport } from "./host/hostMessageTransport.ts";
import { uiText } from "./text.ts";

export type ShellHostMessage =
  | "host.visibility:visible"
  | "host.visibility:hidden"
  | "host.window-resize:start"
  | "host.window-resize:end";

export function postShellMessage(message: string) {
  getHostMessageTransport()?.postMessage(message);
}

export function pickShellFolder(title: string, initialPath?: string): Promise<string | null> {
  const transport = getHostMessageTransport();
  if (!transport?.canSubscribeMessages) {
    return Promise.reject(new Error(uiText.shellBridge.desktopOnly));
  }

  const requestId = crypto.randomUUID();
  return new Promise<string | null>((resolve, reject) => {
    let unsubscribe: (() => void) | null = null;
    const timeout = window.setTimeout(() => {
      unsubscribe?.();
      reject(new Error(uiText.shellBridge.folderPickerTimeout));
    }, 120_000);
    const handler = (message: unknown) => {
      if (!isFolderPickerResponse(message, requestId)) {
        return;
      }

      window.clearTimeout(timeout);
      unsubscribe?.();
      resolve(typeof message.path === "string" ? message.path : null);
    };
    unsubscribe = transport.subscribeMessages(handler);
    if (!unsubscribe) {
      window.clearTimeout(timeout);
      reject(new Error(uiText.shellBridge.folderPickerUnavailable));
      return;
    }
    transport.postMessage({ type: "shell.pickFolder", requestId, title, initialPath });
  });
}

function isFolderPickerResponse(value: unknown, requestId: string): value is { type: string; requestId: string; path?: unknown } {
  if (!value || typeof value !== "object") {
    return false;
  }

  const candidate = value as { type?: unknown; requestId?: unknown };
  return candidate.type === "shell.pickFolder.result" && candidate.requestId === requestId;
}

export function subscribeShellHostMessages(listener: (message: ShellHostMessage) => void) {
  const transport = getHostMessageTransport();
  if (!transport?.canSubscribeMessages) {
    return () => undefined;
  }

  const unsubscribe = transport.subscribeMessages((message) => {
    if (typeof message === "string" && message.startsWith("host.")) {
      listener(message as ShellHostMessage);
    }
  });
  return unsubscribe ?? (() => undefined);
}

let windowResizeFallbackTimer = 0;

export function beginShellWindowResize() {
  if (document.body.dataset.windowResizing !== "yes") {
    document.body.dataset.windowResizing = "yes";
  }
  window.clearTimeout(windowResizeFallbackTimer);
  windowResizeFallbackTimer = window.setTimeout(endShellWindowResize, 300);
}

export function endShellWindowResize() {
  window.clearTimeout(windowResizeFallbackTimer);
  windowResizeFallbackTimer = 0;
  if (document.body.dataset.windowResizing === "yes") {
    delete document.body.dataset.windowResizing;
  }
}

export function textOrEmpty(value: unknown): string {
  return typeof value === "string" ? value.trim() : value == null ? "" : String(value).trim();
}

export function uniqueTextValues(values: unknown[]): string[] {
  const seen = new Set<string>();
  const result: string[] = [];
  for (const value of values) {
    const text = textOrEmpty(value);
    const key = text.toLowerCase();
    if (text && !seen.has(key)) {
      seen.add(key);
      result.push(text);
    }
  }

  return result;
}

export function formatBoolean(value: unknown) {
  return value ? uiText.shellBridge.yes : uiText.shellBridge.no;
}

export function formatPercent(value: unknown) {
  const numeric = Number(value);
  return Number.isFinite(numeric) ? `${numeric.toFixed(1)}%` : "0.0%";
}

export function isHttpUrl(value: unknown) {
  return /^https?:\/\//i.test(textOrEmpty(value));
}

export function pathLooksUsable(value: unknown) {
  const text = textOrEmpty(value);
  return /^[a-zA-Z]:[\\/]/.test(text) || /^\\\\/.test(text);
}

export function normalizeSoftwareKind(software: SoftwareRecord): "Adapted" | "Controlled" | "Game" | "HighPerformance" | "Other" {
  // kind 与 displayKind 现在都是标识，直接比标识，不再认措辞。
  const kind = software.kind ?? "";
  const displayKind = software.displayKind ?? "";
  for (const candidate of ["Adapted", "Controlled", "Game", "HighPerformance"] as const) {
    if (kind === candidate || displayKind === candidate) {
      return candidate;
    }
  }

  return "Other";
}

export function normalizeName(value: unknown) {
  return textOrEmpty(value).toLowerCase();
}

export function isComponentInstalled(component: ManagedComponent) {
  const state = textOrEmpty(component.state);
  return Boolean(component.installed)
    || component.providerActive === true
    || state === "Installed"
    || state === "Active"
    || state === "InstalledUnverified";
}

export function isSameOrUnderPath(path: string, root: string) {
  const candidate = normalizePathForCompare(path);
  const normalizedRoot = normalizePathForCompare(root);
  return candidate === normalizedRoot || candidate.startsWith(`${normalizedRoot}\\`);
}

export function normalizePathForCompare(path: string) {
  return textOrEmpty(path).replaceAll("/", "\\").replace(/[\\]+$/g, "").toLowerCase();
}

export function getRootMigrationPaths(paths: string[], roots: MigrationRoots | null) {
  return uniqueTextValues(paths)
    .filter(pathLooksUsable)
    .filter((path) => !isManagedResourceManagerPath(path, roots));
}

export function isManagedResourceManagerPath(path: string, roots: MigrationRoots | null) {
  if (!roots) {
    return false;
  }

  const managedRoots = [
    roots.userDataRoot,
    roots.miscRoot,
    roots.dependencyRoot,
    roots.managedSoftwareRoot
  ].filter(Boolean) as string[];

  return managedRoots.some((root) => isSameOrUnderPath(path, root));
}

export function formatCapabilityFlags(flags: Array<[unknown, string]>) {
  const labels = flags.filter(([enabled]) => Boolean(enabled)).map(([, label]) => label);
  return labels.length ? labels.join(" / ") : uiText.shellBridge.none;
}
