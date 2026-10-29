import assert from "node:assert/strict";
import { createRoot, createSignal } from "solid-js";
import {
  backendNotificationListDecoder, knownBackendNotification, type BackendNotificationList, type KnownBackendNotification
} from "../src/data/notifications/backendNotifications.ts";
import {
  bindBackendNotificationToasts, createBackendNotificationFollower, showsInApp
} from "../src/features/notifications/backendNotificationToasts.ts";
import { pointPreferences } from "../src/preferences/frontendPreferences.ts";
import type { AppNotificationSettings } from "../src/types.ts";
import type { CurrentValueSource } from "../src/frontendRuntime/push/PushValueSourceFamily.ts";
import type { SourceSnapshot, SourceStatus } from "../src/frontendRuntime/source/SourceSnapshot.ts";

// docs/notifications_2026-10-29.md: the backend sends kind and facts; the interface decides what to show.
const decode = (value: unknown) => backendNotificationListDecoder.decode(value) as BackendNotificationList;
const action = (sequence: number, facts: Record<string, unknown> = {}) => ({ sequence, kind: "memory-protection.action",
  occurredAt: "2026-10-29T10:00:00Z", facts: { actionId: `action-${sequence}`, targetKind: "process", name: "game.exe",
    processId: 4100 + sequence, recordCount: 1, bytesWritten: 1024 * sequence, ...facts } });
const other = (sequence: number) => ({ sequence, kind: "backpack-overheat.action", occurredAt: "2026-10-29T10:00:00Z",
  facts: { anything: [1, 2] } });

// The envelope is checked; facts are kept for the kind decoders, so an unknown kind or bad facts never fail the frame.
const list = decode({ notifications: [other(1), action(2, { processId: "x" }), action(3, { targetKind: "processTree", recordCount: 4 })] });
assert.deepEqual(list.notifications.map(item => item.sequence), [1, 2, 3]);
assert.equal(knownBackendNotification(list.notifications[0]!), null, "unknown kind");
assert.equal(knownBackendNotification(list.notifications[1]!), null, "facts that do not decode");
assert.deepEqual(knownBackendNotification(list.notifications[2]!), { kind: "memory-protection.action", sequence: 3,
  targetKind: "processTree", name: "game.exe", processId: 4103, recordCount: 4, bytesWritten: 3072 });
assert.deepEqual(decode({ notifications: [] }).notifications, []);
for (const invalid of [
  {}, { notifications: null }, { notifications: [{ ...action(1), sequence: 0 }] },
  { notifications: [action(2), action(1)] }, { notifications: [action(1), action(1)] },
  { notifications: [{ ...action(1), kind: "" }] }, { notifications: [{ ...action(1), facts: null }] },
  { notifications: [{ ...action(1), occurredAt: 5 }] }
]) assert.throws(() => decode(invalid), undefined, JSON.stringify(invalid));

// Follower: the first list only records the newest sequence; later ones show each newer known notification once.
{
  const shown: number[] = [];
  const follower = createBackendNotificationFollower(notification => shown.push(notification.sequence));
  follower.accept(decode({ notifications: [action(1), action(2)] }));
  assert.deepEqual(shown, [], "first frame shows nothing");
  follower.accept(decode({ notifications: [action(1), action(2), other(3), action(4, { name: 7 }), action(5)] }));
  assert.deepEqual(shown, [5], "unknown kinds and bad facts are skipped, later known ones still show");
  follower.accept(decode({ notifications: [action(2), other(3), action(4), action(5)] }));
  assert.deepEqual(shown, [5], "nothing is shown twice");
  follower.accept(decode({ notifications: [action(1)] }));
  assert.deepEqual(shown, [5], "a list behind the record (file rebuilt) only records");
  follower.accept(decode({ notifications: [action(1), action(2)] }));
  assert.deepEqual(shown, [5, 2], "after the rebuild newer sequences show again");
  follower.reset();
  follower.accept(decode({ notifications: [action(1), action(2), action(3)] }));
  assert.deepEqual(shown, [5, 2], "a reset starts again from a first frame");
}
{
  const shown: number[] = [];
  const follower = createBackendNotificationFollower(notification => shown.push(notification.sequence));
  follower.accept(decode({ notifications: [] }));
  follower.accept(decode({ notifications: [action(1)] }));
  assert.deepEqual(shown, [1], "an empty first list records 0, so sequence 1 shows");
}

