import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import { isolatedSourceFunctions } from "./helpers/isolatedSourceFunctions.ts";

const page = readFileSync(new URL("../src/features/management/components/ManagementPage.tsx", import.meta.url), "utf8");
const actions = page.slice(page.indexOf('<div class="software-actions">'));
const condition = /<Show when=\{([^}]+)\}>\s*<button[\s\S]*?onRemoveManual/.exec(actions)?.[1];
assert.ok(condition, "the manual removal button has a visibility condition");
const details = readFileSync(new URL("../src/app/useSoftwareActions.ts", import.meta.url), "utf8");
const detailCondition = /manualRegistration:\s*([^,\n]+)/.exec(details)?.[1];
assert.ok(detailCondition, "the software detail model projects manual registration");

for (const [id, kind, expected] of [
  ["opaque-id", "manual", true],
  ["manual:legacy-looking", "adapter", false],
  ["manual:without-facts", undefined, false],
  ["manual:real", "manual", true]
] as const) {
  const record = { id, name: "Fixture", registrationFacts: kind ? { kind, processCount: 0, serviceCount: 0 } : undefined };
  assert.equal(runInNewContext(detailCondition, { record }), expected,
    `${id}/${kind}: the detail model follows registration facts`);
  for (const mutable of [true, false]) {
    const allowed = expected && mutable;
    assert.equal(runInNewContext(condition, { props: { mutablePersistenceEnabled: mutable, software: record } }), allowed,
      `${id}/${kind}: button follows registration facts and persistence permission`);
    const calls: string[] = [];
    const { removeManualRegistration } = isolatedSourceFunctions(
      new URL("../src/stores/managementStore.ts", import.meta.url), ["removeManualRegistration"], {
        options: {
          mutablePersistenceEnabled: () => mutable,
          confirmDialog: async () => { calls.push("confirm"); return true; },
          showToast: () => {}
        },
        managementActionKey: (_type: string, target: string) => target,
        actionLabels: () => ({}), setActionLabel: () => {}, clearActionLabel: () => {},
        removeManualSoftware: async (target: string) => { calls.push(`remove:${target}`); },
        refreshSoftware: async () => { calls.push("refresh"); },
        showErrorToast: (error: unknown) => { throw error; },
        uiText: { managementFeedback: {}, stores: {}, feedback: {} }
      }
    );
    assert.equal(await removeManualRegistration(record), allowed, `${id}/${kind}: action guard follows registration facts`);
    assert.deepEqual(calls, allowed ? ["confirm", `remove:${id}`, "refresh"] : []);
  }
}
console.log("Manual removal button and action consume registrationFacts.kind.");
