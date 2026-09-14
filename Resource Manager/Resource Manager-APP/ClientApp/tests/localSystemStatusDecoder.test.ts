import assert from "node:assert/strict";
import {
  localSystemStatusDecoder
} from "../src/data/localSystem/localSystemStatusDecoder.ts";
import {
  ResponseDecodeError
} from "../src/frontendRuntime/request/ResponseDecoder.ts";

const source = {
  capturedAt: "2026-08-22T10:20:30.123-05:00",
  bootedAt: "2026-08-20T01:02:03.000-05:00",
  uptimeSeconds: 206_307,
  futureField: "ignored"
};
const decoded = localSystemStatusDecoder.decode(source);
assert.deepEqual(decoded, {
  capturedAt: source.capturedAt,
  bootedAt: source.bootedAt,
  uptimeSeconds: source.uptimeSeconds
});
assert.notEqual(decoded, source);

const invalidCases: Array<[unknown, string]> = [
  [null, "$"],
  [[], "$"],
  [{ ...source, capturedAt: undefined }, "$.capturedAt"],
  [{ ...source, capturedAt: "not-a-date" }, "$.capturedAt"],
  [{ ...source, bootedAt: "" }, "$.bootedAt"],
  [{ ...source, uptimeSeconds: "12" }, "$.uptimeSeconds"],
  [{ ...source, uptimeSeconds: 1.5 }, "$.uptimeSeconds"],
  [{ ...source, uptimeSeconds: -1 }, "$.uptimeSeconds"],
  [{
    ...source,
    capturedAt: "2026-08-20T01:02:03.000Z",
    bootedAt: "2026-08-21T01:02:03.000Z"
  }, "$.bootedAt"]
];

for (const [value, expectedPath] of invalidCases) {
  assert.throws(
    () => localSystemStatusDecoder.decode(value),
    (error: unknown) => error instanceof ResponseDecodeError
      && error.path === expectedPath);
}