// Binding: subscribes only while active; the value handed back synchronously at subscribe is the previous
// subscription's cache, not this subscription's first frame; failures and deactivation start over.
{
  const listeners = new Set<(snapshot: SourceSnapshot<BackendNotificationList>) => void>();
  let cached: BackendNotificationList | null = decode({ notifications: [action(1)] });
  let subscribeCount = 0;
  const snapshot = (status: SourceStatus, data: BackendNotificationList | null): SourceSnapshot<BackendNotificationList> =>
    ({ key: "notifications.list", status, data, error: null, backendEpoch: "e", revision: 1, acceptedAttempt: 1,
      domainRevision: null, capturedAt: null });
  const source = {
    readSnapshot: () => snapshot(cached ? "ready" : "loading", cached),
    subscribeSnapshot(listener: (value: SourceSnapshot<BackendNotificationList>) => void) {
      subscribeCount++;
      listeners.add(listener);
      listener(snapshot(cached ? "ready" : "loading", cached));
      return () => listeners.delete(listener);
    }
  } as unknown as CurrentValueSource<BackendNotificationList>;
  const send = (status: SourceStatus, data: BackendNotificationList | null) => {
    if (data) cached = data;
    for (const listener of [...listeners]) listener(snapshot(status, data));
  };
  const shown: KnownBackendNotification[] = [];
  const [active, setActive] = createSignal(false);
  const dispose = createRoot(dispose => {
    bindBackendNotificationToasts({ source, active, settings: () => pointPreferences().notifications,
      show: notification => shown.push(notification) });
    return dispose;
  });
  assert.equal(subscribeCount, 0, "inactive interface does not subscribe");
  setActive(true);
  assert.equal(subscribeCount, 1);
  send("ready", decode({ notifications: [action(1), action(2)] }));
  assert.deepEqual(shown, [], "the replay after subscribing only records, even though the cache was older");
  send("ready", decode({ notifications: [action(1), action(2), action(3)] }));
  assert.deepEqual(shown.map(item => item.sequence), [3]);
  send("error", null);
  send("ready", decode({ notifications: [action(2), action(3), action(4)] }));
  assert.deepEqual(shown.map(item => item.sequence), [3], "after a failure the next replay only records");
  setActive(false);
  assert.equal(listeners.size, 0, "hiding the interface unsubscribes");
  send("ready", decode({ notifications: [action(3), action(4), action(5)] }));
  setActive(true);
  send("ready", decode({ notifications: [action(4), action(5), action(6)] }));
  assert.deepEqual(shown.map(item => item.sequence), [3], "notifications while hidden are left to the tray");
  send("ready", decode({ notifications: [action(5), action(6), action(7)] }));
  assert.deepEqual(shown.map(item => item.sequence), [3, 7]);
  dispose();
  assert.equal(listeners.size, 0, "disposal unsubscribes");
}

