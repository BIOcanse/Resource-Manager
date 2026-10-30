import type { AppCopy } from "../zh/index.ts";

export const enMemoryCpuTierCopy: Pick<AppCopy, "memoryCpuTier"> = {
  memoryCpuTier: {
    intro: "A set of options that trade memory against CPU is grouped into five tiers: tier 1 uses the least memory and puts the most pressure on the CPU, tier 5 uses the least CPU and puts the most pressure on memory, and tier 3 is the default. Tiers 2 to 4 enable the proportional options, whose cost on the other side is minor, in different proportions; tiers 1 and 5 additionally enable aggressive options with a significant cost. Every option can also be set individually.",
    tierTitle: "Tier",
    tierDescription: "After a tier is selected, the value of each of its options is listed below.",
    tierAriaLabel: "Memory and CPU trade-off tier",
    tierLabel: (tier: number) => `Tier ${tier}`,
    tierLowEnd: "Less memory",
    tierHighEnd: "Less CPU",
    savedTier: (tier: number) => `Saved: tier ${tier}`,
    defaultTier: (tier: number) => `Default: tier ${tier}`,
    measuredTitle: "Measured difference",
    measuredDescription: "Approximately how much more or less memory and CPU each tier uses.",
    notMeasured: "Not measured yet",
    optionsTitle: (tier: number) => `Options of tier ${tier}`,
    previewNote: "This is an unsaved preview: the values are derived from the selected tier and your individual settings. After saving, the values reported by the backend are shown.",
    loading: "Reading tiers and options…",
    loadFailed: "The tiers and options of the memory and CPU trade-off could not be read.",
    retry: "Retry",
    noOptions: "The backend reported no options.",
    unknownOption: "Unknown option",
    unset: "Not set",
    valueLabel: "Value",
    choiceLabel: "Choice",
    categories: { proportional: "Proportional", aggressive: "Aggressive" },
    applyModes: { immediate: "Applies immediately", restart: "Applies after restart" },
    sources: { tier: "From tier", user: "Your setting" },
    currentlyRunning: (value: string) => `Currently in use: ${value}`,
    afterRestart: (value: string) => `After restart: ${value}`,
    restartNote: "Takes effect after the service restarts; this page does not restart anything.",
    clear: "Clear",
    seconds: (value: string) => `${value} s`,
    milliseconds: (value: string) => `${value} ms`,
    choices: {
      optimize: "At least Optimize (returns memory on every check)",
      autoLean: "Automatic, leaning towards less memory",
      auto: "Automatic",
      autoRetain: "Automatic, leaning towards retaining memory",
      unrestricted: "Unrestricted (does not return memory proactively)"
    },
    options: {
      gcGen0MaxBudget: { title: "GC generation 0 budget limit", description: "The memory the runtime may allocate between two collections; a smaller limit collects more often and uses less memory. When not set, the runtime decides from the processor cache." },
      gcConserveMemory: { title: "GC compaction preference", description: "How actively the runtime compacts the heap to reduce memory, from 0 to 9; higher values save memory and cost more CPU." },
      ledgerMemoryModeFloor: { title: "Ledger memory mode floor", description: "The minimum eagerness with which the application's own memory ledger returns unused memory." },
      bufferReturn: { title: "Buffer return", description: "How quickly temporary areas such as the process enumeration buffer and the counter parsing area return memory after use." },
      nativeWorkareaGrowth: { title: "Native work area growth", description: "Whether the work areas of native modules grow with actual use and shrink back, or reserve their maximum at once." },
      singleRequestStopBufferMs: { title: "Source retention after a single read", description: "How long a shared sampling source stays open after a one-off read ends, so that a read soon after need not start it again; 0 stops it as soon as the read ends. Keeping it open schedules no sampling." },
      serviceIdentityCacheMs: { title: "Service snapshot lifetime", description: "How long the snapshot of services and their processes is reused; 0 rereads it every time." },
      browserRuntimeSnapshotSeconds: { title: "Browser runtime catalog snapshot lifetime", description: "How long the snapshot of the browser runtime catalog is reused; 0 rereads it every time." },
      platformSensorSnapshotSeconds: { title: "Platform sensor snapshot lifetime", description: "How long the snapshot of platform sensor readings is reused; 0 rereads it every time." },
      storageSnapshotSeconds: { title: "Storage snapshot lifetime", description: "How long the snapshot of storage device information is reused; 0 rereads it every time." },
      rendererAbsenceEntries: { title: "Renderer absence cache entries", description: "The maximum number of processes remembered as having no renderer; more entries mean fewer repeated checks and more memory." },
      fileQuerySessionsMax: { title: "File query session limit", description: "The maximum number of file query sessions kept at the same time." },
      fileQuerySessionsPrebuilt: { title: "Prebuilt file query sessions", description: "The number of file query sessions created in advance and kept ready; 0 creates them on demand." }
    }
  }
};
