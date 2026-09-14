import assert from "node:assert/strict";
import { normalizeSystemProcessIdentities } from
  "../src/processes/systemProcessIdentity.ts";

const exact = normalizeSystemProcessIdentities([
  { processId: 42, processStartKey: "132537600000000000" },
  { processId: 42, processStartKey: "132537600000000000" },
  { processId: 7, processStartKey: "9007199254740995" }
]);
assert.deepEqual(exact, [
  { processId: 7, processStartKey: "9007199254740995" },
  { processId: 42, processStartKey: "132537600000000000" }
]);

const ambiguous = normalizeSystemProcessIdentities([
  { processId: 10, processStartKey: "100" },
  { processId: 10, processStartKey: "101" },
  { processId: 11, processStartKey: "200" }
]);
assert.deepEqual(ambiguous, [
  { processId: 11, processStartKey: "200" }
]);

const invalid = normalizeSystemProcessIdentities([
  { processId: 0, processStartKey: "100" },
  { processId: 1.5, processStartKey: "100" },
  { processId: Number.MAX_SAFE_INTEGER + 1, processStartKey: "100" },
  { processId: 12, processStartKey: "0" },
  { processId: 13, processStartKey: "-1" },
  { processId: 14, processStartKey: "1.0" },
  { processId: 15, processStartKey: "1e3" }
]);
assert.deepEqual(invalid, []);

console.log("system process identity tests passed");
