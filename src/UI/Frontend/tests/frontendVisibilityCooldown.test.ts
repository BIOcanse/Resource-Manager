import assert from "node:assert/strict";
import { FrontendVisibilityCooldown } from "../src/frontendWork/FrontendVisibilityCooldown.ts";
import { ResourceManager } from "../src/preferences/generated/frontendPoints.values.ts";

assert.equal(ResourceManager.FrontendPoints.VisibilityCooldownMs, 5000);
let now = 0;
let nextId = 0;
const timers = new Map<number, { at: number; run: () => void }>();
const schedule = ((run: () => void, ms: number) => {
  const id = ++nextId;
  timers.set(id, { at: now + ms, run });
  return id;
}) as unknown as typeof setTimeout;
const cancel = ((id: number) => { timers.delete(id); }) as unknown as typeof clearTimeout;
let visible = true;
const transitions: boolean[] = [];
const owner = new FrontendVisibilityCooldown(5000, value => {
  if (value !== visible) transitions.push(value);
  visible = value;
}, schedule, cancel);
function advance(ms: number) {
  now += ms;
  for (const [id, timer] of timers) if (timer.at <= now) { timers.delete(id); timer.run(); }
}
owner.setVisible(false);
advance(1000);
owner.setVisible(true);
advance(5000);
assert.deepEqual(transitions, [], "snipping flicker neither stops nor restarts work");
owner.setVisible(false);
advance(4999);
owner.setVisible(false);
assert.equal(visible, true, "repeated hidden does not stop early or extend the deadline");
advance(1);
assert.deepEqual(transitions, [false]);
owner.setVisible(true);
assert.deepEqual(transitions, [false, true]);
owner.setVisible(false);
owner.dispose();
advance(6000);
assert.equal(visible, true, "disposal cancels the pending callback");
const immediate: boolean[] = [];
const zero = new FrontendVisibilityCooldown(0, value => immediate.push(value), schedule, cancel);
zero.setVisible(false);
assert.deepEqual(immediate, [false], "declared zero stops synchronously without a timer");
assert.equal(timers.size, 0);
zero.setVisible(true);
assert.deepEqual(immediate, [false, true]);
console.log("frontend visibility cooldown tests passed");
