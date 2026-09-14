import assert from "node:assert/strict";
import {
  formatObservationTimestamp
} from "../src/presentation/observationTimestamp.ts";

const formatted = formatObservationTimestamp("2026-08-05T12:34:56.000Z");
assert.match(formatted, /2026/);
assert.match(formatted, /08|8/);
assert.match(formatted, /05|5/);
assert.equal(formatObservationTimestamp("not-a-date"), "");
assert.equal(formatObservationTimestamp(), "");
