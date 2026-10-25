// A test adapter for the native mutation boundary. Installed after browser startup
// so the existing standalone preferences/session owners remain unchanged.
export async function installElevatedBackendBridge(page) {
  await page.evaluate(() => {
    const listeners = new Set();
    window.__elevatedBackendRequests = [];
    window.unboundWebView = {
      addEventListener(type, listener) { if (type === "message") listeners.add(listener); },
      removeEventListener(type, listener) { if (type === "message") listeners.delete(listener); },
      postMessage(command) {
        if (command.type !== "backend.elevatedRequest") return;
        window.__elevatedBackendRequests.push(command);
        void fetch("/__test/elevated-backend-request", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(command) })
          .then(response => response.json())
          .then(result => { for (const listener of [...listeners]) listener({ data: result }); });
      }
    };
  });
}
