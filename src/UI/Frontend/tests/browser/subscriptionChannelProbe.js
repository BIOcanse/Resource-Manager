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
    // action returns performance.now() (ms) at the visibility change. NavigationStart
    // places it on the same Chromium monotonic clock as the network events.
    async assertReleasedAfter(action, timeoutMs = 10000) {
      await session.send("Performance.enable");
      const before = await session.send("Performance.getMetrics");
      const navigationStart = before.metrics.find(metric => metric.name === "NavigationStart")?.value;
      assert.ok(Number.isFinite(navigationStart), "Chromium did not provide NavigationStart");
      assert.ok(events.some(event => event.delta === 1), "No browser subscription was observed before hiding");
      const hiddenAt = navigationStart + (await action()) / 1000;
      while (true) {
        await session.send("Runtime.evaluate", { expression: "0" });
        const { metrics } = await session.send("Performance.getMetrics");
        const now = metrics.find(metric => metric.name === "Timestamp")?.value;
        assert.ok(Number.isFinite(now), "Chromium did not provide its monotonic timestamp");
        if (requests.size === 0 || now >= hiddenAt + timeoutMs / 1000) break;
        await new Promise(resolve => setTimeout(resolve, 50));
      }
      return assertSubscriptionChannelReleased(events, hiddenAt, timeoutMs);
    },
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

export function subscriptionPathMaximum(subscriptionSets, pathname) {
  let maximum = 0;
  for (const selectors of subscriptionSets) {
    let count = 0;
    for (const selector of selectors) {
      if (new URL(selector, "http://127.0.0.1").pathname === pathname) count++;
    }
    maximum = Math.max(maximum, count);
  }
  return maximum;
}

export function assertSubscriptionChannelReleased(events, hiddenAt, timeoutMs) {
  assert.equal(subscriptionChannelConcurrency(events).inFlight, 0,
    `Browser subscription channel is still active after hiding: ${JSON.stringify(events)}`);
  const finishedAt = Math.max(...events.filter(event => event.delta === -1).map(event => event.timestamp));
  assert.ok(Number.isFinite(finishedAt), "No browser subscription ended");
  const releaseMs = Math.max(0, (finishedAt - hiddenAt) * 1000);
  assert.ok(releaseMs <= timeoutMs,
    `Browser subscription release took ${releaseMs} ms (limit ${timeoutMs} ms): ${JSON.stringify(events)}`);
  return { releaseMs };
}
