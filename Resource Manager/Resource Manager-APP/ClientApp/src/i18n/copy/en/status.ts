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
      needsComponent: "Install and enable the matching hardware support component.",
      deviceNotProvided: "This device does not provide this data."
    },
    component: {
      nameFallback: "Hardware support component",
      purposeFallback: "Adds hardware information and related features to Resource Manager.",
      names: {
        amdSmuPawnIo: "AMD processor sensor support",
        amdRyzenMaster: "AMD Ryzen monitoring support",
        msiAfterburner: "MSI Afterburner",
        libreHardwareMonitor: "General hardware sensor support",
        notebookFanControl: "Notebook fan monitoring support",
        notebookOemFan: "Notebook vendor fan support",
        windowsPerformanceToolkit: "Windows Performance Toolkit",
        latencyMon: "LatencyMon",
        nvidiaNvml: "NVIDIA GPU monitoring support",
        nvidiaNvapi: "NVIDIA GPU extended monitoring support",
        amdAdlx: "AMD GPU monitoring support",
        intelPcm: "Intel processor monitoring support"
      },
      purposes: {
        amdCpuSensors: "Adds AMD processor power, temperature, voltage, and frequency information.",
        gpuOptionalMonitoring: "Provides optional GPU monitoring information.",
        generalHardwareSensors: "Adds fan, temperature, voltage, and motherboard hardware information.",
        notebookFan: "Adds notebook fan speed and running state.",
        latencyAnalysis: "Used to investigate system latency and performance problems further.",
        nvidiaGpu: "Adds NVIDIA GPU frequency, temperature, power, and fan information.",
        amdGpu: "Adds AMD GPU frequency, temperature, power, and fan information.",
        intelCpu: "Adds Intel processor power, frequency, and temperature information."
      },
      categories: {
        performanceAnalysis: "Performance analysis tools",
        helperTool: "Helper tools",
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
