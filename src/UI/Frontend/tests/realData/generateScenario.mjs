import { mkdir, readFile, writeFile } from "node:fs/promises";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

export const scenarioLanguages = Object.freeze([
  "zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES", "ru-RU"
]);

export const localizedProcessNames = Object.freeze({
  "zh-CN": ["浏览器", "音乐播放器", "视频编辑器", "游戏客户端", "云端同步", "代码编辑器", "聊天应用", "资源管理器", "分布式工作负载分析服务"],
  "zh-TW": ["瀏覽器", "音樂播放器", "影片編輯器", "遊戲用戶端", "雲端同步", "程式碼編輯器", "聊天應用程式", "資源管理器", "分散式工作負載分析服務"],
  "en-US": ["Browser", "Music Player", "Video Editor", "Game Client", "Cloud Sync", "Code Editor", "Chat Application", "Resource Manager", "Distributed Workload Analysis Service"],
  "ja-JP": ["ブラウザー", "音楽プレーヤー", "動画編集", "ゲームクライアント", "クラウド同期", "コードエディター", "チャットアプリ", "リソースマネージャー", "分散ワークロード分析サービス"],
  "ko-KR": ["브라우저", "음악 플레이어", "동영상 편집기", "게임 클라이언트", "클라우드 동기화", "코드 편집기", "채팅 앱", "리소스 관리자", "분산 워크로드 분석 서비스"],
  "fr-FR": ["Navigateur", "Lecteur multimédia", "Éditeur vidéo", "Client de jeu", "Synchronisation cloud", "Éditeur de code", "Application de messagerie", "Gestionnaire de ressources", "Service d’analyse des charges distribuées"],
  "de-DE": ["Browser", "Musikplayer", "Videoeditor", "Spielclient", "Cloud-Synchronisierung", "Code-Editor", "Chat-Anwendung", "Ressourcenverwaltung", "Dienst zur Analyse verteilter Arbeitslasten"],
  "es-ES": ["Navegador", "Reproductor de música", "Editor de vídeo", "Cliente de juego", "Sincronización en la nube", "Editor de código", "Aplicación de chat", "Administrador de recursos", "Servicio de análisis de cargas distribuidas"],
  "ru-RU": ["Браузер", "Музыкальный плеер", "Видеоредактор", "Игровой клиент", "Облачная синхронизация", "Редактор кода", "Чат-приложение", "Диспетчер ресурсов", "Служба анализа распределённых нагрузок"]
});

const metricCatalogPath = fileURLToPath(new URL("../fixtures/metricFieldsWire/catalog.json", import.meta.url));
const metricFieldDeviceSamples = JSON.parse(readFileSync(metricCatalogPath, "utf8"));
const engineClasses = ["3D", "Copy", "Compute", "Video Decode", "Video Processing", "Other"];
const unitBytes = 1024 ** 3;

function hashSeed(seed) {
  const text = String(seed ?? 1);
  let hash = 2166136261;
  for (let index = 0; index < text.length; index++) {
    hash ^= text.charCodeAt(index);
    hash = Math.imul(hash, 16777619);
  }
  return hash >>> 0 || 0x9e3779b9;
}

function createRandom(seed) {
  let state = hashSeed(seed);
  return () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let value = state;
    value = Math.imul(value ^ (value >>> 15), value | 1);
    value ^= value + Math.imul(value ^ (value >>> 7), value | 61);
    return ((value ^ (value >>> 14)) >>> 0) / 4294967296;
  };
}

const integer = (random, min, max) => min + Math.floor(random() * (max - min + 1));
const pick = (random, values, fallback) => values?.length ? values[integer(random, 0, values.length - 1)] : fallback;
const isRecord = value => value !== null && typeof value === "object" && !Array.isArray(value);