// The other two kinds: only the facts the interface shows are required.
{
  const update = (sequence: number, facts: Record<string, unknown>) => ({ sequence, kind: "update-available", occurredAt: "2026-10-29T10:00:00Z", facts });
  const access = (sequence: number, facts: Record<string, unknown>) => ({ sequence, kind: "access-request", occurredAt: "2026-10-29T10:00:00Z", facts });
  const frame = decode({ notifications: [update(1, { version: "1.4.0" }), update(2, { version: "" }), update(3, {}),
    access(4, { requestId: "r-1", name: "Example agent", observedExecutablePath: "C:\Tools\agent.exe", extra: true }),
    access(5, { requestId: "r-2", name: "Example agent" }), access(6, { requestId: 7, name: "x", observedExecutablePath: "y" })] });
  assert.deepEqual(frame.notifications.map(knownBackendNotification), [
    { kind: "update-available", sequence: 1, version: "1.4.0" }, null, null,
    { kind: "access-request", sequence: 4, requestId: "r-1", name: "Example agent", observedExecutablePath: "C:\Tools\agent.exe" },
    null, null]);
}

// Settings: a kind shows in the interface only while its "show" switch and its in-app switch are both on;
// the Windows switch belongs to the tray. Before the preference cache is read nothing shows.
{
  const all = pointPreferences().notifications;
  const kinds = [["memory-protection.action", "memoryProtection"], ["update-available", "update"], ["access-request", "accessRequest"]] as const;
  for (const [kind, prefix] of kinds) {
    assert.equal(showsInApp(kind, all), true, kind);
    assert.equal(showsInApp(kind, { ...all, [`${prefix}Notify`]: "off" }), false, `${kind}: show switch off`);
    assert.equal(showsInApp(kind, { ...all, [`${prefix}InApp`]: "off" }), false, `${kind}: in-app off`);
    assert.equal(showsInApp(kind, { ...all, [`${prefix}System`]: "off" }), true, `${kind}: the Windows switch does not stop the in-app notice`);
    for (const [otherKind, otherPrefix] of kinds) if (otherKind !== kind)
      assert.equal(showsInApp(kind, { ...all, [`${otherPrefix}Notify`]: "off", [`${otherPrefix}InApp`]: "off" }), true, `${kind} ignores ${otherKind}`);
  }
  assert.equal(showsInApp("update-available", undefined), false);
}

// The binding reads the saved switches at the moment it decides; a notification skipped by a switch is not shown later.
{
  const listeners = new Set<(snapshot: SourceSnapshot<BackendNotificationList>) => void>();
  const snapshot = (data: BackendNotificationList): SourceSnapshot<BackendNotificationList> =>
    ({ key: "notifications.list", status: "ready", data, error: null, backendEpoch: "e", revision: 1, acceptedAttempt: 1,
      domainRevision: null, capturedAt: null });
  const source = {
    readSnapshot: () => snapshot(decode({ notifications: [] })),
    subscribeSnapshot(listener: (value: SourceSnapshot<BackendNotificationList>) => void) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    }
  } as unknown as CurrentValueSource<BackendNotificationList>;
  const send = (...sequences: number[]) => {
    const data = decode({ notifications: sequences.map(sequence => action(sequence)) });
    for (const listener of [...listeners]) listener(snapshot(data));
  };
  const [settings, setSettings] = createSignal<AppNotificationSettings | undefined>(undefined);
  const shown: number[] = [];
  const dispose = createRoot(dispose => {
    bindBackendNotificationToasts({ source, active: () => true, settings, show: notification => shown.push(notification.sequence) });
    return dispose;
  });
  send(1);
  send(1, 2);
  assert.deepEqual(shown, [], "nothing shows before the preference cache is read");
  setSettings({ ...pointPreferences().notifications, memoryProtectionNotify: "off" });
  send(1, 2, 3);
  assert.deepEqual(shown, [], "show switch off");
  setSettings({ ...pointPreferences().notifications, memoryProtectionInApp: "off" });
  send(1, 2, 3, 4);
  assert.deepEqual(shown, [], "in-app switch off");
  setSettings(pointPreferences().notifications);
  assert.deepEqual(shown, [], "turning a switch on does not show the skipped notifications");
  send(1, 2, 3, 4, 5);
  assert.deepEqual(shown, [5]);
  dispose();
}

console.log("backend notifications: ok");
