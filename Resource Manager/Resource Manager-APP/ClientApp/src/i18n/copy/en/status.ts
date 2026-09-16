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
        sharedWebView2Runtime: "Shared WebView2 runtime",
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
        msiAfterburner: "If Afterburner is already installed, its GPU readings are picked up too. Optional.",
        amdRyzenSdk: "AMD's own SDK. Reads Ryzen power, voltage, current, and temperature.",
        amdSmu: "Reads the SMU tables directly, adding STAPM and per-core readings the SDK does not expose.",
        intelCpu: "Reads Intel processor power, frequency, and temperature.",
        generalHardwareSensors: "The general source for motherboard, fan, temperature, and voltage readings. Enough for most machines.",
        nvidiaNvml: "Reads NVIDIA clocks, VRAM, power, and temperature from the installed driver.",
        nvidiaNvapi: "Adds the fan, voltage, and current readings the base NVIDIA interface leaves out.",
        amdGpu: "Reads AMD GPU clocks, temperature, power, and fan speed.",
        notebookEcFan: "Reads fans from the notebook's EC, for machines where neither general monitoring nor the GPU driver exposes them.",
        notebookOemFan: "Uses the vendor's own driver for CPU/GPU fan speed. More accurate on recognised models.",
        latencyMon: "Investigates interrupt latency and stutter, down to which driver is holding the system up.",
        windowsPerformanceToolkit: "Microsoft's WPR / Xperf, for deeper system-level tracing.",
        sharedWebView2Runtime: "The Chromium runtime the interface runs on, shared across the machine."
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
