import type { AppCopy } from "../zh/index.ts";

export const enStatusCopy: Pick<AppCopy, "status"> = {
  status: {
    sentenceEnd: ".",
    actionFailed: "The action failed, please try again later",
    stateUpdated: "State updated",
    unknownState: "State unknown",
    noData: "No data",
    unknownTime: "Time unknown",
    systemGraphicsUsage: "System graphics usage",
    state: {
      available: "Available",
      installed: "Installed",
      installedUnverified: "Installed, waiting for confirmation",
      readyToInstall: "Ready to install",
      downloadable: "Available to download",
      manualDownload: "Manual download required",
      notInstalled: "Not installed",
      running: "Running",
      stopped: "Stopped",
      starting: "Starting",
      ready: "Ready",
      refreshing: "Refreshing",
      warming: "Preparing",
      failed: "Action failed",
      unavailable: "Temporarily unavailable",
      disabled: "Off",
      enabled: "On",
      queued: "Waiting",
      canceling: "Canceling",
      canceled: "Canceled",
      completed: "Completed",
      restored: "Restored",
      missing: "Not found",
      unknown: "State unknown"
    },
    risk: {
      low: "Low risk",
      medium: "Medium risk",
      high: "High risk",
      root: "Root directory migration",
      blocked: "Migration blocked",
      unknown: "Risk not confirmed"
    },
    migration: {
      kindRoot: "Software root directory",
      kindData: "Software data",
      targetMisc: "Other data",
      targetUser: "User data",
      classificationApplicationRoot: "Software root directory",
      classificationUserAppData: "User application data",
      classificationProgramData: "Shared application data",
      classificationDirectory: "Regular directory",
      classificationFile: "File",
      classificationMissing: "Source location does not exist",
      classificationUnknown: "Data to be confirmed",
      stateRunning: "Monitoring",
      stateStopped: "Stopped",
      stateCompleted: "Completed",
      stateRestored: "Restored",
      stateFailed: "Not completed",
      stateUnknown: "State unknown"
    },
    metricGroup: {
      cpu: "Processor",
      gpu: "Graphics processor",
      memory: "Memory",
      virtualMemory: "Virtual memory",
      disk: "Disk",
      network: "Network",
      motherboard: "Motherboard",
      fan: "Fan",
      hardwareMonitor: "Hardware sensors",
      system: "System"
    },
    metricUnavailable: {
      needsComponent: "Needs a hardware support component",
      deviceNotProvided: "Not readable"
    },
    component: {
      nameFallback: "Hardware support component",
      purposeFallback: "Adds hardware information and related features to Resource Manager.",
      names: {
          fanControlCore: "Fan control core",
          hardwareBridge: "Hardware control helper",
        amdSmuPawnIo: "AMD processor sensor support",
        hardwareSensorsPawnIo: "Hardware sensor driver (PawnIO)",
        intelMsrPawnIo: "Intel processor control",
        libreHardwareMonitor: "Motherboard and memory sensor support",
        sharedWebView2Runtime: "Shared WebView2 runtime",
        notebookOemFan: "Notebook vendor fan support",
        nvidiaNvml: "NVIDIA GPU monitoring support",
        nvidiaNvapi: "NVIDIA GPU extended monitoring support",
        amdAdlx: "AMD GPU monitoring support",
      },
      purposes: {
          fanControlCore: "Reads and controls chassis fans using firmware or software curves.",
          hardwareBridge: "Adjusts processor power limits and voltage, restoring saved original values when released.",
        amdSmu: "Reads the processor's SMU tables directly for STAPM, power, voltage, current, temperature, and per-core readings.",
        hardwareSensorsPawnIo: "Installs the officially signed PawnIO driver used by the motherboard and memory sensors. Never installed silently.",
        intelMsrPawnIo: "Adjusts PL1/PL2, time windows, and voltage offsets through the officially signed IntelMSR module. Every write is read back, and original values return on release.",
        motherboardMemorySensors: "Built-in core library for motherboard temperatures, voltages, fans, and memory temperature. Needs the hardware sensor driver.",
        nvidiaNvml: "Reads NVIDIA clocks, VRAM, power, and temperature from the installed driver.",
        nvidiaNvapi: "Adds the fan, voltage, and current readings the base NVIDIA interface leaves out.",
        amdGpu: "Reads AMD GPU clocks, temperature, power, and fan speed.",
        notebookOemFan: "Uses the vendor's own driver for CPU/GPU fan speed. More accurate on recognised models.",
        sharedWebView2Runtime: "The Chromium runtime the interface runs on, shared across the machine."
      },
      categories: {
        hardwareMonitoring: "Hardware monitoring"
      }
    },
    resourceData: {
    },
    detail: {
      status: "Status",
      description: "Description",
      software: "Software",
      unnamedSoftware: "Unnamed software",
      content: "Content",
      savedLocation: "Saved location",
      createdAt: "Created",
      restoredAt: "Restored",
      migrationContent: "Migrated content",
      location: "Location",
      sourcePath: "Original location",
      destinationPath: "New location"
    }
  }
};
