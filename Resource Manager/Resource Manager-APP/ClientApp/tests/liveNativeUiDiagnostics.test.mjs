import assert from "node:assert/strict";
import { isExpectedNavigationCancellation } from "./browser/liveNativeUiDiagnostics.mjs";

const context = {
  expectedOrigin: "http://127.0.0.1:9321",
  transitionTargetPageId: "components",
  transitionDeadlineMilliseconds: 2_000,
  observedAtMilliseconds: 1_500
};
const expected = {
  method: "GET",
  url: "http://127.0.0.1:9321/api/resource-monitor/snapshot?mode=software",
  failure: "net::ERR_ABORTED"
};

assert.equal(isExpectedNavigationCancellation(expected, context), true);
assert.equal(isExpectedNavigationCancellation(
  expected,
  { ...context, observedAtMilliseconds: 2_001 }), false);
assert.equal(isExpectedNavigationCancellation(
  { ...expected, method: "POST" },
  context), false);
assert.equal(isExpectedNavigationCancellation(
  { ...expected, url: "http://127.0.0.1:9322/api/resource-monitor/snapshot" },
  context), false);
assert.equal(isExpectedNavigationCancellation(
  { ...expected, url: "http://127.0.0.1:9321/frontend-build.json" },
  context), false);
assert.equal(isExpectedNavigationCancellation(
  { ...expected, failure: "net::ERR_CONNECTION_RESET" },
  context), false);
assert.equal(isExpectedNavigationCancellation(
  expected,
  { ...context, transitionTargetPageId: null }), false);

console.log("live Native UI diagnostics tests passed");
