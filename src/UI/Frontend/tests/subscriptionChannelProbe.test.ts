import assert from "node:assert/strict";
import { assertSubscriptionChannelReleased, subscriptionChannelConcurrency, subscriptionPathMaximum } from "./browser/subscriptionChannelProbe.js";

const start = (requestId: string, timestamp: number) => ({ requestId, timestamp, delta: 1 });
const end = (requestId: string, timestamp: number) => ({ requestId, timestamp, delta: -1 });
assert.deepEqual(subscriptionChannelConcurrency([
  start("old", 1), start("new", 3), end("old", 2)
]), { inFlight: 1, maxInFlight: 1 }, "late close delivery is not physical overlap");
assert.deepEqual(subscriptionChannelConcurrency([
  start("old", 1), start("new", 2), end("old", 3)
]), { inFlight: 1, maxInFlight: 2 }, "a real overlap must fail the browser assertion");
assert.deepEqual(subscriptionChannelConcurrency([
  start("old", 1), end("old", 2)
]), { inFlight: 0, maxInFlight: 1 }, "a closed channel cannot pass as active");
console.log("Browser subscription probe distinguishes late event delivery from transport overlap.");

const localPath = "/api/local-system/status/subscribe";
assert.equal(subscriptionPathMaximum([
  ["/api/operations/subscribe", `${localPath}?intervalMs=1000`],
  [`${localPath}?intervalMs=1000`]
], localPath), 1, "a replacement request does not duplicate a selector within one channel");
assert.equal(subscriptionPathMaximum([[localPath, localPath]], localPath), 2,
  "duplicate logical subscriptions within a single physical channel must exceed one");
assert.equal(subscriptionPathMaximum([[`${localPath}?intervalMs=1000`, `${localPath}?intervalMs=2000`]], localPath), 2,
  "different intervals must not hide duplicate local-system subscriptions");
assert.equal(subscriptionPathMaximum([["/api/operations/subscribe"]], localPath), 0,
  "a missing selector cannot pass as one active logical subscription");

assert.deepEqual(assertSubscriptionChannelReleased([start("old", 1), end("old", 2)], 1.5, 10000),
  { releaseMs: 500 }, "late delivery to the runner does not change the browser release time");
assert.deepEqual(assertSubscriptionChannelReleased([start("old", 1), end("old", 11)], 1, 10000),
  { releaseMs: 10000 }, "the ten-second boundary is inclusive");
assert.throws(() => assertSubscriptionChannelReleased([
  start("old", 1), end("old", 2), start("access-requests", 2.001)
], 1.5, 10000), /still active/, "a replacement stream cannot pass through the gap between streams");
assert.throws(() => assertSubscriptionChannelReleased([
  start("old", 1), end("old", 11.001)
], 1, 10000), /release took/, "a release after ten seconds must fail even if delivered before the assertion");
