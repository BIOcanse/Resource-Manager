import assert from "node:assert/strict";
import { mkdtemp, mkdir, readFile } from "node:fs/promises";
import { readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";
import { learnProfileFromSlices, parseLearnerArgs, writeProfileAtomically } from "./learnProfile.ts";
import { generateScenario, scenarioLanguages, writeScenarioBatch } from "./generateScenario.ts";

import type { Profile, Distribution, MetricProfile, GeneratedDevice, GeneratedScenario } from "./generateScenario.ts";
import type { Slice, SliceFrameEvent, SliceResponseEvent } from "./sliceFormat.ts";

type BootstrapProfile = Omit<Profile, "processCounts" | "gpuCounts" | "deviceCounts" | "metrics" | "resourceFactMetricIds" | "resourceFactGpu1Vram" | "deviceKinds" | "frameIntervalSamplesMs" | "nameLengthHistograms"> & {
  processCounts: Required<Distribution>; gpuCounts: Required<Distribution>; deviceCounts: Required<Distribution>;
  metrics: Record<string, MetricProfile & { samples: number[]; integerSamples: string[] }>;
  resourceFactMetricIds: string[]; resourceFactGpu1Vram: NonNullable<Profile["resourceFactGpu1Vram"]>;
  deviceKinds: string[]; frameIntervalSamplesMs: number[];
  nameLengthHistograms: Record<string, Required<Distribution>>;
};
const fixture = <T>(path: string): T => JSON.parse(readFileSync(new URL(`../fixtures/${path}`, import.meta.url), "utf8"));
const bootstrapProfile = fixture<BootstrapProfile>("realDataProfile.json");
const unicodeLength = (value: string) => [...value].length;
const outputRoot = resolve(process.env.RM_REAL_DATA_TEST_OUTPUT
  ?? join(tmpdir(), "rm-real-data-profile-test-output"));

async function createRunDirectory(prefix: string) {
  await mkdir(outputRoot, { recursive: true });
  return mkdtemp(join(outputRoot, `${prefix}-`));
}

function makeSlice(durationMs: number, events: (SliceFrameEvent | Omit<SliceResponseEvent, "status" | "contentType" | "encoding">)[]): Slice {
  return {
    schemaVersion: 1,
    startedAt: "2026-10-26T12:00:00.000Z",
    durationMs,
    complete: true,
    events: events.map(event => event.kind === "response" ? {
      status: 200,
      contentType: "application/json",
      encoding: "json",
      ...event
    } : event)
  };
}

test("bootstrap profile records fixture evidence and labels estimates", () => {
  const accounting = fixture<{ frames: { metricIds: string[] }[] }>("resourceFactsAccounting.pre-r4.real.json");
  const metricCatalog = fixture<GeneratedDevice[]>("metricFieldsWire/catalog.json");
  const metricFrame = fixture<{ value: GeneratedDevice[] }>("metricFieldsWire/subscription-frame.json");
  const metricIds = [...new Set(accounting.frames.flatMap(frame => frame.metricIds))].sort();

  assert.equal(bootstrapProfile.schemaVersion, 1);
  assert.equal(bootstrapProfile.source, "bootstrap");
  assert.deepEqual([...bootstrapProfile.resourceFactMetricIds].sort(), metricIds);
  assert.deepEqual(bootstrapProfile.processCounts.range, { min: 33, max: 80 });
  assert.equal(bootstrapProfile.processCounts.observed, false);
  assert.equal(bootstrapProfile.metrics["memory.usage"].samples[0], 27.4 * 1024 ** 3);
  assert.equal(bootstrapProfile.metrics["memory.usage"].observed, false);
  assert.equal(bootstrapProfile.metrics["gpu.1.vram"].status, "observed-unavailable");
  assert.equal(bootstrapProfile.resourceFactGpu1Vram.totalValue, null);
  assert.equal(bootstrapProfile.resourceFactGpu1Vram.rowCount, 5);
  assert.deepEqual(bootstrapProfile.deviceKinds.sort(), metricCatalog.map(item => item.device.kind).sort());
  assert.equal(bootstrapProfile.deviceCounts.samples[0], metricCatalog.length);

  for (const device of metricFrame.value) {
    for (const field of device.fields) {
      const id = `${field.name}@${device.deviceIdentity}`;
      const expected = field.integerValue ?? field.numericValue;
      const metric = bootstrapProfile.metrics[id];
      assert.ok(metric, `profile has ${id}`);
      if (typeof expected === "number") assert.equal(metric.samples[0], expected, id);
      else assert.equal(metric.integerSamples[0], expected, id);
    }
  }

  assert.equal(bootstrapProfile.metrics["npu_all_engine_busy_percent@wire-npu"].samples[0], 125.5,
    "fixture value above 100 percent remains unchanged");
  assert.equal(bootstrapProfile.gpuCounts.samples[0], 2);
  assert.equal(bootstrapProfile.gpuCounts.observed, false);
  assert.deepEqual(bootstrapProfile.frameIntervalSamplesMs.length, 2);
  assert.ok(bootstrapProfile.frameIntervalSamplesMs.every(value => value > 0));

  const provenanceText = JSON.stringify(bootstrapProfile.provenance);
  assert.match(provenanceText, /user brief \(2026-10-26\)/);
  assert.match(provenanceText, /not independently verified/);
  assert.match(provenanceText, /mock-capacity/);
  assert.match(provenanceText, /checked-unmatched/);

  assert.deepEqual(Object.keys(bootstrapProfile.nameLengthHistograms).sort(), [...scenarioLanguages].sort());
  for (const language of scenarioLanguages) {
    const names = bootstrapProfile.nameLengthHistograms[language];
    assert.equal(names.observed, false, language);
    assert.equal(names.status, "estimated", language);
    assert.equal(Object.values(names.histogram).reduce((sum, count) => sum + count, 0), names.sampleCount, language);
  }
});

test("learner aggregates multiple validated slices and summarizes process, device, metric, and frame samples", () => {
  const gpuDevice = {
    deviceIdentity: "wire-gpu-test",
    fields: [],
    device: { kind: "gpu_adapter", address: { kind: "gpu_adapter", pnpInstanceId: "GPU-TEST" }, displayName: "Test GPU", ordinal: 0 }
  };
  const makeFacts = (processIds: number[], cpuValue: number, withTopology = false) => ({
    version: 2,
    processes: processIds.map(processId => ({ processId, name: `process-${processId}.exe` })),
    namesByLanguage: { "zh-CN": ["系统", "浏览器"], "en-US": ["System", "Browser"] },
    ...(withTopology ? {
      devices: [gpuDevice],
      gpus: [{ index: 0, engines: [{}, {}] }, { index: 1, engines: [{}] }]
    } : {}),
    metrics: [
      { metricId: "cpu.usage", unit: "%", totalValue: cpuValue, totalSystemPercent: cpuValue },
      { metricId: "gpu.0.usage", unit: "%", numericValue: 40, percent: 40 },
      { metricId: "gpu.1.vram", unit: "B", totalValue: null, unavailableReason: "gpu-dedicated-memory-total-unavailable" }
    ]
  });
  const makeFrame = (atMs: number, body: unknown): SliceFrameEvent => ({ atMs, kind: "frame", path: "/api/metrics/subscribe?ids=cpu.usage&intervalMs=1000", body });
  const slices = [
    makeSlice(3000, [
      { atMs: 0, kind: "response", path: "/api/resource-facts/snapshot", body: makeFacts([10, 11], 20, true) },
      makeFrame(1000, { metrics: [{ id: "cpu.usage", unit: "%", numericValue: 25, percent: 25 }] }),
      { atMs: 2000, kind: "frame", path: "/api/resource-facts/subscribe?items=process.inventory", body: makeFacts([10, 11, 12], 40) },
      makeFrame(2000, { metrics: [{ id: "cpu.usage", unit: "%", numericValue: 45, percent: 45 }] }),
      makeFrame(2600, { metrics: [{ id: "cpu.usage", unit: "%", numericValue: 50, percent: 50 }] })
    ]),
    makeSlice(1800, [
      makeFrame(400, { metrics: [{ id: "cpu.usage", unit: "%", numericValue: 60, percent: 60 }] }),
      makeFrame(1400, { metrics: [{ id: "cpu.usage", unit: "%", numericValue: 65, percent: 65 }] })
    ])
  ];

  const profile = learnProfileFromSlices(slices, ["slice-one", "slice-two"]);
  assert.equal(profile.schemaVersion, 1);
  assert.equal(profile.source, "learned");
  assert.deepEqual(profile.processCounts.samples, [2, 3]);
  assert.deepEqual(profile.metrics["cpu.usage"].samples, [20, 25, 40, 45, 50, 60, 65]);
  assert.equal(profile.metrics["cpu.usage"].range.min, 20);
  assert.equal(profile.metrics["cpu.usage"].range.max, 65);
  assert.deepEqual(profile.metrics["gpu.1.vram"].status, "observed-unavailable");
  assert.equal(profile.metrics["gpu.1.vram"].nullSamples, 2);
  assert.ok(profile.gpuCounts.samples.includes(2));
  assert.ok(profile.deviceCounts.samples.includes(1));
  assert.ok(profile.engineCountsPerGpu.samples.includes(2));
  assert.ok(profile.deviceKinds.includes("gpu_adapter"));
  assert.ok(profile.deviceTemplates["gpu_adapter"]);
  assert.deepEqual(profile.nameLengthHistograms["zh-CN"].histogram, { 2: 2, 3: 2 });
  assert.deepEqual(profile.nameLengthHistograms["en-US"].histogram, { 6: 2, 7: 2 });
  assert.ok(profile.unclassifiedNameLengthHistograms["latin-ambiguous"].samples.length > 0);
  assert.ok(profile.provenance.nameLanguageInference.includes("Latin and Han names remain"));
  assert.deepEqual(profile.frameIntervalSamplesMs, [1000, 600, 1000]);
  assert.equal(profile.provenance.sliceDirectories.length, 2);
});

test("process inventory PIDs take precedence over duplicated per-metric process indexes", () => {
  const processIds = Array.from({ length: 33 }, (_, index) => 7000 + index);
  const processReferences = processIds.map((_, processIndex) => ({ processIndex, value: 1 }));
  const profile = learnProfileFromSlices([makeSlice(100, [{ atMs: 10, kind: "frame", path: "/api/resource-facts/subscribe?items=process.inventory", body: {
      processes: [...processIds.map(processId => ({ processId, name: `worker-${processId}.exe` })), { processId: -1, name: "" }],
      metrics: [
        { metricId: "cpu.usage", unit: "%", totalValue: 32, software: [{ processes: processReferences }] },
        { metricId: "memory.usage", unit: "B", totalValue: 1024, software: [{ processes: processReferences }] }
      ]
    } }])]);
  assert.deepEqual(profile.processCounts.samples, [33]);
  assert.equal(profile.unclassifiedNameLengthHistograms["latin-ambiguous"].sampleCount, 33);
});

test("learner CLI arguments and atomic profile writing work with an in-memory learned profile", async () => {
  const slice = makeSlice(100, [{ atMs: 10, kind: "response", path: "/api/metrics/snapshot", body: {
      metrics: [{ id: "cpu.usage", unit: "%", numericValue: 17, percent: 17 }]
    } }]);
  const profile = learnProfileFromSlices([slice], ["synthetic-cli-slice"]);
  const root = await createRunDirectory("profile-writer");
  const outputPath = join(root, "learned.json");
  const parsed = parseLearnerArgs(["--out", outputPath, "slice-one", "slice-two"]);
  assert.deepEqual(parsed, { outputPath, sliceDirectories: ["slice-one", "slice-two"], help: false });
  assert.throws(() => parseLearnerArgs(["slice-one"]), /Usage:/);

  const writtenPath = await writeProfileAtomically(outputPath, profile);
  assert.equal(writtenPath, outputPath);
  const written: ReturnType<typeof learnProfileFromSlices> = JSON.parse(await readFile(writtenPath, "utf8"));
  assert.equal(written.schemaVersion, 1);
  assert.equal(written.source, "learned");
  assert.deepEqual(written.metrics["cpu.usage"].samples, [17]);
});

test("longest-name generation reuses ambiguous Latin observations without claiming a locale observation", () => {
  const profile = learnProfileFromSlices([makeSlice(20, [{ atMs: 10, kind: "response", path: "/api/resource-facts/snapshot", body: {
      processes: [{ processId: 8100, name: `A${"x".repeat(299)}` }]
    } }])]);
  const generated = generateScenario(profile, "long-latin", "longest-names");
  for (const language of ["en-US", "fr-FR", "de-DE", "es-ES"] as const) {
    assert.equal(profile.nameLengthHistograms[language].observed, false, language);
    assert.equal(generated.generatedData.nameLengthSourcesByLanguage[language], "ambiguous-fallback:latin-ambiguous", language);
    assert.ok(generated.generatedData.namesByLanguage[language].every(name => unicodeLength(name) >= 300), language);
  }
});

test("scenario generation is deterministic and emits a multilingual weighted dataset", () => {
  const first = generateScenario(bootstrapProfile, "seed-19", "realistic");
  const second = generateScenario(bootstrapProfile, "seed-19", "realistic");
  assert.deepEqual(first, second);
  assert.equal(first.scenarioPatch.resourceTableVariant, "many");
  assert.ok(first.scenarioPatch.resourceTableRowCount >= 33 && first.scenarioPatch.resourceTableRowCount <= 80);
  assert.equal(first.generatedData.names.length, first.properties.processCount);
  assert.equal(first.generatedData.weights.length, first.properties.processCount);
  assert.ok(Math.abs(first.generatedData.weights.reduce((sum, value) => sum + value, 0) - 1) < 1e-12);
  assert.ok(Math.max(...first.generatedData.weights) > Math.min(...first.generatedData.weights) * 5);
  assert.deepEqual([...new Set(first.generatedData.nameLanguages.slice(0, 9))].sort(), [...scenarioLanguages].sort());
  assert.deepEqual(Object.keys(first.generatedData.namesByLanguage).sort(), [...scenarioLanguages].sort());
  assert.equal(first.generatedData.metricUpdates.length, 8);
  assert.ok(new Set(first.generatedData.metricUpdates.map(frame => frame["cpu.usage"].numericValue)).size > 1);
  assert.equal(first.generatedData.metrics["npu_all_engine_busy_percent@wire-npu"].numericValue, 125.5);
  assert.equal(first.generatedData.metrics["gpu.1.vram"].status, "unavailable");
  assert.equal(first.generatedData.gpus.find(gpu => gpu.index === 1)!.vramBytes, null);
  assert.equal(first.generatedData.devices.length, 7);
  assert.ok(first.generatedData.devices.every(device => device.deviceIdentity && device.device.kind === device.device.address!.kind));
});

test("named scale modes cover extreme process, long-name, GPU, and batch cases", async () => {
  const extreme = generateScenario(bootstrapProfile, 4, "realistic-extreme");
  assert.equal(extreme.scenarioPatch.resourceTableRowCount, 160);
  assert.equal(generateScenario(bootstrapProfile, 4, { mode: "realistic-extreme", multiplier: 3 }).properties.processCount, 240);

  const thousand = generateScenario(bootstrapProfile, 5, "processes-1000");
  assert.equal(thousand.scenarioPatch.resourceTableRowCount, 1000);
  assert.equal(thousand.generatedData.names.length, 1000);

  const longest = generateScenario(bootstrapProfile, 6, "longest-names");
  for (const language of scenarioLanguages) {
    const lengths = longest.generatedData.namesByLanguage[language].map(unicodeLength);
    assert.equal(new Set(lengths).size, 1, language);
    assert.equal(lengths[0], bootstrapProfile.nameLengthHistograms[language].range.max, language);
  }

  const fourGpus = generateScenario(bootstrapProfile, 7, "gpus-4");
  assert.equal(fourGpus.generatedData.gpus.length, 4);
  assert.equal(fourGpus.properties.gpuCount, 4);

  const root = await createRunDirectory("profile-batch");
  const output = join(root, "scenarios");
  const paths = await writeScenarioBatch({ profile: bootstrapProfile, count: 3, outputDir: output, seedStart: 20, mode: "realistic" });
  assert.equal(paths.length, 3);
  assert.deepEqual(paths.map(path => path.split(/[\\/]/).at(-1)), ["scenario-000020.json", "scenario-000021.json", "scenario-000022.json"]);
  const files = await Promise.all(paths.map(async (path): Promise<GeneratedScenario> => JSON.parse(await readFile(path, "utf8"))));
  assert.deepEqual(files.map(file => file.properties.seed), [20, 21, 22]);
});