function normalizeMode(mode) {
  if (mode === true) return { name: "realistic-extreme", multiplier: 2 };
  if (mode === false || mode === undefined || mode === null || mode === "realistic") return { name: "realistic", multiplier: 2 };
  if (typeof mode === "string") {
    if (["realistic-extreme", "processes-1000", "longest-names", "gpus-4"].includes(mode)) return { name: mode, multiplier: 2 };
    throw new Error(`Unknown generated scenario mode: ${mode}`);
  }
  if (isRecord(mode)) {
    const name = mode.mode ?? (mode.extreme ? "realistic-extreme" : "realistic");
    const normalized = normalizeMode(name);
    const multiplier = mode.multiplier ?? mode.extremeMultiplier ?? normalized.multiplier;
    if (!Number.isFinite(multiplier) || multiplier < 1) throw new Error("Extreme multiplier must be a finite number greater than or equal to 1.");
    return { ...normalized, multiplier };
  }
  throw new Error("Scenario mode must be a named mode, boolean, or mode options object.");
}

function numericSamples(summary) {
  if (!isRecord(summary)) return [];
  if (Array.isArray(summary.samples)) return summary.samples.filter(value => typeof value === "number" && Number.isFinite(value));
  if (Array.isArray(summary.numericSamples)) return summary.numericSamples.filter(value => typeof value === "number" && Number.isFinite(value));
  return [];
}

function sampleMetric(metrics, id, random, fallback = null) {
  const definition = metrics?.[id];
  if (!definition) return fallback;
  const availableSamples = numericSamples(definition);
  const unavailableSamples = Number.isInteger(definition.nullSamples) && definition.nullSamples > 0 ? definition.nullSamples : 0;
  const hasIntegerSamples = Array.isArray(definition.integerSamples) && definition.integerSamples.length > 0;
  if (!availableSamples.length && !hasIntegerSamples && (definition.status === "observed-unavailable" || unavailableSamples > 0)) {
    return {
      numericValue: null,
      ...(definition.unit ? { unit: definition.unit } : {}),
      status: "unavailable",
      unavailableReason: definition.unavailableReason ?? definition.unavailableReasons?.[0] ?? "gpu-dedicated-memory-total-unavailable"
    };
  }
  if (availableSamples.length && unavailableSamples > 0
    && random() < unavailableSamples / (unavailableSamples + availableSamples.length)) {
    return {
      numericValue: null,
      ...(definition.unit ? { unit: definition.unit } : {}),
      status: "unavailable",
      unavailableReason: definition.unavailableReason ?? definition.unavailableReasons?.[0] ?? "system-total-unavailable"
    };
  }
  const samples = availableSamples;
  const sampleIndex = samples.length ? integer(random, 0, samples.length - 1) : -1;
  const value = sampleIndex >= 0 ? samples[sampleIndex] : fallback?.numericValue ?? null;
  if (value === null && Array.isArray(definition.integerSamples) && definition.integerSamples.length) {
    const integerValue = pick(random, definition.integerSamples, null);
    return { numericValue: null, integerValue, ...(definition.unit ? { unit: definition.unit } : {}) };
  }
  const result = { numericValue: value, ...(definition.unit ? { unit: definition.unit } : {}) };
  const percentValues = Array.isArray(definition.percentSamples) ? definition.percentSamples.filter(Number.isFinite) : [];
  if (percentValues.length) result.percent = percentValues.length === samples.length && sampleIndex >= 0
    ? percentValues[sampleIndex] : pick(random, percentValues, null);
  else if (definition.unit === "%" && value !== null) result.percent = value;
  if (value === null && fallback) return { ...fallback };
  return result;
}

function sampleCount(profile, key, random, fallback) {
  const summary = profile?.[key];
  const samples = Array.isArray(summary?.samples) ? summary.samples.filter(value => Number.isInteger(value) && value >= 0) : [];
  if (samples.length) return pick(random, samples, fallback);
  const min = summary?.range?.min;
  const max = summary?.range?.max;
  if (Number.isInteger(min) && Number.isInteger(max) && min >= 0 && max >= min) return integer(random, min, max);
  return fallback;
}

function processCountRange(profile) {
  const summary = profile?.processCounts;
  const values = Array.isArray(summary?.samples) ? summary.samples.filter(value => Number.isInteger(value) && value >= 0) : [];
  const min = Number.isInteger(summary?.range?.min) ? summary.range.min : values.length ? Math.min(...values) : 33;
  const max = Number.isInteger(summary?.range?.max) ? summary.range.max : values.length ? Math.max(...values) : 80;
  return { min: Math.max(1, min), max: Math.max(Math.max(1, min), max) };
}

