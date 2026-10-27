import { mkdir, rename, unlink, writeFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { basename, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { readSlice, validateSlice, selectorKey } from "./sliceFormat.mjs";

export const profileLanguageIds = Object.freeze([
  "zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES", "ru-RU"
]);

const sampleLimit = 4096;
const finite = value => typeof value === "number" && Number.isFinite(value);
const isRecord = value => value !== null && typeof value === "object" && !Array.isArray(value);
const unicodeLength = value => [...value].length;

function histogram(values) {
  const result = {};
  for (const value of values) result[value] = (result[value] ?? 0) + 1;
  return Object.fromEntries(Object.entries(result).sort((left, right) => Number(left[0]) - Number(right[0])));
}

function quantile(sorted, fraction) {
  if (!sorted.length) return null;
  const position = (sorted.length - 1) * fraction;
  const lower = Math.floor(position);
  const upper = Math.ceil(position);
  if (lower === upper) return sorted[lower];
  return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
}

function summarize(samples) {
  const values = samples.slice(0, sampleLimit);
  const sorted = [...values].sort((left, right) => left - right);
  if (!sorted.length) return { samples: [], sampleCount: 0, range: { min: null, max: null }, distribution: { p50: null, p90: null, mean: null } };
  return {
    samples: values,
    sampleCount: samples.length,
    range: { min: sorted[0], max: sorted.at(-1) },
    distribution: {
      p50: quantile(sorted, 0.5),
      p90: quantile(sorted, 0.9),
      mean: values.reduce((total, value) => total + value, 0) / values.length
    }
  };
}

function walk(root, visit, initialContext = {}) {
  const stack = [{ value: root, context: initialContext, depth: 0 }];
  const seen = new Set();
  let visited = 0;
  while (stack.length) {
    const { value, context, depth } = stack.pop();
    if (!value || typeof value !== "object" || seen.has(value)) continue;
    if (depth > 128) throw new Error("Slice payload nesting limit exceeded while learning profile.");
    if (++visited > 200000) throw new Error("Slice payload node limit exceeded while learning profile.");
    seen.add(value);
    const current = isRecord(value) && typeof value.deviceIdentity === "string"
      ? { ...context, deviceIdentity: value.deviceIdentity }
      : context;
    visit(value, current);
    const children = Array.isArray(value) ? value : Object.values(value);
    for (const child of children) {
      if (child && typeof child === "object") stack.push({ value: child, context: current, depth: depth + 1 });
    }
  }
}

function languageFrom(value) {
  const normalized = String(value ?? "").trim().replaceAll("_", "-").toLowerCase();
  return profileLanguageIds.find(language => language.toLowerCase() === normalized) ?? null;
}

function classifyNameScript(value) {
  if (/\p{Script=Hangul}/u.test(value)) return { language: "ko-KR", bucket: null };
  if (/[\p{Script=Hiragana}\p{Script=Katakana}]/u.test(value)) return { language: "ja-JP", bucket: null };
  if (/\p{Script=Cyrillic}/u.test(value)) return { language: "ru-RU", bucket: null };
  if (/\p{Script=Han}/u.test(value)) return { language: null, bucket: "han-ambiguous" };
  if (/\p{Script=Latin}/u.test(value)) return { language: null, bucket: "latin-ambiguous" };
  return { language: null, bucket: "other-script" };
}

function extractProcessCount(body) {
  let largest = null;
  const processIds = new Set();
  const processIndexes = new Set();
  let sawProcessArray = false;
  walk(body, value => {
    if (!isRecord(value)) return;
    for (const [key, items] of Object.entries(value)) {
      if (key.toLowerCase() !== "processes" || !Array.isArray(items)) continue;
      sawProcessArray = true;
      const ids = [];
      for (const item of items) {
        if (!isRecord(item)) continue;
        const processId = item.processId ?? item.pid;
        if (finite(processId) && processId > 0) {
          processIds.add(processId);
          ids.push(processId);
        } else if (finite(item.processIndex) && item.processIndex >= 0) {
          processIndexes.add(item.processIndex);
          ids.push(item.processIndex);
        }
      }
      largest = Math.max(largest ?? 0, new Set(ids).size);
    }
    for (const [key, items] of Object.entries(value)) {
      if (key.toLowerCase() !== "software" || !Array.isArray(items)) continue;
      const count = items.reduce((total, item) => total + (finite(item?.processCount) ? Math.max(0, item.processCount) : 0), 0);
      if (count > 0) largest = Math.max(largest ?? 0, count);
    }
  });
  if (processIds.size) return processIds.size;
  if (processIndexes.size) return processIndexes.size;
  return sawProcessArray ? largest ?? 0 : largest;
}

function extractGpuCount(body) {
  const indices = new Set();
  let arrayCount = 0;
  walk(body, (value, context) => {
    if (!isRecord(value)) return;
    const candidates = [value.id, value.metricId, value.name, value.totalMetricId];
    for (const candidate of candidates) {
      const match = typeof candidate === "string" ? /^gpu\.(\d+)(?:\.|$)/i.exec(candidate) : null;
      if (match) indices.add(Number(match[1]));
    }
    if (value.device?.kind === "gpu_adapter" || value.kind === "gpu_adapter") {
      const ordinal = finite(value.ordinal) ? value.ordinal : finite(value.device?.ordinal) ? value.device.ordinal : null;
      if (ordinal !== null) indices.add(ordinal);
      else if (context.deviceIdentity) indices.add(context.deviceIdentity);
      else indices.add(`gpu-${indices.size}`);
    }
    for (const key of ["gpus", "adapters", "gpuInformation"]) {
      if (Array.isArray(value[key])) arrayCount = Math.max(arrayCount, value[key].length);
    }
  });
  if (!indices.size && !arrayCount) return null;
  const indexedCount = [...indices].filter(Number.isInteger).reduce((maximum, index) => Math.max(maximum, index + 1), 0);
  return Math.max(indices.size, indexedCount, arrayCount);
}

function extractDeviceCount(body) {
  let maximum = null;
  walk(body, value => {
    const arrays = Array.isArray(value) ? [value] : isRecord(value) ? Object.values(value).filter(Array.isArray) : [];
    for (const items of arrays) {
      const records = items.filter(item => isRecord(item)
        && (typeof item.deviceIdentity === "string" || (typeof item.kind === "string" && isRecord(item.address))
          || (isRecord(item.device) && typeof item.device.kind === "string")));
      if (records.length) maximum = Math.max(maximum ?? 0, records.length);
    }
  });
  return maximum;
}

function addDeviceTemplates(body, templates) {
  walk(body, value => {
    const candidates = Array.isArray(value) ? value : isRecord(value) && Array.isArray(value.devices) ? value.devices : [];
    for (const item of candidates) {
      if (!isRecord(item)) continue;
      const device = isRecord(item.device) ? item.device : item;
      const kind = device.kind;
      if (typeof kind !== "string" || !isRecord(device.address)) continue;
      if (!templates[kind]) templates[kind] = structuredClone(item);
    }
  });
}

function addLocalizedNames(body, target, unclassified) {
  const add = (language, name) => {
    if (language) {
      target[language].push(unicodeLength(name));
      return;
    }
    const classification = classifyNameScript(name);
    if (classification.language) target[classification.language].push(unicodeLength(name));
    else (unclassified[classification.bucket] ??= []).push(unicodeLength(name));
  };
  walk(body, value => {
    if (!isRecord(value)) return;
    for (const [key, localized] of Object.entries(value)) {
      if (!["namesByLanguage", "namesByLocale", "localizedNames"].includes(key)) continue;
      if (!isRecord(localized)) continue;
      for (const [languageId, names] of Object.entries(localized)) {
        const language = languageFrom(languageId);
        if (!language) continue;
        const texts = Array.isArray(names)
          ? names.filter(name => typeof name === "string")
          : isRecord(names) ? Object.values(names).filter(name => typeof name === "string") : [];
        for (const name of texts) add(language, name);
      }
    }
    const language = languageFrom(value.languageId ?? value.language ?? value.locale);
    if (typeof value.name === "string" && language) add(language, value.name);
    else if (typeof (value.displayName ?? value.name) === "string"
      && (finite(value.processId) || finite(value.pid) || typeof value.softwareId === "string" || typeof value.deviceIdentity === "string")) {
      add(null, value.displayName ?? value.name);
    }
  });
}

function addMetricSamples(body, target) {
  walk(body, (value, context) => {
    if (!isRecord(value)) return;
    const unit = typeof value.unit === "string" ? value.unit : null;
    const id = value.metricId ?? value.id ?? (typeof value.name === "string" && unit
      ? `${value.name}${context.deviceIdentity ? `@${context.deviceIdentity}` : ""}` : null);
    if (typeof id !== "string" || !unit) return;
    let bucket = target.get(id);
    if (!bucket) {
      bucket = { unit, samples: [], integerSamples: [], percentSamples: [], nullSamples: 0, unavailableReasons: [] };
      target.set(id, bucket);
    }
    if (unit && !bucket.unit) bucket.unit = unit;
    let hasValue = false;
    for (const key of ["numericValue", "totalValue", "value", "number"]) {
      if (finite(value[key])) {
        bucket.samples.push(value[key]);
        hasValue = true;
        break;
      }
    }
    if (typeof value.integerValue === "string" && /^-?\d+$/.test(value.integerValue)) {
      bucket.integerSamples.push(value.integerValue);
      hasValue = true;
    }
    if (!hasValue && ["numericValue", "totalValue", "value", "number"].some(key => Object.hasOwn(value, key) && value[key] === null)) {
      bucket.nullSamples++;
      if (typeof value.unavailableReason === "string") bucket.unavailableReasons.push(value.unavailableReason);
    }
    const percent = value.percent ?? value.totalSystemPercent ?? value.percentValue;
    if (finite(percent)) bucket.percentSamples.push(percent);
  });
}

function addEngineSamples(body, target) {
  walk(body, value => {
    if (!isRecord(value)) return;
    if (Array.isArray(value.engines)) target.push(value.engines.length);
    else if (finite(value.engineCount) && value.engineCount >= 0) target.push(value.engineCount);
  });
}

function distributionProfile(samples, observed = true) {
  return { ...summarize(samples), observed, status: samples.length ? "observed" : "unobserved" };
}

async function readSliceDirectory(directory) {
  const path = resolve(directory, "slice.json");
  const slice = await readSlice(path);
  return { slice, source: basename(resolve(path, "..")) };
}

function aggregateProfile(inputs) {
  const processCounts = [];
  const gpuCounts = [];
  const deviceCounts = [];
  const engineCountsPerGpu = [];
  const frameGroups = new Map();
  const metricValues = new Map();
  const localizedLengths = Object.fromEntries(profileLanguageIds.map(language => [language, []]));
  const unclassifiedLengths = {};
  const deviceKinds = new Set();
  const deviceTemplates = {};
  let totalEvents = 0;
  let totalDurationMs = 0;

  inputs.forEach(({ slice }, sliceIndex) => {
    totalEvents += slice.events.length;
    totalDurationMs += slice.durationMs;
    const countAtTime = new Map();
    for (const event of slice.events) {
      if (event.kind === "end") continue;
      if (event.kind === "frame") {
        const key = `${sliceIndex}:${selectorKey(event.path)}`;
        const frames = frameGroups.get(key) ?? [];
        frames.push(event.atMs);
        frameGroups.set(key, frames);
      }
      const count = extractProcessCount(event.body);
      if (count !== null) {
        const key = `${sliceIndex}:${event.atMs}`;
        countAtTime.set(key, Math.max(countAtTime.get(key) ?? 0, count));
      }
      const gpuCount = extractGpuCount(event.body);
      if (gpuCount !== null) gpuCounts.push(gpuCount);
      const deviceCount = extractDeviceCount(event.body);
      if (deviceCount !== null) deviceCounts.push(deviceCount);
      addLocalizedNames(event.body, localizedLengths, unclassifiedLengths);
      addMetricSamples(event.body, metricValues);
      addEngineSamples(event.body, engineCountsPerGpu);
      addDeviceTemplates(event.body, deviceTemplates);
      walk(event.body, value => {
        if (isRecord(value)) {
          if (typeof value.kind === "string" && (isRecord(value.address) || typeof value.deviceIdentity === "string")) deviceKinds.add(value.kind);
          if (isRecord(value.device) && typeof value.device.kind === "string") deviceKinds.add(value.device.kind);
        }
      });
    }
    processCounts.push(...countAtTime.values());
  });

  const frameIntervalSamplesMs = [];
  for (const frames of frameGroups.values()) {
    const ordered = [...frames].sort((left, right) => left - right);
    for (let index = 1; index < ordered.length; index++) {
      const interval = ordered[index] - ordered[index - 1];
      if (interval > 0) frameIntervalSamplesMs.push(interval);
    }
  }

  const metrics = Object.fromEntries([...metricValues.entries()].sort(([left], [right]) => left.localeCompare(right)).map(([id, values]) => [id, {
    unit: values.unit,
    ...summarize(values.samples),
    integerSamples: values.integerSamples.slice(0, sampleLimit),
    integerRange: values.integerSamples.length ? {
      min: values.integerSamples.reduce((minimum, value) => BigInt(value) < BigInt(minimum) ? value : minimum),
      max: values.integerSamples.reduce((maximum, value) => BigInt(value) > BigInt(maximum) ? value : maximum)
    } : { min: null, max: null },
    percentSamples: values.percentSamples.slice(0, sampleLimit),
    nullSamples: values.nullSamples,
    unavailableReasons: [...new Set(values.unavailableReasons)],
    observed: values.samples.length > 0 || values.integerSamples.length > 0 || values.nullSamples > 0,
    status: values.samples.length || values.integerSamples.length ? values.nullSamples ? "partially-available" : "observed"
      : values.nullSamples ? "observed-unavailable" : "unobserved"
  }]));

  const nameLengthHistograms = Object.fromEntries(profileLanguageIds.map(language => {
    const samples = localizedLengths[language].slice(0, sampleLimit);
    return [language, {
      unit: "unicode-code-points",
      ...summarize(samples),
      histogram: histogram(samples),
      observed: samples.length > 0,
      status: samples.length ? "observed" : "unobserved"
    }];
  }));
  const unclassifiedNameLengthHistograms = Object.fromEntries(Object.entries(unclassifiedLengths).map(([category, samples]) => [category, {
    unit: "unicode-code-points",
    ...summarize(samples),
    histogram: histogram(samples),
    observed: samples.length > 0,
    status: "observed-ambiguous-language"
  }]));

  return {
    schemaVersion: 1,
    source: "learned",
    provenance: {
      sliceDirectories: inputs.map(({ source }) => source),
      totalDurationMs,
      totalEvents,
      nameLanguageInference: "Explicit language metadata is kept; Hangul, kana, and Cyrillic map to an unambiguous script locale; Latin and Han names remain in ambiguity buckets.",
      dimensions: {
        processCounts: processCounts.length ? "observed" : "unobserved",
        nameLengthHistograms: Object.values(nameLengthHistograms).some(item => item.observed) ? "partially-observed" : "unobserved",
        gpuCounts: gpuCounts.length ? "observed" : "unobserved",
        engineCountsPerGpu: engineCountsPerGpu.length ? "observed" : "unobserved",
        deviceCounts: deviceCounts.length ? "observed" : "unobserved",
        metrics: Object.values(metrics).some(item => item.observed) ? "observed" : "unobserved",
        frameIntervalsMs: frameIntervalSamplesMs.length ? "observed" : "unobserved"
      }
    },
    processCounts: distributionProfile(processCounts),
    nameLengthHistograms,
    unclassifiedNameLengthHistograms,
    gpuCounts: distributionProfile(gpuCounts),
    engineCountsPerGpu: distributionProfile(engineCountsPerGpu),
    deviceCounts: distributionProfile(deviceCounts),
    deviceKinds: [...deviceKinds].sort(),
    deviceTemplates,
    metrics,
    frameIntervalSamplesMs: frameIntervalSamplesMs.slice(0, sampleLimit)
  };
}

export { histogram as buildHistogram };

export function learnProfileFromSlices(slices, sourceLabels = []) {
  const values = (Array.isArray(slices) ? slices : [slices]).filter(Boolean);
  if (!values.length) throw new Error("Pass one or more validated slice values.");
  const labels = Array.isArray(sourceLabels) ? sourceLabels : [];
  const inputs = values.map((slice, index) => ({
    slice: validateSlice(slice),
    source: typeof labels[index] === "string" ? labels[index] : `in-memory-slice-${index + 1}`
  }));
  return aggregateProfile(inputs);
}

// Directory reads go through readSlice so only real-slices/<UTC stamp>/slice.json is accepted.
export async function learnProfile(sliceDirectories) {
  const directories = (Array.isArray(sliceDirectories) ? sliceDirectories : [sliceDirectories]).filter(Boolean);
  if (!directories.length) throw new Error("Pass one or more slice directories.");
  const inputs = await Promise.all(directories.map(readSliceDirectory));
  return aggregateProfile(inputs);
}

function assertLearnedProfile(profile) {
  if (!isRecord(profile) || profile.schemaVersion !== 1 || profile.source !== "learned") {
    throw new Error("Only a schemaVersion 1 learned profile can be written.");
  }
  for (const key of ["processCounts", "gpuCounts", "engineCountsPerGpu", "deviceCounts"]) {
    if (!isRecord(profile[key]) || !Array.isArray(profile[key].samples) || !isRecord(profile[key].range)) {
      throw new Error(`Learned profile has an invalid ${key} distribution.`);
    }
  }
  if (!isRecord(profile.nameLengthHistograms) || !profileLanguageIds.every(language => {
    const value = profile.nameLengthHistograms[language];
    return isRecord(value) && Array.isArray(value.samples) && isRecord(value.histogram);
  })) throw new Error("Learned profile must contain name-length histograms for all nine UI languages.");
  if (!isRecord(profile.metrics) || !Array.isArray(profile.frameIntervalSamplesMs)
    || !Array.isArray(profile.deviceKinds) || !isRecord(profile.provenance)) {
    throw new Error("Learned profile is missing metrics, device kinds, frame intervals, or provenance.");
  }
}

export async function writeProfileAtomically(outputPath, profile) {
  assertLearnedProfile(profile);
  const destination = resolve(outputPath);
  const directory = dirname(destination);
  const temporary = resolve(directory, `.${basename(destination)}.${randomUUID()}.tmp`);
  await mkdir(directory, { recursive: true });
  let temporaryCreated = false;
  try {
    await writeFile(temporary, `${JSON.stringify(profile, null, 2)}\n`, { encoding: "utf8", flag: "wx" });
    temporaryCreated = true;
    await rename(temporary, destination);
    temporaryCreated = false;
    return destination;
  } catch (error) {
    if (temporaryCreated) {
      try { await unlink(temporary); } catch { /* Preserve the original write error. */ }
    }
    throw error;
  }
}

export function parseLearnerArgs(args) {
  let outputPath = null;
  const sliceDirectories = [];
  let help = false;
  for (let index = 0; index < args.length; index++) {
    const value = args[index];
    if (value === "--help" || value === "-h") {
      help = true;
      continue;
    }
    if (value === "--out") {
      if (!args[index + 1]) throw new Error("--out requires a profile file path.");
      outputPath = args[++index];
      continue;
    }
    if (value.startsWith("-")) throw new Error(`Unknown option: ${value}`);
    sliceDirectories.push(value);
  }
  if (!help && (!outputPath || !sliceDirectories.length)) {
    throw new Error("Usage: node tests/realData/learnProfile.mjs --out PROFILE.json <slice-dir> [<slice-dir> ...]");
  }
  return { outputPath, sliceDirectories, help };
}

async function runCli(args) {
  const options = parseLearnerArgs(args);
  if (options.help) {
    process.stdout.write("Usage: node tests/realData/learnProfile.mjs --out PROFILE.json <slice-dir> [<slice-dir> ...]\n");
    return;
  }
  const profile = await learnProfile(options.sliceDirectories);
  const destination = await writeProfileAtomically(options.outputPath, profile);
  process.stdout.write(`Wrote learned profile to ${destination}\n`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  runCli(process.argv.slice(2)).catch(error => {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  });
}
