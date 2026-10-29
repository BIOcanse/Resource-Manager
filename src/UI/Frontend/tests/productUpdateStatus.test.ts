import assert from "node:assert/strict";
import test from "node:test";
import { formatUtcTime, parseUtcTime, productUpdateStatusDecoder } from "../src/data/productUpdateStatus.ts";
import { formatElapsedDuration } from "../src/presentation/elapsedDuration.ts";

test("status times are UTC only and are shown as UTC", () => {
  assert.ok(parseUtcTime("2026-10-26T08:00:00Z"));
  assert.ok(parseUtcTime("2026-10-26T08:00:00.1234567+00:00"));
  assert.equal(parseUtcTime("2026-10-26T08:00:00"), null, "no offset");
  assert.equal(parseUtcTime("2026-10-26T16:00:00+08:00"), null, "not UTC");
  assert.equal(formatUtcTime("2026-10-26T08:00:59.9Z"), "2026-10-26 08:00 UTC");
  assert.equal(formatElapsedDuration(90249), "25:04:09");
  const decoded = productUpdateStatusDecoder.decode({ stage: "idle", availableVersion: "1.1.0",
    nextCheckAt: "2026-10-26T08:00:00Z", lastCheckedAt: null, checkResult: "available", automaticCheckStatus: "waitingForInternet", onlineTimeSinceLastCheckSeconds: 30,
    updatedAt: "2026-10-25T19:00:00+00:00", workspace: null, error: null, checkedInstalledVersion: "0.2.4", incompatibleVersions: [] });
  assert.equal(decoded.availableVersion, "1.1.0");
  assert.equal(decoded.nextCheckAt, "2026-10-26T08:00:00Z");
  assert.equal(decoded.onlineTimeSinceLastCheckSeconds, 30);
  assert.equal(decoded.automaticCheckStatus, "waitingForInternet");
  assert.equal(productUpdateStatusDecoder.decode({ stage: "idle" }).availableVersion, null);
  assert.throws(() => productUpdateStatusDecoder.decode({ stage: "idle", lastCheckedAt: "2026-10-26T08:00:00" }));
  assert.throws(() => productUpdateStatusDecoder.decode({ stage: "idle", nextCheckAt: "2026-10-26T16:00:00+08:00" }));
  assert.throws(() => productUpdateStatusDecoder.decode({ stage: "idle", onlineTimeSinceLastCheckSeconds: -1 }));
  assert.throws(() => productUpdateStatusDecoder.decode({ invalid: true }));
  assert.throws(() => productUpdateStatusDecoder.decode({ stage: "idle", automaticCheckStatus: "offline" }));
  assert.throws(() => productUpdateStatusDecoder.decode({ stage: "idle", updatedAt: "2026-10-25T19:00:00" }));
});
