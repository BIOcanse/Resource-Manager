import assert from "node:assert/strict";
import { canAnimateResourceBar } from "../src/features/resourceBreakdown/resourceBarAnimation.ts";
assert.equal(canAnimateResourceBar("normal"), true);
assert.equal(canAnimateResourceBar("none"), false);
assert.equal(canAnimateResourceBar("ultra"), false);