function histogramLengths(profile, language) {
  const summary = profile?.nameLengthHistograms?.[language];
  const values = Array.isArray(summary?.samples) ? summary.samples.filter(value => Number.isInteger(value) && value > 0) : [];
  if (values.length) return { samples: values, source: summary.observed ? "observed-locale" : `profile-${summary.status ?? "estimated"}` };
  const ambiguousBucket = language === "en-US" || language === "fr-FR" || language === "de-DE" || language === "es-ES"
    ? "latin-ambiguous"
    : language === "zh-CN" || language === "zh-TW" || language === "ja-JP" ? "han-ambiguous" : null;
  const ambiguous = ambiguousBucket ? profile?.unclassifiedNameLengthHistograms?.[ambiguousBucket] : null;
  const ambiguousSamples = Array.isArray(ambiguous?.samples) ? ambiguous.samples.filter(value => Number.isInteger(value) && value > 0) : [];
  if (ambiguousSamples.length) return { samples: ambiguousSamples, source: `ambiguous-fallback:${ambiguousBucket}` };
  const catalog = localizedProcessNames[language] ?? localizedProcessNames["en-US"];
  return { samples: catalog.map(name => [...name].length), source: "estimated-synthetic-catalog" };
}

function nameForLength(language, target, random, ordinal, longest = false) {
  const catalog = localizedProcessNames[language] ?? localizedProcessNames["en-US"];
  const sorted = catalog.map(name => ({ name, length: [...name].length })).sort((left, right) => left.length - right.length);
  const maximumLength = Math.max(...sorted.map(item => item.length));
  const wanted = longest ? Math.max(target ?? 0, maximumLength) : target;
  if (wanted === null || wanted === undefined) return catalog[integer(random, 0, catalog.length - 2)];
  const exact = sorted.filter(item => item.length === wanted);
  if (exact.length) return exact[integer(random, 0, exact.length - 1)].name;
  const nearest = sorted.filter(item => item.name !== catalog.at(-1)).sort((left, right) => Math.abs(left.length - wanted) - Math.abs(right.length - wanted))[0] ?? sorted.at(-1);
  if (wanted <= nearest.length) return nearest.name;
  let value = longest ? sorted.at(-1).name : nearest.name;
  const extension = {
    "zh-CN": " 服务分析", "zh-TW": " 服務分析", "ja-JP": " 分析サービス", "ko-KR": " 분석 서비스",
    "fr-FR": " Service d’analyse", "de-DE": " Analyse-Service", "es-ES": " Servicio de análisis",
    "ru-RU": " Служба анализа", "en-US": " Analysis Service"
  }[language] ?? " Analysis Service";
  let extensionIndex = 0;
  while ([...value].length < wanted) {
    const points = [...extension];
    value += points[extensionIndex % points.length];
    extensionIndex++;
  }
  return value;
}

function makeNames(count, random, mode, profile) {
  const start = integer(random, 0, scenarioLanguages.length - 1);
  const namesByLanguage = {};
  const nameLengthSourcesByLanguage = {};
  for (const language of scenarioLanguages) {
    const lengthProfile = histogramLengths(profile, language);
    const lengths = lengthProfile.samples;
    nameLengthSourcesByLanguage[language] = lengthProfile.source;
    const maximum = Math.max(...lengths);
    namesByLanguage[language] = Array.from({ length: count }, (_, index) => {
      const wanted = mode === "longest-names" ? maximum : pick(random, lengths, null);
      return nameForLength(language, wanted, random, index + 1, mode === "longest-names");
    });
  }
  const nameLanguages = Array.from({ length: count }, (_, index) => scenarioLanguages[(index + start) % scenarioLanguages.length]);
  const names = nameLanguages.map((language, index) => namesByLanguage[language][index]);
  return { names, nameLanguages, namesByLanguage, nameLengthSourcesByLanguage };
}

function makeWeights(count, random) {
  if (!count) return [];
  const values = Array.from({ length: count }, (_, index) => (0.9 + random() * 0.2) / (index + 1) ** 1.15).sort((left, right) => right - left);
  const total = values.reduce((sum, value) => sum + value, 0);
  const weights = values.map(value => value / total);
  if (weights.length > 1) weights[weights.length - 1] = 1 - weights.slice(0, -1).reduce((sum, value) => sum + value, 0);
  return weights;
}

