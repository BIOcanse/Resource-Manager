import { getHostMessageTransport, type HostMessageTransport } from "./hostMessageTransport.ts";
import { ResourceManager } from "../preferences/generated/frontendPoints.values.ts";

/**
 * 用户点击时由窗口进程打开地址或在资源管理器里选中文件（docs/crash_dumps_2026-10-31.md：后端从不打开任何东西）。
 * 窗口进程只接受 https://github.com/ 下的地址和本机上存在的文件的绝对路径，回 shell.open.result。
 * 没有原生宿主（浏览器里打开的前端）时：地址交给浏览器的新窗口打开，选中文件做不到，回 unavailable。
 */
export type ShellOpenRequest =
  | { readonly kind: "url"; readonly url: string }
  | { readonly kind: "file"; readonly path: string };

export class ShellOpenError extends Error {
  readonly reason: string;
  constructor(reason: string) {
    super(reason);
    this.name = "ShellOpenError";
    this.reason = reason;
  }
}

export function requestShellOpen(
  request: ShellOpenRequest,
  transport: HostMessageTransport | null = getHostMessageTransport(),
  openWindow: (url: string) => void = (url) => { window.open(url, "_blank", "noopener"); }
): Promise<void> {
  if (!transport?.canSubscribeMessages) {
    if (request.kind === "url") {
      openWindow(request.url);
      return Promise.resolve();
    }
    return Promise.reject(new ShellOpenError("unavailable"));
  }
  return new Promise((resolve, reject) => {
    const requestId = crypto.randomUUID();
    let unsubscribe: (() => void) | null = null;
    const timer = setTimeout(() => { unsubscribe?.(); reject(new ShellOpenError("unavailable")); }, ResourceManager.FrontendPoints.ShellOpenTimeoutMs);
    unsubscribe = transport.subscribeMessages((message) => {
      if (!message || typeof message !== "object") return;
      const response = message as { type?: unknown; requestId?: unknown; opened?: unknown; error?: unknown };
      if (response.type !== "shell.open.result" || response.requestId !== requestId) return;
      clearTimeout(timer);
      unsubscribe?.();
      if (response.opened === true) resolve();
      else reject(new ShellOpenError(typeof response.error === "string" && response.error ? response.error : "failed"));
    });
    if (!unsubscribe) { clearTimeout(timer); reject(new ShellOpenError("unavailable")); return; }
    try {
      transport.postMessage(request.kind === "url"
        ? { type: "shell.openUrl", requestId, url: request.url }
        : { type: "shell.revealFile", requestId, path: request.path });
    } catch (error) {
      clearTimeout(timer);
      unsubscribe();
      reject(error);
    }
  });
}
