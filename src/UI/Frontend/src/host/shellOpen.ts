import { getHostMessageTransport, type HostMessageTransport } from "./hostMessageTransport.ts";
import { ResourceManager } from "../preferences/generated/frontendPoints.values.ts";

/** The window process opens user-requested GitHub HTTPS URLs or reveals existing absolute file paths. Without a native host, URLs open in a browser window; file reveal returns unavailable. */
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