function makeGpuRecords(count, profile, random) {
  const estimates = Array.isArray(profile?.gpuVramBytes?.samples) ? profile.gpuVramBytes.samples : [];
  const usages = numericSamples(profile?.gpuUsagePercent);
  const metrics = profile?.metrics ?? {};
  const gpus = [];
  const engines = [];
  for (let index = 0; index < count; index++) {
    const engineCount = sampleCount(profile, "engineCountsPerGpu", random, 4);
    const metricUsage = sampleMetric(metrics, `gpu.${index}.usage`, random, null);
    const usagePercent = Number.isFinite(metricUsage?.percent) ? metricUsage.percent
      : Number.isFinite(metricUsage?.numericValue) ? metricUsage.numericValue : pick(random, usages, 28 + random() * 42);
    const vramId = `gpu.${index}.vram`;
    const metricVram = sampleMetric(metrics, vramId, random, null);
    const hasVramMetric = Object.hasOwn(metrics, vramId);
    const vramBytes = metricVram?.status === "unavailable" || (hasVramMetric && !Number.isFinite(metricVram?.numericValue)) ? null
      : Number.isFinite(metricVram?.numericValue) ? metricVram.numericValue
        : pick(random, estimates, index === 0 ? 8 * unitBytes : null);
    const name = index === 0 ? "Discrete GPU 0" : index === 1 ? "Integrated GPU 1" : `GPU ${index}`;
    gpus.push({ index, name, engineCount, vramBytes, usagePercent });
    for (let engineIndex = 0; engineIndex < engineCount; engineIndex++) {
      const engineClass = engineClasses[engineIndex % engineClasses.length];
      engines.push({ gpuIndex: index, index: engineIndex, name: engineClass, engineClass,
        usagePercent: Math.round((4 + random() * 74) * 10) / 10 });
    }
  }
  return { gpus, engines };
}

function patchDeviceAddress(address, occurrence) {
  if (!isRecord(address) || occurrence === 0) return address;
  const next = structuredClone(address);
  if (Number.isInteger(next.groupRelativeIndex)) next.groupRelativeIndex += occurrence;
  if (Number.isInteger(next.nodeNumber)) next.nodeNumber += occurrence;
  if (Number.isInteger(next.interfaceIndex)) next.interfaceIndex += occurrence;
  if (Number.isInteger(next.physicalDiskDeviceId)) next.physicalDiskDeviceId += occurrence;
  if (typeof next.sensorIdentifier === "string") next.sensorIdentifier = `${next.sensorIdentifier}/generated-${occurrence}`;
  if (typeof next.pnpInstanceId === "string") next.pnpInstanceId = `${next.pnpInstanceId}-generated-${occurrence}`;
  if (typeof next.adapterLuid === "string") next.adapterLuid = `${next.adapterLuid.slice(0, -2)}${String(occurrence + 1).padStart(2, "0")}`;
  if (typeof next.interfaceGuid === "string") next.interfaceGuid = `${next.interfaceGuid.slice(0, -2)}${String(occurrence + 1).padStart(2, "0")}`;
  if (typeof next.uniqueId === "string") next.uniqueId = `${next.uniqueId}-${occurrence}`;
  if (typeof next.serialNumber === "string") next.serialNumber = `${next.serialNumber}-${occurrence}`;
  return next;
}

