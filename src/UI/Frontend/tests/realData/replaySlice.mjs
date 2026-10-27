import { readSlice, selectorKey, matchSelector } from "./sliceFormat.mjs";

// A selector is covered only by the same query (ignoring requested cadence).
// Known list selectors can consume a recorded superset; payload stays untouched.
export function coversSelector(recorded, requested) {
  return matchSelector(recorded, requested) !== null;
}

function coversFieldPayload(event, path) {
  const recorded = new URL(event.path, "http://localhost"), requested = new URL(path, "http://localhost");
  if (recorded.pathname !== "/api/metrics/subscribe" || requested.pathname !== recorded.pathname
    || !recorded.searchParams.has("fields") || !requested.searchParams.has("fields") || !Array.isArray(event.body)) return false;
  if ([...requested.searchParams.keys()].some(key => !["fields", "device", "intervalMs"].includes(key))) return false;
  const devices = requested.searchParams.getAll("device"), groups = requested.searchParams.getAll("fields");
  if (devices.length > 1 && devices.length !== groups.length) return false;
  const selections = devices.length > 1 ? devices.map((device, index) => ({ device, fields: groups[index] }))
    : [{ device: devices[0] ?? null, fields: groups.join(",") }];
  return selections.every(selection => {
    const fields = selection.fields.split(",").filter(Boolean);
    return fields.length > 0 && fields.every(name => event.body.some(device =>
      (selection.device === null || device.deviceIdentity === selection.device) && device.fields?.some(field => field.name === name)));
  });
}

export function createSliceReplay(slice, clock = () => performance.now()) {
  const started = clock();
  const events = slice.events.toSorted((a, b) => a.atMs - b.atMs);
  const select = (kind, path) => {
    const matching = events.filter(event => event.kind === kind && (coversSelector(event.path, path) || (kind === "frame" && coversFieldPayload(event, path))));
    if (!matching.length) return [];
    // Prefer an exact query; never mix frames from several recorded subscriptions.
    const selected = matching.find(event => selectorKey(event.path) === selectorKey(path))?.path ?? matching[0].path;
    return events.filter(event => event.kind === kind && event.path === selected);
  };
  return {
    response(path) {
      const matching = select("response", path);
      if (!matching.length) return null;
      const elapsed = clock() - started;
      return matching.findLast(event => event.atMs <= elapsed) ?? matching[0];
    },
    frames(path) { return select("frame", path); },
    subscribe(path, publish, schedule = setTimeout, cancel = clearTimeout) {
      const frames = select("frame", path);
      const elapsed = clock() - started;
      const current = frames.findLast(frame => frame.atMs <= elapsed);
      if (current) publish(current.body);
      const timers = frames.filter(frame => frame.atMs > elapsed)
        .map(frame => schedule(() => publish(frame.body), frame.atMs - elapsed));
      return () => { for (const timer of timers) cancel(timer); };
    }
  };
}

export async function loadSliceReplay(path) {
  return path ? createSliceReplay(await readSlice(path)) : null;
}
