import assert from "node:assert/strict";
import type {
  SourceDemand,
  SourceLease
} from "../src/frontendRuntime/source/SourceDescriptor.ts";
import { SourceLeaseBinding } from
  "../src/frontendRuntime/source/SourceLeaseBinding.ts";
import type { SourceSnapshot } from
  "../src/frontendRuntime/source/SourceSnapshot.ts";

interface Payload {
  readonly value: string;
}

const first = fakeLease(snapshot("query-a", "ready", { value: "old" }, 1));
const replacement = fakeLease(snapshot("query-b", "loading", null, 0));
const publications: SourceSnapshot<Payload>[] = [];
const binding = new SourceLeaseBinding<Payload>(
  (value) => publications.push(value));

binding.switch("query-a", () => first.lease);
assert.equal(publications.length, 1);

binding.switch("query-b", () => replacement.lease);
assert.equal(publications.length, 2);
assert.equal(publications.at(-1)?.key, "query-b");
assert.equal(publications.at(-1)?.data, null);
assert.equal(first.releaseCount(), 1);

replacement.emit(snapshot("query-b", "error", null, 1, new Error("failed")));
assert.equal(publications.length, 3);
assert.equal(publications.at(-1)?.key, "query-b");
assert.equal(publications.at(-1)?.data, null);

const demand: SourceDemand = { active: true, refreshIntervalMs: 250 };
binding.setDemand(demand);
assert.deepEqual(first.demands, []);
assert.deepEqual(replacement.demands, [demand]);

const superseding = fakeLease(snapshot("query-c", "loading", null, 0));
binding.switch("query-c", () => superseding.lease);
assert.equal(replacement.releaseCount(), 1);
assert.equal(publications.at(-1)?.key, "query-c");
replacement.emit(snapshot("query-b", "ready", { value: "late" }, 2));
assert.equal(publications.at(-1)?.key, "query-c");
assert.equal(publications.at(-1)?.data, null);

const accepted = snapshot("query-c", "ready", { value: "new" }, 1);
superseding.emit(accepted);
assert.equal(publications.at(-1), accepted);

const finalLease = fakeLease(snapshot("query-d", "loading", null, 0));
binding.switch("query-d", () => finalLease.lease);
assert.equal(superseding.releaseCount(), 1);
assert.equal(publications.at(-1)?.key, "query-d");
assert.equal(publications.at(-1)?.data, null);
superseding.emit(snapshot("query-c", "unavailable", null, 2));
assert.equal(publications.at(-1)?.key, "query-d");

finalLease.emit(snapshot("query-d", "ready", { value: "last" }, 1));
const beforeRelease = publications.at(-1);

binding.release();
assert.equal(finalLease.releaseCount(), 1);
assert.equal(publications.at(-1), beforeRelease);

const initialLoadingPublications: SourceSnapshot<Payload>[] = [];
const initialLoading = new SourceLeaseBinding<Payload>(
  (value) => initialLoadingPublications.push(value));
const initialLoadingLease = fakeLease(snapshot("query-initial", "loading", null, 0));
initialLoading.switch("query-initial", () => initialLoadingLease.lease);
assert.equal(initialLoadingPublications.length, 1);
assert.equal(initialLoadingPublications[0].data, null);
initialLoading.release();

const guardedPublications: SourceSnapshot<Payload>[] = [];
const guarded = new SourceLeaseBinding<Payload>((value) => {
  if (value.key === "query-publish-error") {
    throw new Error("publish failed");
  }
  guardedPublications.push(value);
});
const stable = fakeLease(snapshot("query-stable", "ready", { value: "stable" }, 1));
guarded.switch("query-stable", () => stable.lease);

const subscribeFailure = fakeLease(
  snapshot("query-subscribe-error", "loading", null, 0),
  new Error("subscribe failed"));
assert.throws(
  () => guarded.switch("query-subscribe-error", () => subscribeFailure.lease),
  /subscribe failed/);
assert.equal(subscribeFailure.releaseCount(), 1);
assert.equal(stable.releaseCount(), 0);

const publishFailure = fakeLease(
  snapshot("query-publish-error", "ready", { value: "rejected" }, 1));
assert.throws(
  () => guarded.switch("query-publish-error", () => publishFailure.lease),
  /publish failed/);
assert.equal(publishFailure.releaseCount(), 1);
assert.equal(stable.releaseCount(), 0);

stable.emit(snapshot("query-stable", "ready", { value: "still-current" }, 2));
assert.equal(guardedPublications.at(-1)?.data?.value, "still-current");
guarded.release();
assert.equal(stable.releaseCount(), 1);

console.log("Source lease binding tests passed");

function fakeLease(
  initial: SourceSnapshot<Payload>,
  subscribeError: Error | null = null
) {
  let current = initial;
  let releases = 0;
  const listeners = new Set<(value: SourceSnapshot<Payload>) => void>();
  const demands: SourceDemand[] = [];
  const lease: SourceLease<Payload> = {
    key: initial.key,
    get snapshot() {
      return current;
    },
    subscribe(listener) {
      if (subscribeError) {
        throw subscribeError;
      }
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    setDemand(demand) {
      demands.push(demand);
    },
    refresh() {
      return Promise.resolve(current);
    },
    acceptAuthoritative() {
      throw new Error("Not used by this fixture.");
    },
    release() {
      releases += 1;
      listeners.clear();
    }
  };
  return {
    lease,
    demands,
    emit(value: SourceSnapshot<Payload>) {
      current = value;
      for (const listener of listeners) {
        listener(value);
      }
    },
    releaseCount: () => releases
  };
}

function snapshot(
  key: string,
  status: SourceSnapshot<Payload>["status"],
  data: Payload | null,
  revision: number,
  error: unknown = null
): SourceSnapshot<Payload> {
  return {
    key,
    status,
    data,
    error,
    backendEpoch: "epoch-a",
    revision,
    acceptedAttempt: data ? revision : null,
    domainRevision: null,
    capturedAt: data ? "2026-08-29T00:00:00.000Z" : null
  };
}