function makeDevices(profile, random) {
  const configuredKinds = Array.isArray(profile?.deviceKinds) ? profile.deviceKinds.filter(kind => typeof kind === "string") : [];
  const kinds = configuredKinds.length ? configuredKinds : metricFieldDeviceSamples.map(item => item.device.kind);
  const count = Math.max(0, sampleCount(profile, "deviceCounts", random, kinds.length));
  if (!count || !kinds.length) return [];
  const shuffled = [...kinds];
  const offset = integer(random, 0, shuffled.length - 1);
  const selectedKinds = Array.from({ length: count }, (_, index) => shuffled[(index + offset) % shuffled.length]);
  const occurrences = new Map();
  return selectedKinds.map(kind => {
    const occurrence = occurrences.get(kind) ?? 0;
    occurrences.set(kind, occurrence + 1);
    const template = profile.deviceTemplates?.[kind] ?? metricFieldDeviceSamples.find(item => item.device?.kind === kind) ?? null;
    const item = template ? structuredClone(template) : {
      deviceIdentity: `generated-${kind}`,
      fields: [],
      device: { kind, address: { kind }, displayName: null, ordinal: occurrence }
    };
    if (!item.device) item.device = { kind, address: { kind }, displayName: null, ordinal: occurrence };
    item.device.kind = kind;
    if (isRecord(item.device.address)) item.device.address.kind = kind;
    item.device.ordinal = occurrence;
    item.device.address = patchDeviceAddress(item.device.address, occurrence) ?? { kind };
    item.deviceIdentity = occurrence === 0 ? template?.deviceIdentity ?? `generated-${kind}`
      : `${template?.deviceIdentity ?? `generated-${kind}`}-${occurrence}`;
    if (occurrence > 0 && typeof item.device.displayName === "string") item.device.displayName = `${item.device.displayName} ${occurrence + 1}`;
    if (!Array.isArray(item.fields)) item.fields = [];
    return item;
  });
}

function intervalSamples(profile) {
  const samples = Array.isArray(profile?.frameIntervalSamplesMs) ? profile.frameIntervalSamplesMs : [];
  return samples.filter(value => typeof value === "number" && Number.isFinite(value) && value > 0);
}

function buildMetrics(profile, random, gpus) {
  const metrics = {};
  for (const id of Object.keys(profile?.metrics ?? {}).sort()) {
    const sampled = sampleMetric(profile.metrics, id, random, null);
    if (sampled) metrics[id] = sampled;
  }
  if (!metrics["cpu.usage"]) metrics["cpu.usage"] = { numericValue: 12, percent: 12, unit: "%" };
  if (!metrics["memory.usage"]) metrics["memory.usage"] = { numericValue: 8 * unitBytes, percent: 25, unit: "B" };
  for (const gpu of gpus) {
    const usageId = `gpu.${gpu.index}.usage`;
    const vramId = `gpu.${gpu.index}.vram`;
    if (!metrics[usageId]) metrics[usageId] = { numericValue: gpu.usagePercent, percent: gpu.usagePercent, unit: "%" };
    if (!metrics[vramId]) metrics[vramId] = { numericValue: gpu.vramBytes, unit: "B" };
  }
  return metrics;
}

function makeMetricUpdates(profile, metrics, seed) {
  const random = createRandom(`${seed ?? 1}:metric-updates`);
  return Array.from({ length: 8 }, (_, frameIndex) => Object.fromEntries(Object.entries(metrics).map(([id, base]) => {
    if (base.status === "unavailable" || base.numericValue === null) return [id, { ...base }];
    const definition = profile?.metrics?.[id];
    const samples = numericSamples(definition);
    const sourceValue = samples.length ? samples[frameIndex % samples.length] : base.numericValue;
    if (!Number.isFinite(sourceValue)) return [id, { ...base }];
    const range = definition?.range;
    const spread = Number.isFinite(range?.max) && Number.isFinite(range?.min) && range.max > range.min
      ? range.max - range.min
      : Math.max(Math.abs(sourceValue) * (base.unit === "%" ? 0.06 : 0.025), base.unit === "B" ? 32 * 1024 ** 2 : 0.5);
    const phase = (frameIndex / 8) * Math.PI * 2 + random() * Math.PI * 0.25;
    const proposed = Math.max(0, sourceValue + Math.sin(phase) * spread * 0.12 + (random() - 0.5) * spread * 0.04);
    const value = Number.isFinite(range?.min) && Number.isFinite(range?.max)
      ? Math.max(range.min, Math.min(range.max, proposed)) : proposed;
    const update = { ...base, numericValue: Math.round(value * 100) / 100 };
    if (base.unit === "%") update.percent = update.numericValue;
    return [id, update];
  })));
}

