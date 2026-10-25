import { enControlPresentationCopy } from "./controlPresentation.ts";
import type { AppCopy } from "../zh/index.ts";

export const enShellCopy: Pick<AppCopy, "reportRules" | "common" | "feedback" | "page" | "control" | "diskUsage" | "shell"> = {
  common: {
    saving: "Saving",
    saveFailed: "Save failed"
  },
  feedback: {
    confirm: "Confirm",
    cancel: "Cancel",
    close: "Close",
    success: "Action completed",
    error: "Action failed",
    warning: "Needs attention",
    info: "Notice"
  },
  page: {
    monitor: "Monitor",
    components: "Software",
    optimization: "Optimization",
    details: "Details",
    settings: "Settings",
    diskUsage: "Disk",
    control: "Control"
  },
  reportRules: {
    "title": "Report rules",
    "note": "Changing a rule clears its counts and active reports and starts judging again with the new values. Trust entries stay.",
    "strayProcessTitle": "Suspected stray processes",
    "backgroundMemoryTitle": "Background memory usage",
    "loading": "Loading report rules…",
    "loadFailed": "Could not load report rules.",
    "retry": "Retry",
    "defaultValue": "Default",
    "reset": "Reset to default",
    "executableNamesHelp": "One executable name per line, without the extension (node, not node.exe). An empty list produces no reports.",
    "ruleLabels": {
      "cpu_throttle_temperature": "CPU temperature",
      "cpu_throttle_usage": "CPU usage",
      "cpu_throttle_frequency": "CPU frequency ratio",
      "interrupt_longest_event": "Longest interrupt event",
      "interrupt_cpu_share": "Interrupt CPU share",
      "background_memory_share": "Background memory share",
      "background_memory_bytes": "Background memory size",
      "stray_process_memory": "Process memory size",
      "stray_process_idle": "Process idle duration"
    },
    "fields": {
      "activationThreshold": "Activation threshold",
      "clearThreshold": "Clear threshold",
      "requiredConsecutiveHits": "Consecutive hits",
      "requiredConsecutiveMisses": "Consecutive misses",
      "zeroActivityThreshold": "Zero activity ceiling",
      "executableNames": "Runtime tool names"
    },
    "units": {
      "celsius": "°C",
      "percent": "%",
      "milliseconds": "ms",
      "minutes": "min",
      "mebibytes": "MiB",
      "gibibytes": "GiB",
      "count": "observations"
    },
    "reasons": {
      "unknown-rule": "Unknown rule.",
      "duplicate-rule": "Duplicate rule.",
      "not-editable": "This field cannot be edited.",
      "out-of-range": "Value is outside the allowed range.",
      "clear-on-wrong-side": "The clear threshold must be on the safe side of the activation threshold.",
      "invalid-executable-names": "Invalid tool list: up to 256 names, 1–260 characters each, without /, \\, : or case-insensitive duplicates."
    }
  },
  control: {
    recovery: {
      retry: "Retry",
      noticeReadFailed: "Could not read the control notice status. Read again.",
      noticeAckFailed: "Could not acknowledge the control notice. Please retry.",
      accessLoading: "Reading control access level.",
      accessReadFailed: "Could not read the control access level. Read again.",
      objectMissing: "The device is currently absent.",
      capabilityMissing: "This capability is absent on the device.",
      writerMissing: "Write support is not implemented for this item.",
      backendUnavailable: "The write channel is currently unavailable.",
      releaseFailed: "Could not release this control.",
      unitMismatch: "The setting unit does not match the device.",
      writeFailed: "The write failed.",
      writeRejected: "The device rejected this setting. See diagnostics for details.",
      readOnly: "The current startup profile is read-only.",
      accessRequired: (level: string) => `This item requires ${level} control access.`,
      outOfRange: (minimum: number, maximum: number, unit: string) => `This item must be between ${minimum} and ${maximum} ${unit}.`
    },
    reassertionInterval: "Reapply interval (seconds)",
    stateUnavailable: "Authoritative control settings are unavailable. Apply is paused.",
    retryRead: "Read again",
    curveUnavailable: "The current firmware curve could not be read. Try again; no replacement curve was created.",
    presentation: enControlPresentationCopy,
    title: "Control",
    intro: "Fan, graphics and processor tuning all live here. Whatever cannot be tuned is listed too, with the reason.",
    loadFailed: "Could not read the controllable devices.",
    empty: "Nothing tunable has been identified on this machine yet.",
    ready: "Adjustable",
    needsComponent: (name: string) => `Needs ${name}`,
    forgetFailed: "Delete not allowed",
    instances: {
      title: "Devices seen before",
      present: "Present",
      absent: "Not present",
      refresh: "Detect again",
      forget: "Remove record",
      firstSeen: (at: string) => `First seen ${at}`,
      open: "Manage devices"
    },
    actual: "Now ",
    presets: {
      save: "Save preset",
      namePlaceholder: "Preset name",
      remove: "Delete preset"
    },
    overclock: {
      title: "Intel integrated graphics authorization",
      body: "Intel's integrated-graphics control library requires your consent before it "
        + "allows any clock change, negative offsets included. That is its own requirement "
        + "and has nothing to do with other graphics cards.",
      accept: "Accept",
      revoke: "Withdraw consent",
      accepted: "Accepted"
    },
    ownership: {
      label: "Control owner",
      firmware: "Firmware-managed",
      app: "App-managed",
      firmwareNote: "The firmware is managing this right now; the settings below have no effect."
    },
    toggleChoice: {
      firmware: "Return to firmware",
      firmwareHint: "Keeps no setting for this item. Saving restores the state from before the app took over and returns control to the firmware; it does not switch the item off."
    },
    notice: {
      warrantyTitle: "About your warranty",
      warrantyBody: [
        "Vendor terms usually count any out-of-spec operation as a modification, in either "
          + "direction — taken literally, that includes undervolting.",
        "The practical difference is what it leaves behind: lowering voltage, clocks or power "
          + "walls stops at power-off and records nothing in the processor; raising clocks or "
          + "voltage sets a flag in the processor that service can read.",
        "On laptops many of these limits come from the system vendor's BIOS rather than the "
          + "chip vendor's defaults, so how far this page can go is their decision too."
      ],
      riskTitle: "Before you start",
      next: "Next",
      accept: "Understood"
    },
    accessLevel: {
      title: "Adjustment access",
      intro: "Decides which items the Control page lets you change. Moving up a level "
        + "changes none of your existing settings; it only unlocks more items.",
      name: { normal: "Normal", root: "root" },
      summary: {
        normal: "Power, current, fan and standard clock adjustments. "
          + "Incorrect settings can cause instability.",
        root: "Items nothing catches: writing voltage directly, changing the base clock. "
          + "Rarely needed."
      },
      hint: {
        normal: "A wrong value makes the machine unstable; a reboot recovers it. Switch to Normal in Settings first.",
        root: "Nothing catches a wrong value here. Switch to root in Settings first."
      },
      consequences: {
        normal: [
          "Too little voltage shuts the machine off; too much clock offset corrupts or "
            + "blanks the screen. Both clear on reboot and harm nothing.",
          "Raising the thermal limit moves the overheat protection outward; running that "
            + "way for long ages the silicon faster."
        ],
        root: [
          "Writing voltage and base clock directly has no software guard at all; one wrong "
            + "number can leave the machine unable to boot.",
          "Nothing here is needed day to day. Only change a value you already understand."
        ]
      },
      disclaimer: {
        normal: "Some settings can make the computer unstable, and on a machine with an "
          + "existing design fault they carry some risk of damage. This software accepts "
          + "no responsibility for any consequence of changing hardware configuration.",
        root: "In root mode some adjustments are extremely dangerous and are very likely "
          + "to cause system instability or permanent hardware damage. This software accepts "
          + "no responsibility for any consequence of changing hardware configuration."
      },
      confirmTitle: (name: string) => `Switch to ${name}?`,
      confirmAction: "Switch",
      cancel: "Cancel",
      saveFailed: "Could not switch; still on the previous level."
    },
    channel: {
      nvapi: "NVAPI",
      "nvapi-drs": "Driver profile",
      nvml: "NVML",
      "oem-ec": "System firmware",
      "amd-smu": "AMD SMU",
      "fan-core": "Fan core",
      igcl: "Intel graphics",
      adlx: "AMD ADLX"
    } as Record<string, string>,
    channelHint: "Which channel this setting is actually written through.",
    detect: "Re-detect hardware",
    detecting: "Detecting",
    readings: "Read-only readings",
    takeoverCoversOthers: (others: string) =>
      `On this machine, handing fans to software is a single machine-wide switch: choosing software takeover also pulls ${others} away from firmware control, leaving them at their current speed unless you give them a curve too.`,
    createCurve: "New curve",
    readingFirmwareCurve: "Reading the firmware curve…",
    fixedStepsCurveNote: "This firmware table only lets you change the speed of each step; the temperature breakpoints are fixed by the firmware.",
    curvePreview: "Curve preview",
    saveFailed: "Save failed",
    apply: "Apply",
    discard: "Discard",
    term: {
      windows: "Windows", nvidia: "NVIDIA", amd: "AMD", intel: "Intel",
      oem: "System manufacturer", unknown: "Unknown manufacturer",
      "firmware-management": "Firmware management",
      "firmware-write": "Firmware write",
      "software-management": "Software management",
      "takeover-all-fans": "Software management takes over every fan on this channel.",
      "uniwill-wmi-ec": "Firmware channel (Uniwill EC)",
      "acer-gaming-wmi": "Firmware channel (Acer WMI)",
      "asus-atk-wmi": "Firmware channel (ASUS ATK)",
      "hp-wmi-bios": "Firmware channel (HP BIOS)",
      "lenovo-legion-wmi": "Firmware channel (Lenovo Legion)",
      "desktop-superio": "Motherboard fan channel (Super I/O)",
      "hwmon": "Kernel fan channel (hwmon)",
      portable: "Laptop",
      fixed: "Desktop",
      cpu: "CPU fan",
      gpu: "GPU fan",
      intake: "Intake fan",
      case: "Case fan"
    } as Record<string, string>,
    attachment: {
      integrated: "Integrated",
      discrete: "Discrete",
      unknown: "Attachment unknown"
    },
    status: {
      unset: "Not set",
      edited: "Changed, not applied",
      applying: "Applying",
      applied: "Applied",
      unsupported: "Not tunable on this machine",
      failed: "Did not apply"
    },
    kind: {
      gpu: "Graphics",
      cpu: "Processor",
      fan: "Fans"
    }
  },
  diskUsage: {
    title: "Disk Usage",
    intro: "Scan, then see files laid out as tiles sized by what they actually take up.",
    scopeLabel: "Scan scope",
    scopeAllVolumes: "All drives",
    scopeVolume: "One drive",
    scopeFolder: "One folder",
    modeLabel: "Scan method",
    modeFast: "Fast scan",
    modeFastHint: "Reads the filesystem index, so a whole drive takes seconds. NTFS only, and needs administrator rights.",
    modeFull: "Full scan",
    modeFullHint: "Walks the directories one level at a time. Slow, but works on any drive.",
    chooseFolder: "Choose folder",
    folderNotChosen: "No folder chosen yet",
    scan: "Start scan",
    rescan: "Scan again",
    cancel: "Cancel scan",
    scanning: "Scanning",
    scanFailed: "The scan did not complete. See Task Center for details.",
    resultReadFailed: "Could not read scan results.",
    retainedResultStale: "The displayed result is from an earlier read and may be out of date.",
    volumesReadFailed: "Could not read the volume list.",
    retainedVolumesStale: "The displayed volume list is from an earlier read and may be out of date.",
    retryRead: "Retry read",
    volumeColumnLabel: "Drive",
    rootSelectorLabel: "Scanned root",
    queryPage: (page: number) => `Page ${page}`,
    nextPage: "Next page",
    previousPage: "Previous page",
    partialQuery: "This page shows some files and folders. Other items are on other pages. Open a folder to see its contents.",
    noVolumes: "No scannable drive was found.",
    notReady: "Not ready",
    freeOfTotal: (free: string, total: string) => `${free} free of ${total}`,
    volumeKind: {
      physical: "Physical drive",
      virtual: "Virtual drive",
      external: "External drive",
      network: "Network location",
      optical: "Optical drive",
      unknown: "Unknown source"
    },
    fastUnsupported: "This drive has no readable filesystem index, so a fast scan cannot reach it. Use a full scan.",
    skipped: "Not covered by this scan",
    skipReason: {
      noFileSystemIndex: "The filesystem has no readable index",
      needsElevation: "Reading the index needs administrator rights",
      volumeNotReady: "The drive is not ready",
      targetUnavailable: "The path does not exist or cannot be opened"
    },
    scanKind: {
      masterFileTable: "Read the filesystem index",
      directoryWalk: "Walked the directories"
    },
    navigateUp: "Up one level",
    resetView: "Reset view",
    collapseSetup: "Hide options",
    expandSetup: "Scan options",
    scanTotals: (size: string, files: number, folders: number) =>
      `${size} · this root · ${files} files · ${folders} folders across this scan`,
    fileCount: (count: number) => `${count} files`,
    omitted: (count: number) =>
      `${count} more tiles are too small or off-screen right now. Zoom in to see them.`,
    copied: "Copied",
    menuOpenLocation: "Open file location",
    menuProperties: "Properties",
    menuCopyPath: "Copy full path",
    menuDrillDown: "Zoom into this",
    menuSize: "Size",
    menuPath: "Path",
    menuKindFile: "File",
    menuKindDirectory: "Folder",
    emptyTitle: "Nothing scanned yet",
    emptyDetail: "Pick a scope and a method, then start the scan."
  },
  shell: {
    productName: "Resource Manager",
    documentTitle: (page: string, product: string) => `${page} · ${product}`,
    pageNav: "Page navigation",
    currentPage: "Current page",
    runtimeCapability: "Runtime capability",
    taskCenter: "Task center",
    taskCenterActive: (count: number) => `Task center, ${count} in progress`,
    taskCenterSyncing: "Task center · backend tasks are syncing",
    taskCenterDisconnected: "Task center · the local service is resyncing",
    taskCenterUnavailable: "Task center · backend task state unavailable",
    minimize: "Minimize",
    maximize: "Maximize",
    closeWindow: "Close",
    uptime: "Uptime",
    uptimeTitle: "Time since this PC last started"
  }
};
