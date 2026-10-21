import assert from "node:assert/strict";
import { enrichSoftwareContextTarget } from "../src/components/softwareContextTarget.ts";
import type { SoftwareContextMenuTarget } from "../src/components/SoftwareContextMenu";
import type { ResourceTableRow } from "../src/types";

function processRow(processId: number, processStartKey?: string): ResourceTableRow {
  return { id: `process-${processId}`, depth: 1, kind: "process", name: "worker",
    softwareId: "suite", softwareName: "Suite", status: "running", processId, processStartKey,
    processCount: 1, processIds: [processId], executablePaths: [`C:/suite/worker-${processId}.exe`],
    impactScore: 0, values: {} };
}
const selected: SoftwareContextMenuTarget = { kind: "process", name: "worker", softwareId: "suite",
  softwareName: "Suite", processIds: [42], processTargets: [{ processId: 42, processStartKey: "100" }],
  executablePaths: ["C:/suite/worker-42.exe"] };
const peers = [processRow(42, "100"), processRow(43, "200")];
assert.strictEqual(enrichSoftwareContextTarget(selected, peers), selected);
assert.deepEqual(enrichSoftwareContextTarget(selected, peers).processTargets, [{ processId: 42, processStartKey: "100" }]);
assert.deepEqual(enrichSoftwareContextTarget(selected, peers).executablePaths, ["C:/suite/worker-42.exe"]);

// Missing identity and PID reuse never inherit a current peer's creation identity.
const missingIdentity: SoftwareContextMenuTarget = { ...selected, processTargets: [] };
assert.deepEqual(enrichSoftwareContextTarget(missingIdentity, peers).processTargets, []);
assert.deepEqual(enrichSoftwareContextTarget(selected, [processRow(42, "101"), peers[1]]).processTargets, selected.processTargets);

const group: SoftwareContextMenuTarget = { kind: "software", name: "Suite", softwareId: "suite" };
const expanded = enrichSoftwareContextTarget(group, peers);
assert.deepEqual(expanded.processTargets, [{ processId: 42, processStartKey: "100" }, { processId: 43, processStartKey: "200" }]);
assert.deepEqual(expanded.processIds, [42, 43]);
const ambiguous = enrichSoftwareContextTarget(group, [peers[0], processRow(42, "101"), peers[1]]);
assert.deepEqual(ambiguous.processTargets, [{ processId: 43, processStartKey: "200" }]);
console.log("software context actions preserve exact process instances and explicit group scope");
