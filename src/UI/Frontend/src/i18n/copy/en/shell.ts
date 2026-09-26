import type { AppCopy } from "../zh/index.ts";

export const enShellCopy: Pick<AppCopy, "common" | "feedback" | "page" | "control" | "diskUsage" | "shell"> = {
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
    monitor: "Monitoring Console",
    components: "Components and Software",
    optimization: "Performance Optimization",
    details: "Detailed Information",
    settings: "Settings",
    diskUsage: "Disk Usage",
    control: "Control"
  },
  control: {
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
    curveExecutionLabel: "Who runs the curve",
    takeoverCoversOthers: (others: string) =>
      `On this machine, handing fans to software is a single machine-wide switch: choosing software takeover also pulls ${others} away from firmware control, leaving them at their current speed unless you give them a curve too.`,
    curveExecutionFirmware: "Write to firmware",
    curveExecutionFirmwareHint: "The firmware follows the table itself. Survives closing this app and rebooting.",
    curveExecutionSoftware: "Software takeover",
    curveExecutionSoftwareHint: "This app recalculates every 0.1 s. The fan returns to firmware when the app is gone.",
    createCurve: "New curve",
    readingFirmwareCurve: "Reading the firmware curve…",
    fixedStepsCurveNote: "This firmware table only lets you change the speed of each step; the temperature breakpoints are fixed by the firmware.",
    curvePreview: "Curve preview",
    saveFailed: "Save failed",
    apply: "Apply",
    discard: "Discard",
    term: {
      portable: "Laptop",
      fixed: "Desktop",
      cpu: "CPU fan",
      "curve-firmware": "Firmware curve",
      "curve-software": "Software curve",
      "curve-fixed-steps": "Fixed-step firmware table",
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
    volumeColumnLabel: "Drive",
    noVolumes: "No scannable drive was found.",
    notReady: "Not ready",
    freeOfTotal: (free: string, total: string) => `${free} free of ${total}`,
    volumeKind: {
      physical: "Physical drive",
      virtual: "Virtual drive",
      removable: "Removable",
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
      `${size} · ${files} files · ${folders} folders`,
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
    closeWindow: "Close"
  }
};
