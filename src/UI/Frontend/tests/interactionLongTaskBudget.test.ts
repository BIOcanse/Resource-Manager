import assert from "node:assert/strict";
import { ALLOWED_EFFECT_OVERRUNS, EFFECT_OVERRUN_TOLERANCE_MS, assessInteractionLongTasks } from "./browser/interactionLongTaskBudget.ts";
import { summarizeTaskCpu, type CpuCallFrame, type CpuProfile } from "./browser/pageMotionEvidence.ts";

assert.deepEqual(ALLOWED_EFFECT_OVERRUNS, []);
const windows = [{ step: "primary components→details", start: 100, end: 200 }, { step: "details device→gpu", start: 200, end: 400 }];
const entry = { effect: "contentTransition", step: windows[0].step, measuredMs: 60,
  measuredOn: { date: "2026-10-26", jobId: "synthetic-contract-fixture" }, reason: "Test fixture only; never registered" };
assert.equal(assessInteractionLongTasks([{ start: 100, duration: 50 }], windows).length, 0);
let [result] = assessInteractionLongTasks([{ start: 99, duration: 50.01 }], windows);
assert.equal(result.step, windows[0].step, "dispatch task starting before click must overlap the click window");
assert.equal(result.passed, false);
assert.match(result.message, /primary components→details: 50.01 ms.*unregistered/);
[result] = assessInteractionLongTasks([{ start: 110, duration: entry.measuredMs + EFFECT_OVERRUN_TOLERANCE_MS }], windows, [entry]);
assert.equal(result.passed, true);
[result] = assessInteractionLongTasks([{ start: 110, duration: entry.measuredMs + EFFECT_OVERRUN_TOLERANCE_MS + 0.01 }], windows, [entry]);
assert.equal(result.passed, false);
assert.match(result.message, /contentTransition.*synthetic-contract-fixture.*EFFECT_OVERRUN_TOLERANCE_MS/);
[result] = assessInteractionLongTasks([{ start: 200, duration: 60 }], windows, [entry]);
assert.equal(result.step, windows[1].step);
assert.equal(result.entry, null, "a registration on another step cannot exempt this step");
[result] = assessInteractionLongTasks([{ start: 500, duration: 60 }], windows, [entry]);
assert.equal(result.passed, false);
assert.match(result.step, /outside switch windows/);
// Exercise the runtime boundary with a deliberately malformed registry, without weakening its typed caller contract.
assert.throws(() => Reflect.apply(assessInteractionLongTasks, undefined, [[], windows, [{ ...entry, measuredOn: {} }]]), /Invalid/);
assert.throws(() => assessInteractionLongTasks([], windows, [entry, entry]), /Invalid/);

// CDP monotonic microseconds and page performance.now differ by NavigationStart.
const callFrame = (functionName: string): CpuCallFrame => ({ functionName, scriptId: "0", url: "", lineNumber: -1, columnNumber: -1 });
const profile: CpuProfile & { endTime: number } = { startTime: 1_000_000, endTime: 1_003_000, nodes: [
  { id: 1, callFrame: callFrame("(root)"), children: [2] },
  { id: 2, callFrame: callFrame("effect"), children: [3] },
  { id: 3, callFrame: callFrame("getBoundingClientRect") }
], samples: [3, 2, 3], timeDeltas: [1000, 1000, 1000] };
const cpu = summarizeTaskCpu(profile, 1, { start: 0.5, duration: 2 });
assert.equal(cpu.sampledMs, 2);
assert.equal(cpu.self.find((item) => item.functionName === "getBoundingClientRect")!.ms, 1);
assert.equal(cpu.inclusive.find((item) => item.functionName === "effect")!.ms, 2);
console.log("PASS interaction long-task attribution, exact budget boundaries, registry validation and CPU clock/weights");
