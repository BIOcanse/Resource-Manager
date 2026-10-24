import assert from "node:assert/strict";

// The mock's response.close can arrive after the next POST under load. Measure
// physical overlap at Chromium's transport boundary, using event timestamps so
// delayed delivery to the test runner cannot create an apparent overlap either.
export async function observeSubscriptionChannel(page) {
  const session = await page.context().newCDPSession(page);
  const requests = new Set();
  const events = [];
  session.on("Network.requestWillBeSent", event => {
    if (new URL(event.request.url).pathname !== "/api/subscriptions/stream") return;
    requests.add(event.requestId);
    events.push({ requestId: event.requestId, timestamp: event.timestamp, delta: 1 });
  });
  const finish = event => {
    if (!requests.delete(event.requestId)) return;
    events.push({ requestId: event.requestId, timestamp: event.timestamp, delta: -1 });
  };
  session.on("Network.loadingFailed", finish);
  session.on("Network.loadingFinished", finish);
  await session.send("Network.enable");
  return {
    async assertSinglePhysicalChannel() {
      // Flush the CDP session before consuming its already published events.
      await session.send("Runtime.evaluate", { expression: "0" });
      const value = subscriptionChannelConcurrency(events);
      assert.equal(value.inFlight, 1, `No active browser callback channel: ${JSON.stringify(events)}`);
      assert.equal(value.maxInFlight, 1, `Browser callback channels overlapped: ${JSON.stringify(events)}`);
      return value;
    }
  };
}

export function subscriptionChannelConcurrency(events) {
  let inFlight = 0;
  let maxInFlight = 0;
  for (const event of [...events].sort((a, b) => a.timestamp - b.timestamp || a.delta - b.delta)) {
    inFlight += event.delta;
    maxInFlight = Math.max(maxInFlight, inFlight);
  }
  return { inFlight, maxInFlight };
}
