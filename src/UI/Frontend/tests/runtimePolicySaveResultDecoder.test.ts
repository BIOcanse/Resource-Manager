import assert from "node:assert/strict";
import { decodeRuntimePolicySaveResult } from "../src/api/runtimePolicySaveResultDecoder.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";

const result = {
  policy: { softwareId: "fixture" }, startupInterception: null,
  runtimeApplicationDisposition: "committedAndPublished",
  startupInterceptionDisposition: "notAttempted",
  runtimePlanVersion: 3, runtimePublicationSequence: 5,
  runtimeDeliveryFailureCount: 0, failureCode: null
};
for (const disposition of ["committedAndPublished", "committedWithDeliveryFailures", "savedNotApplied"]) {
  assert.equal(decodeRuntimePolicySaveResult({ ...result, runtimeApplicationDisposition: disposition }).runtimeApplicationDisposition, disposition);
}
for (const disposition of ["committedAndApplied", "committedWithCapabilityConstraints", "notRequested", "revisionConflict", "unknown", null, undefined]) {
  assert.throws(() => decodeRuntimePolicySaveResult({ ...result, runtimeApplicationDisposition: disposition }),
    (error: unknown) => error instanceof ResponseDecodeError && error.path === "$.runtimeApplicationDisposition");
}
for (const startupInterceptionDisposition of ["applied", "applyFailed", "notAttempted"]) {
  assert.equal(decodeRuntimePolicySaveResult({ ...result, startupInterceptionDisposition }).startupInterceptionDisposition, startupInterceptionDisposition);
}
assert.throws(() => decodeRuntimePolicySaveResult({ ...result, runtimeDeliveryFailureCount: -1 }), ResponseDecodeError);
assert.throws(() => decodeRuntimePolicySaveResult({ ...result, startupInterceptionDisposition: "published" }), ResponseDecodeError);
console.log("Runtime policy publication decoder tests passed");
