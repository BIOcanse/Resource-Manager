import { getHostMessageTransport } from "../../host/hostMessageTransport.ts";

export type LoginStartupTransport = ReturnType<typeof getHostMessageTransport>;

export function requestLoginStartup(enabled?: boolean, transport: LoginStartupTransport = getHostMessageTransport()): Promise<boolean> {
  if (!transport?.canSubscribeMessages) return Promise.reject(new Error("unavailable"));
  return new Promise((resolve, reject) => {
    const requestId = crypto.randomUUID();
    let unsubscribe: (() => void) | null = null;
    const timer = setTimeout(() => { unsubscribe?.(); reject(new Error("unavailable")); }, 10000);
    unsubscribe = transport.subscribeMessages((message) => {
      if (!message || typeof message !== "object") return;
      const response = message as { type?: string; requestId?: string; enabled?: unknown; error?: string | null };
      if (response.type !== "shell.loginStartup.result" || response.requestId !== requestId) return;
      clearTimeout(timer);
      unsubscribe?.();
      if (response.error || typeof response.enabled !== "boolean") reject(new Error(response.error ?? "unavailable"));
      else resolve(response.enabled);
    });
    if (!unsubscribe) { clearTimeout(timer); reject(new Error("unavailable")); return; }
    try {
      transport.postMessage({ type: enabled === undefined ? "shell.loginStartup.get" : "shell.loginStartup.set",
        requestId, ...(enabled === undefined ? {} : { enabled }) });
    } catch (error) { clearTimeout(timer); unsubscribe(); reject(error); }
  });
}
