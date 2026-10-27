import { readFile, realpath, stat } from "node:fs/promises";
import { basename, relative, isAbsolute, resolve, sep } from "node:path";

export const rawSliceRoot = "F:/code/Resource-Manager-Development/real-slices";
const deniedPath = /credentials?|access-keys?|access-requests?|draft|refresh|reapply|secrets?|tokens?/i;

export function assertRawSlicePath(path) {
  const rel = relative(resolve(rawSliceRoot), resolve(path));
  const segments = rel.split(sep);
  if (rel.startsWith("..") || isAbsolute(rel) || segments.length !== 2
    || !/^\d{8}T\d{6}Z$/.test(segments[0]) || segments[1] !== "slice.json") {
    throw new Error("Raw slices must be real-slices/<YYYYMMDDTHHMMSSZ>/slice.json");
  }
}

export function resolveSliceFilePath(path) {
  const resolved = resolve(path);
  return basename(resolved).toLowerCase() === "slice.json" ? resolved : resolve(resolved, "slice.json");
}

export function validateSlice(slice) {
  if (slice?.schemaVersion !== 1 || !isCanonicalUtcTimestamp(slice.startedAt)
    || !Number.isFinite(slice.durationMs) || slice.durationMs <= 0
    || typeof slice.complete !== "boolean"
    || !Array.isArray(slice.events) || slice.events.length > 200000) {
    throw new Error("Invalid slice header");
  }
  if (slice.readiness && (typeof slice.readiness.ready !== "boolean" || !Number.isFinite(slice.readiness.waitedMs)
    || !Number.isFinite(slice.readiness.timeoutMs) || slice.readiness.waitedMs < 0 || !Array.isArray(slice.readiness.events))) {
    throw new Error("Invalid slice readiness");
  }
  const groups = [{ events: slice.events, limit: slice.durationMs },
    ...(slice.readiness ? [{ events: slice.readiness.events, limit: slice.readiness.timeoutMs }] : [])];
  for (const group of groups) for (const event of group.events) {
    if (!["response", "frame", "end"].includes(event.kind) || !Number.isFinite(event.atMs)
      || event.atMs < 0 || event.atMs > group.limit
      || typeof event.path !== "string" || !isSafeSliceSelector(event.path)
      || !Object.hasOwn(event, "body")) {
      throw new Error("Invalid slice event");
    }
    if (event.kind === "response"
      && (!Number.isInteger(event.status) || event.status < 100 || event.status > 599
        || typeof event.contentType !== "string" || !["json", "text"].includes(event.encoding))) {
      throw new Error("Invalid response event");
    }
    if (event.kind === "frame" && !new URL(event.path, "http://localhost").pathname.endsWith("/subscribe")) {
      throw new Error("Invalid frame selector");
    }
    if (event.kind === "end" && (!Number.isInteger(event.status) || event.status < 0 || event.status > 599
      || typeof event.reason !== "string")) throw new Error("Invalid stream end event");
  }
  return slice;
}

export async function readSlice(path) {
  const candidate = resolveSliceFilePath(path);
  assertRawSlicePath(candidate);
  const actual = await realpath(candidate);
  assertRawSlicePath(actual);
  if (!(await stat(actual)).isFile()) throw new Error("Slice input must resolve to slice.json");
  return validateSlice(JSON.parse(await readFile(actual, "utf8")));
}

export function selectorKey(path) {
  const url = parseSelector(path);
  if (!url) throw new Error("Invalid production selector");
  url.searchParams.delete("intervalMs");
  if (!hasGroupedSelectorParameters(url.searchParams)) {
    normalizeSetSelectorValues(url.searchParams);
    url.searchParams.sort();
  }
  const query = url.searchParams.toString();
  return url.pathname + (query ? `?${query}` : "");
}

/**
 * A recorded selector can serve an exact request, or a request whose explicit
 * `ids`/`items` set is contained in the recorded set while every other selector
 * is equal. The result only describes coverage; callers still project subset
 * payloads according to the endpoint's wire shape.
 */
export function matchSelector(recordedPath, requestedPath) {
  const recorded = parseSelector(recordedPath);
  const requested = parseSelector(requestedPath);
  if (!recorded || !requested || recorded.pathname !== requested.pathname) return null;
  if (selectorKey(recordedPath) === selectorKey(requestedPath)) return { kind: "exact" };

  const candidateParameters = ["ids", "items"].filter(parameter =>
    !sameValues(recorded.searchParams.getAll(parameter), requested.searchParams.getAll(parameter)));
  if (candidateParameters.length !== 1) return null;
  const parameter = candidateParameters[0];
  if (parameter === "items" && recorded.pathname !== "/api/resource-facts/subscribe") return null;
  if (parameter === "ids" && !["/api/metrics/subscribe", "/api/metrics/gpu-specialized/subscribe"].includes(recorded.pathname)) return null;
  if (hasGroupedSelectorParameters(recorded.searchParams)
    || hasGroupedSelectorParameters(requested.searchParams)) return null;
  if (selectorKeyWithout(recorded.searchParams, parameter)
    !== selectorKeyWithout(requested.searchParams, parameter)) return null;

  const recordedValues = recorded.searchParams.getAll(parameter);
  const requestedValues = requested.searchParams.getAll(parameter);
  if (!hasUnambiguousSet(recordedValues) || !hasUnambiguousSet(requestedValues)) return null;
  const recordedSet = new Set(recordedValues);
  if (!requestedValues.every(value => recordedSet.has(value))) return null;
  return { kind: "subset", parameter, values: [...requestedValues] };
}

function isSafeSliceSelector(path) {
  const url = parseSelector(path);
  return Boolean(url && !deniedPath.test(path));
}

function parseSelector(path) {
  if (typeof path !== "string" || !path.startsWith("/api/") || path.startsWith("//")) return null;
  let url;
  try {
    url = new URL(path, "http://localhost");
  } catch {
    return null;
  }
  if (url.origin !== "http://localhost" || url.username || url.password || url.hash) return null;
  if (url.pathname !== path.split("?", 1)[0]) return null;
  return url;
}

function normalizeSetSelectorValues(params) {
  for (const parameter of ["ids", "items"]) {
    const values = params.getAll(parameter);
    if (values.length < 2) continue;
    params.delete(parameter);
    for (const value of values.sort()) params.append(parameter, value);
  }
}

function selectorKeyWithout(params, excludedParameter) {
  const clone = new URLSearchParams(params);
  clone.delete("intervalMs");
  clone.delete(excludedParameter);
  if (!hasGroupedSelectorParameters(clone)) {
    normalizeSetSelectorValues(clone);
    clone.sort();
  }
  return clone.toString();
}

function hasGroupedSelectorParameters(params) {
  return ["device", "fields", "modes", "measures"].some(parameter => params.has(parameter));
}

function sameValues(left, right) {
  return left.length === right.length && left.every((value, index) => value === right[index]);
}

function hasUnambiguousSet(values) {
  return values.length > 0 && values.every(value => value.length > 0)
    && new Set(values).size === values.length;
}

function isCanonicalUtcTimestamp(value) {
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(value)) return false;
  const timestamp = new Date(value);
  return Number.isFinite(timestamp.getTime()) && timestamp.toISOString() === value;
}
