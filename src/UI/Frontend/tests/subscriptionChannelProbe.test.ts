import assert from "node:assert/strict";
import { subscriptionChannelConcurrency } from "./browser/subscriptionChannelProbe.js";

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
