import assert from "node:assert/strict";
import {
  buildManagementInventoryRevisionSubscriptionUrl,
  managementInventoryRevisionDecoder
} from "../src/data/managementInventoryRevision.ts";

assert.equal(
  buildManagementInventoryRevisionSubscriptionUrl(),
  "/api/management/inventory-revisions/subscribe");
assert.deepEqual(managementInventoryRevisionDecoder.decode({
  softwareRevision: "0",
  componentRevision: "42",
  browserRuntimeRevision: "18446744073709551615"
}), {
  softwareRevision: "0",
  componentRevision: "42",
  browserRuntimeRevision: "18446744073709551615"
});

for (const value of ["", "00", "-1", "18446744073709551616", 1, null]) {
  assert.throws(() => managementInventoryRevisionDecoder.decode({
    softwareRevision: value,
    componentRevision: "0",
    browserRuntimeRevision: "0"
  }));
}

console.log("Management inventory revision decoder tests passed.");