// Seed plus profile determine the entire mock dataset; this only returns data for the existing mock server.
export function generateScenario(profile, seed, mode = "realistic") {
  if (!isRecord(profile) || profile.schemaVersion !== 1) throw new Error("A schemaVersion 1 profile is required.");
  const selectedMode = normalizeMode(mode);
  const random = createRandom(seed);
  const range = processCountRange(profile);
  let processCount = profile.source === "learned"
    ? Math.max(1, sampleCount(profile, "processCounts", random, integer(random, range.min, range.max)))
    : integer(random, range.min, range.max);
  if (selectedMode.name === "realistic-extreme") processCount = Math.ceil(range.max * selectedMode.multiplier);
  if (selectedMode.name === "processes-1000") processCount = 1000;
  if (selectedMode.name === "longest-names") processCount = Math.max(range.min, Math.min(range.max, 33));
  const gpuCount = selectedMode.name === "gpus-4" ? 4 : Math.max(1, sampleCount(profile, "gpuCounts", random, 2));
  const { names, nameLanguages, namesByLanguage, nameLengthSourcesByLanguage } = makeNames(processCount, random, selectedMode.name, profile);
  const weights = makeWeights(processCount, random);
  const { gpus, engines } = makeGpuRecords(gpuCount, profile, random);
  const metrics = buildMetrics(profile, random, gpus);
  const metricUpdates = makeMetricUpdates(profile, metrics, seed);
  const intervalsMs = intervalSamples(profile);
  const devices = makeDevices(profile, random);
  return {
    scenarioPatch: { resourceTableVariant: "many", resourceTableRowCount: processCount },
    generatedData: {
      names,
      nameLanguages,
      namesByLanguage,
      nameLengthSourcesByLanguage,
      weights,
      gpus,
      engines,
      devices,
      intervalsMs,
      metrics,
      metricUpdates
    },
    properties: {
      seed: seed ?? 1,
      mode: selectedMode.name,
      processCount,
      gpuCount,
      engineCount: engines.length,
      deviceCount: devices.length,
      profileSource: profile.source
    }
  };
}

export async function writeScenarioBatch({ profile, count, outputDir, seedStart = 1, mode = "realistic" }) {
  if (!Number.isInteger(count) || count < 1) throw new Error("Batch count must be a positive integer.");
  if (!outputDir) throw new Error("An output directory is required.");
  const destination = resolve(outputDir);
  await mkdir(destination, { recursive: true });
  const written = [];
  for (let index = 0; index < count; index++) {
    const seed = seedStart + index;
    const result = generateScenario(profile, seed, mode);
    const path = resolve(destination, `scenario-${String(seed).padStart(6, "0")}.json`);
    await writeFile(path, `${JSON.stringify(result, null, 2)}\n`, "utf8");
    written.push(path);
  }
  return written;
}

function parseArgs(args) {
  const options = { count: null, outputDir: null, seedStart: 1, mode: "realistic", profilePath: null };
  for (let index = 0; index < args.length; index++) {
    const key = args[index];
    const value = args[index + 1];
    if (key === "--count" || key === "-n") { options.count = Number(value); index++; }
    else if (key === "--out") { options.outputDir = value; index++; }
    else if (key === "--seed-start") { options.seedStart = Number(value); index++; }
    else if (key === "--mode") { options.mode = value; index++; }
    else if (key === "--profile") { options.profilePath = value; index++; }
    else if (key === "--help" || key === "-h") options.help = true;
    else throw new Error(`Unknown argument: ${key}`);
  }
  return options;
}

async function runCli(args) {
  const options = parseArgs(args);
  if (options.help) {
    process.stdout.write("Usage: node tests/realData/generateScenario.mjs --count K --out DIR [--seed-start N] [--mode realistic|realistic-extreme|processes-1000|longest-names|gpus-4] [--profile FILE]\n");
    return;
  }
  if (!Number.isInteger(options.seedStart)) throw new Error("Seed start must be an integer.");
  const profilePath = options.profilePath ? resolve(options.profilePath) : new URL("../fixtures/realDataProfile.json", import.meta.url);
  const profile = JSON.parse(await readFile(profilePath, "utf8"));
  const paths = await writeScenarioBatch({ ...options, profile });
  process.stdout.write(`Wrote ${paths.length} scenario files to ${resolve(options.outputDir)}\n`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  runCli(process.argv.slice(2)).catch(error => {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  });
}
