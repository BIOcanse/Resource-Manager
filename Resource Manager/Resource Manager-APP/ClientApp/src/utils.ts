import type { ManagedComponent, MigrationRoots, SoftwareRecord } from "./types";
import { getHostMessageTransport } from "./host/hostMessageTransport.ts";

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
    return Promise.reject(new Error("此功能仅在 Resource Manager 桌面应用中可用。"));
  }

  const requestId = crypto.randomUUID();
  return new Promise<string | null>((resolve, reject) => {
    let unsubscribe: (() => void) | null = null;
    const timeout = window.setTimeout(() => {
      unsubscribe?.();
      reject(new Error("文件夹选择窗口长时间没有响应，请重试。"));
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
      reject(new Error("当前应用窗口无法打开文件夹选择器，请重新打开 Resource Manager。"));
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
  return value ? "是" : "否";
}

export function formatPercent(value: unknown) {
  const numeric = Number(value);
  return Number.isFinite(numeric) ? `${numeric.toFixed(1)}%` : "0.0%";
}

export function formatBytes(value: unknown) {
  const units = ["B", "KB", "MB", "GB", "TB"];
  let size = Math.max(0, Number(value) || 0);
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit++;
  }

  const digits = unit === 0 ? 0 : size >= 10 ? 1 : 2;
  return `${size.toFixed(digits)} ${units[unit]}`;
}

export function formatSignedBytes(value: unknown) {
  const numeric = Number(value) || 0;
  const sign = numeric > 0 ? "+" : numeric < 0 ? "-" : "";
  return `${sign}${formatBytes(Math.abs(numeric))}`;
}

export function isHttpUrl(value: unknown) {
  return /^https?:\/\//i.test(textOrEmpty(value));
}

export function pathLooksUsable(value: unknown) {
  const text = textOrEmpty(value);
  return /^[a-zA-Z]:[\\/]/.test(text) || /^\\\\/.test(text);
}

export function normalizeSoftwareKind(software: SoftwareRecord): "Adapted" | "Controlled" | "Game" | "HighPerformance" | "Other" {
  const kind = software.kind ?? "";
  const displayKind = software.displayKind ?? "";
  if (kind === "Adapted" || displayKind === "适配软件") {
    return "Adapted";
  }
  if (kind === "Controlled" || displayKind === "受控软件") {
    return "Controlled";
  }
  if (kind === "Game" || displayKind === "游戏") {
    return "Game";
  }
  if (kind === "HighPerformance" || displayKind === "高性能软件") {
    return "HighPerformance";
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
  return labels.length ? labels.join(" / ") : "无";
}
