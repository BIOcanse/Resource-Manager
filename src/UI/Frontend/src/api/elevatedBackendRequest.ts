import { getHostMessageTransport } from "../host/hostMessageTransport.ts";
import { ApiRequestError } from "./httpTransport.ts";
import { uiText } from "../text.ts";

/** Explicit machine mutation through the native host's pinned, elevated backend client. */
export async function putElevatedBackendJson<T>(path: string, body: unknown, fallbackMessage: string): Promise<T> {
  const transport = getHostMessageTransport();
  if (!transport?.canSubscribeMessages) {
    throw new ApiRequestError(uiText.shellBridge.desktopOnly, { kind: "invalid-response", retryable: false });
  }
  const serialized = JSON.stringify(body);
  if (typeof serialized !== "string" || new TextEncoder().encode(serialized).length > 128 * 1024) {
    throw new ApiRequestError(fallbackMessage, { kind: "invalid-response", retryable: false });
  }
  const requestId = crypto.randomUUID();
  return new Promise<T>((resolve, reject) => {
    let unsubscribe: (() => void) | null = null;
    const timer = globalThis.setTimeout(() => {
      unsubscribe?.();
      reject(new ApiRequestError(uiText.misc.transportTimeout, { kind: "timeout", retryable: false }));
    }, 120_000);
    unsubscribe = transport.subscribeMessages(message => {
      if (!message || typeof message !== "object") return;
      const response = message as { type?: unknown; requestId?: unknown; statusCode?: unknown; body?: unknown; error?: unknown };
      if (response.type !== "backend.elevatedRequest.result" || response.requestId !== requestId) return;
      globalThis.clearTimeout(timer);
      unsubscribe?.();
      if (response.error === "canceled") {
        reject(new ApiRequestError(uiText.misc.transportCanceled, { kind: "aborted", retryable: false }));
        return;
      }
      if (response.error != null || !Number.isInteger(response.statusCode)
        || (response.statusCode as number) < 100 || (response.statusCode as number) > 599
        || typeof response.body !== "string" || new TextEncoder().encode(response.body).length > 256 * 1024) {
        reject(new ApiRequestError(fallbackMessage, { kind: "invalid-response", retryable: false }));
        return;
      }
      const status = response.statusCode as number;
      let payload: unknown = null;
      let invalidJson = false;
      if (response.body) {
        try { payload = JSON.parse(response.body); }
        catch { invalidJson = true; }
      }
      if (status < 200 || status >= 300) {
        reject(new ApiRequestError(fallbackMessage, { kind: "http", status, payload: invalidJson ? null : payload, retryable: false }));
        return;
      }
      if (!response.body || invalidJson) {
        reject(new ApiRequestError(fallbackMessage, { kind: "invalid-response", status, retryable: false }));
        return;
      }
      resolve(payload as T);
    });
    if (!unsubscribe) {
      globalThis.clearTimeout(timer);
      reject(new ApiRequestError(uiText.shellBridge.desktopOnly, { kind: "invalid-response", retryable: false }));
      return;
    }
    try { transport.postMessage({ type: "backend.elevatedRequest", requestId, method: "PUT", path, body: serialized }); }
    catch {
      globalThis.clearTimeout(timer);
      unsubscribe();
      reject(new ApiRequestError(fallbackMessage, { kind: "network", retryable: false }));
    }
  });
}
