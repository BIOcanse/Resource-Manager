import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { getHostMessageTransport } from "../src/host/hostMessageTransport.ts";

const utilsSource = readFileSync(new URL("../src/utils.ts", import.meta.url), "utf8");
const providerSource = readFileSync(
  new URL("../src/host/hostMessageTransport.ts", import.meta.url),
  "utf8"
);
assert.doesNotMatch(utilsSource, /chrome\?*\.webview|window\.chrome/);
assert.match(providerSource, /chrome\?\.webview/);
assert.match(providerSource, /unboundWebView/);

const globalWithWindow = globalThis as unknown as { window?: unknown };
const originalWindow = globalWithWindow.window;

try {
  delete globalWithWindow.window;
  assert.equal(getHostMessageTransport(), null);

  const sent: unknown[] = [];
  let subscribed: ((event: { data: unknown }) => void) | null = null;
  let removed: ((event: { data: unknown }) => void) | null = null;
  globalWithWindow.window = {
    chrome: {
      webview: {
        postMessage: (message: unknown) => sent.push(message),
        addEventListener: (_type: "message", listener: (event: { data: unknown }) => void) => {
          subscribed = listener;
        },
        removeEventListener: (_type: "message", listener: (event: { data: unknown }) => void) => {
          removed = listener;
        }
      }
    }
  };

  const transport = getHostMessageTransport();
  assert.ok(transport);
  assert.equal(transport.canSubscribeMessages, true);
  transport.postMessage({ type: "test" });
  assert.deepEqual(sent, [{ type: "test" }]);
  const received: unknown[] = [];
  const unsubscribe = transport.subscribeMessages((message) => received.push(message));
  assert.ok(unsubscribe);
  assert.ok(subscribed);
  subscribed!({ data: "host.visibility:visible" });
  assert.deepEqual(received, ["host.visibility:visible"]);
  unsubscribe!();
  assert.equal(removed, subscribed);

  globalWithWindow.window = {
    chrome: { webview: { postMessage: (message: unknown) => sent.push(message) } }
  };
  const sendOnlyTransport = getHostMessageTransport();
  assert.ok(sendOnlyTransport);
  assert.equal(sendOnlyTransport.canSubscribeMessages, false);
  assert.equal(sendOnlyTransport.subscribeMessages(() => undefined), null);

  const unboundSent: unknown[] = [];
  const webViewSent: unknown[] = [];
  let unboundSubscribed: ((event: { data: unknown }) => void) | null = null;
  let unboundRemoved: ((event: { data: unknown }) => void) | null = null;
  const unboundSource = {
    postMessage: (message: unknown) => unboundSent.push(message),
    addEventListener: (_type: "message", listener: (event: { data: unknown }) => void) => {
      unboundSubscribed = listener;
    },
    removeEventListener: (_type: "message", listener: (event: { data: unknown }) => void) => {
      unboundRemoved = listener;
    }
  };
  globalWithWindow.window = {
    unboundWebView: unboundSource,
    chrome: {
      webview: { postMessage: (message: unknown) => webViewSent.push(message) }
    }
  };

  const unboundTransport = getHostMessageTransport();
  assert.ok(unboundTransport);
  assert.equal(unboundTransport.canSubscribeMessages, true);
  unboundTransport.postMessage("unbound");
  assert.deepEqual(unboundSent, ["unbound"]);
  assert.deepEqual(webViewSent, []);
  const unboundReceived: unknown[] = [];
  const unsubscribeUnbound = unboundTransport.subscribeMessages(
    (message) => unboundReceived.push(message)
  );
  assert.ok(unsubscribeUnbound);
  assert.ok(unboundSubscribed);
  unboundSubscribed!({ data: { type: "host" } });
  assert.deepEqual(unboundReceived, [{ type: "host" }]);

  globalWithWindow.window = {
    unboundWebView: {
      postMessage: (message: unknown) => unboundSent.push(["replacement", message])
    }
  };
  unboundTransport.postMessage("captured");
  unsubscribeUnbound!();
  assert.deepEqual(unboundSent, ["unbound", "captured"]);
  assert.equal(unboundRemoved, unboundSubscribed);

  globalWithWindow.window = {
    unboundWebView: {},
    chrome: {
      webview: { postMessage: (message: unknown) => webViewSent.push(message) }
    }
  };
  const invalidUnboundTransport = getHostMessageTransport();
  assert.ok(invalidUnboundTransport);
  invalidUnboundTransport.postMessage("webview-fallback");
  assert.deepEqual(webViewSent, ["webview-fallback"]);

  const sendOnlyUnbound: unknown[] = [];
  globalWithWindow.window = {
    unboundWebView: {
      postMessage: (message: unknown) => sendOnlyUnbound.push(message)
    }
  };
  const sendOnlyUnboundTransport = getHostMessageTransport();
  assert.ok(sendOnlyUnboundTransport);
  assert.equal(sendOnlyUnboundTransport.canSubscribeMessages, false);
  sendOnlyUnboundTransport.postMessage("send-only");
  assert.deepEqual(sendOnlyUnbound, ["send-only"]);
  assert.equal(sendOnlyUnboundTransport.subscribeMessages(() => undefined), null);
} finally {
  if (originalWindow === undefined) {
    delete globalWithWindow.window;
  } else {
    globalWithWindow.window = originalWindow;
  }
}
