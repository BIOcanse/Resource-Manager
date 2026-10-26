import assert from "node:assert/strict";
import { isolatedSourceFunctions } from "./helpers/isolatedSourceFunctions.ts";

const { managementActionKeyFromOperation } = isolatedSourceFunctions(
  new URL("../src/features/management/components/ManagementPage.tsx", import.meta.url),
  ["managementActionKey", "managementActionKeyFromOperation"]
);

for (const [kind, prefix] of [["component.install", "component"], ["software.uninstall", "software"]]) {
  for (const domainKey of ["opaque-deduplication-key", "other:WRONG:target", undefined]) {
    assert.equal(managementActionKeyFromOperation({
      kind, domainKey, subject: { targetId: "Real:Target:ID" }
    }), `${prefix}:real:target:id`, `${kind}: subject owns target identity, domainKey=${domainKey}`);
  }
  for (const subject of [null, undefined, {}, { targetId: null }, { targetId: "" }]) {
    assert.equal(managementActionKeyFromOperation({ kind, domainKey: "legacy:wrong-id", subject }), null,
      `${kind}: no target identity must not be recovered from a deduplication key`);
  }
}
assert.equal(managementActionKeyFromOperation({ kind: "other", subject: { targetId: "target" } }), null);
console.log("Management action identity follows operation.subject.targetId.");
