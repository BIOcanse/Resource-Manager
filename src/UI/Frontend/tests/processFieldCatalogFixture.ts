/*
 * 进程字段目录的 wire（GET /api/resource-breakdown/fields/catalog），按后端 ProcessMetricFieldRegistry 与
 * ProcessGpuResourceFieldRegistry（metric-source round 4）逐项写出：System.Text.Json 写全部公开属性，空值写 null。
 * supportsResourceFacts = resourceFactTotalMetricId 不为 null；additiveAcrossProcesses 是后端的声明（没有声明为 null）。
 */
type Reading = { resource: string; quantity: string } | null;
type GpuEngine = { adapterLuid: number; physicalIndex: number; engineIndex: number } | null;

export function catalogField(name: string, valueKind: string, unit: string, sourceId: string, facts: {
  reading?: Reading; total?: string | null; additive?: boolean | null; gpuEngine?: GpuEngine; engineClass?: string | null; sourceFieldName?: string | null;
} = {}) {
  const total = facts.total ?? null;
  return { name, valueKind, unit, sourceId, resource: "process", vendor: "all", supportsResourceFacts: total !== null,
    gpuEngine: facts.gpuEngine ?? null, engineClass: facts.engineClass ?? null, sourceFieldName: facts.sourceFieldName ?? null,
    additiveAcrossProcesses: facts.additive ?? null, resourceFactTotalMetricId: total, reading: facts.reading ?? null };
}

export const gpuEngineField = (gpu: number, luid: number, physical: number, engine: number, engineClass: string | null = null) =>
  catalogField(`process_all_gpu_residency_attributed_gpu_${gpu}_luid_${luid.toString(16).padStart(16, "0")}_physical_${physical}_engine_${engine}`,
    "float64", "%", "windows.pdh.gpu-engine", { reading: { resource: "gpu", quantity: "engine_busy_percent" }, total: `gpu.${gpu}.usage`,
      additive: true, gpuEngine: { adapterLuid: luid, physicalIndex: physical, engineIndex: engine }, engineClass,
      sourceFieldName: "process_all_gpu_residency_attributed" });

const residentSources = {
  dedicated_resident_apportioned: "process_all_gpu_resident_bytes_attributed",
  dedicated_resident_private: "process_all_gpu_resident_private_bytes_attributed",
  dedicated_resident_shared_apportioned: "process_all_gpu_resident_shared_bytes_attributed"
} as const;

export const gpuResidentField = (gpu: number, luid: number, quantity: keyof typeof residentSources) =>
  catalogField(`${residentSources[quantity]}_gpu_${gpu}_luid_${luid.toString(16).padStart(16, "0")}`, "float64", "B", "windows.etw.gpu-allocation",
    { reading: { resource: "gpu", quantity }, total: `gpu.${gpu}.vram`, additive: true, sourceFieldName: residentSources[quantity] });

/** 静态字段（ProcessMetricFieldRegistry.Definitions 里与资源事实有关的部分，加两个不能作资源事实的对照）。 */
export function staticProcessFieldCatalog() {
  return [
    catalogField("process_all_network_receive_bytes_attributed", "unsigned64", "B", "windows.etw.network", { total: "network.traffic", additive: true }),
    catalogField("process_all_network_send_bytes_attributed", "unsigned64", "B", "windows.etw.network", { total: "network.traffic", additive: true }),
    catalogField("process_all_network_receive_rate_attributed", "float64", "B/s", "windows.etw.network", { total: "network.traffic", additive: true }),
    catalogField("process_all_network_send_rate_attributed", "float64", "B/s", "windows.etw.network", { total: "network.traffic", additive: true }),
    catalogField("process_all_physical_disk_read_bytes_attributed", "unsigned64", "B", "windows.etw.physical-disk", { total: "disk.io", additive: true }),
    catalogField("process_all_physical_disk_write_bytes_attributed", "unsigned64", "B", "windows.etw.physical-disk", { total: "disk.io", additive: true }),
    catalogField("process_all_gpu_residency_attributed", "float64", "%", "windows.pdh.gpu-engine"),
    catalogField("process_all_cpu_time_actual", "float64", "s", "windows.process.query", { additive: true }),
    catalogField("process_all_working_set_actual", "unsigned64", "B", "windows.process.query",
      { reading: { resource: "memory", quantity: "working_set_including_shared" }, total: "memory.usage", additive: false }),
    catalogField("process_all_private_working_set_actual", "unsigned64", "B", "windows.process.query",
      { reading: { resource: "memory", quantity: "private_working_set" }, total: "memory.usage", additive: true }),
    catalogField("process_all_private_commit_actual", "unsigned64", "B", "windows.process.query",
      { reading: { resource: "memory", quantity: "private_commit" }, total: "virtualMemory.usage", additive: true }),
    catalogField("process_all_peak_private_commit_actual", "unsigned64", "B", "windows.process.query",
      { reading: { resource: "memory", quantity: "peak_private_commit" }, additive: false }),
    catalogField("process_all_handle_count_actual", "unsigned64", "count", "windows.process.query"),
    catalogField("process_all_cpu_usage_actual", "float64", "%", "windows.process.cpu-interval", { total: "cpu.usage", additive: true })
  ];
}
