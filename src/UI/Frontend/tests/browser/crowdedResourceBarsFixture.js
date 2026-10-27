/**
 * 后端实际形状的 CPU 条和物理内存条（real-slices/20261027T043049Z）：几个大软件、一百多个很小的服务，最后是系统/驱动保留。
 * 物理内存的保留量画了约三分之一、带 14 个计数器分项；其中一个小服务是 WLAN AutoConfig（3.26 MiB）。
 * CPU 条前面一串细段，后面是 3% 的保留量和空余。mock 场景字段见 mockFrontendServer.mjs 的 physicalMemorySoftware、cpuSoftware。
 */
const GiB = 1024 ** 3;
const MiB = 1024 ** 2;

export const crowdedMemory = (() => {
  const capacity = 32 * GiB;
  const used = 12 * GiB;
  const wlanId = "windows-service:wlansvc";
  const services = Array.from({ length: 168 }, (_, index) =>
    [`windows-service:fixture-${index}`, `Fixture Service ${index}`, "windows_service", "windows_service", Math.round((20 - index * 0.11) * MiB)]);
  services.splice(150, 0, [wlanId, "WLAN AutoConfig", "windows_service", "windows_service", Math.round(3.26 * MiB)]);
  // 后端按值从大到小排软件行，保留量在最后。
  const software = [
    ["runtime-root:browser", "Microsoft Edge", "runtime_root", "other", Math.round(2.25 * GiB)],
    ["runtime-root:editor", "Visual Studio Code", "runtime_root", "other", 1 * GiB],
    ...services
  ].sort((left, right) => right[4] - left[4]);
  const attributed = software.reduce((sum, row) => sum + row[4], 0);
  const residual = used - attributed;
  // 后端给出的 14 个分项：8 个非分页池标记、其他标记、池内未分类、分页池驻留、驱动驻留、代码驻留和保留量内未分类（取余数）。
  const tags = [["File", "656C6946"], ["IoFE", "45466F49"], ["EtwB", "42777445"], ["ConT", "546E6F43"],
    ["NtFC", "4346744E"], ["HalD", "446C6148"], ["Thre", "65726854"], ["NVRM", "4D52564E"]];
  const leaves = [
    ...tags.map(([tag, hex], index) => [`system-memory/nonpaged-pool/tag/${hex}`, (0.5 - index * 0.05) * GiB, tag]),
    ["system-memory/nonpaged-pool/other-tags", 0.25 * GiB, ""],
    ["system-memory/nonpaged-pool/unclassified", 0.125 * GiB, ""],
    ["system-memory/paged-pool-resident", 1 * GiB, ""],
    ["system-memory/driver-resident", 0.375 * GiB, ""],
    ["system-memory/code-resident", 0.125 * GiB, ""]
  ];
  leaves.push(["system-memory/unclassified", residual - leaves.reduce((sum, leaf) => sum + leaf[1], 0), ""]);
  if (leaves.length !== 14 || leaves.at(-1)[1] <= 0) throw new Error(`Fixture leaves do not fit the residual: ${JSON.stringify(leaves)}`);
  return { capacity, used, attributed, residual, software, leaves, wlanId, residualId: "resource-residual:memory.usage" };
})();

export const crowdedCpu = (() => {
  const software = [
    ["runtime-root:browser", "Microsoft Edge", "runtime_root", "other", 2.1],
    ["runtime-root:editor", "Visual Studio Code", "runtime_root", "other", 0.9],
    ...Array.from({ length: 120 }, (_, index) =>
      [`windows-service:cpu-fixture-${index}`, `CPU Fixture Service ${index}`, "windows_service", "windows_service", 0.012 - index * 0.00008])
  ];
  return { software, residual: 3, residualId: "resource-residual:cpu.usage" };
})();

export const crowdedScenario = {
  physicalMemoryResidual: "ready",
  physicalMemorySoftware: crowdedMemory.software,
  physicalMemoryCounterLeaves: crowdedMemory.leaves,
  cpuSoftware: crowdedCpu.software,
  cpuResidualPercent: crowdedCpu.residual
};
