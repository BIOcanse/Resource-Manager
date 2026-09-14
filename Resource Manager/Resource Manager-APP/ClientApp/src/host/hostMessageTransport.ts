export interface HostMessageTransport {
  readonly canSubscribeMessages: boolean;
  postMessage(message: unknown): void;
  subscribeMessages(listener: (message: unknown) => void): (() => void) | null;
}

interface WebViewMessageEvent {
  data: unknown;
}

interface WebViewMessageSource {
  postMessage(message: unknown): void;
  addEventListener?: (type: "message", listener: (event: WebViewMessageEvent) => void) => void;
  removeEventListener?: (type: "message", listener: (event: WebViewMessageEvent) => void) => void;
}

interface WebViewChrome {
  webview?: WebViewMessageSource;
}

interface HostMessageWindow extends Window {
  unboundWebView?: WebViewMessageSource;
  chrome?: WebViewChrome;
}

function isMessageSource(value: WebViewMessageSource | undefined): value is WebViewMessageSource {
  return typeof value?.postMessage === "function";
}

export function getHostMessageTransport(): HostMessageTransport | null {
  if (typeof window === "undefined") {
    return null;
  }

  const hostWindow = window as HostMessageWindow;
  const source = isMessageSource(hostWindow.unboundWebView)
    ? hostWindow.unboundWebView
    : hostWindow.chrome?.webview;
  if (!isMessageSource(source)) {
    return null;
  }

  const canSubscribeMessages =
    typeof source.addEventListener === "function"
    && typeof source.removeEventListener === "function";
  return {
    canSubscribeMessages,
    postMessage: (message) => source.postMessage(message),
    subscribeMessages: (listener) => {
      if (!canSubscribeMessages) {
        return null;
      }

      const handler = (event: WebViewMessageEvent) => listener(event.data);
      source.addEventListener!("message", handler);
      return () => source.removeEventListener!("message", handler);
    }
  };
}
