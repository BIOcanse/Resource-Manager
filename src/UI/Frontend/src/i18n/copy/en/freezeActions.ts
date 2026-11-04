import { plural } from "../../plural.ts";
import type { AppCopy } from "../zh/index.ts";

export const enFreezeActionsCopy: Pick<AppCopy, "deviceActions" | "crashDumpKeys" | "cpuMonopoly"> = {
  deviceActions: {
    restart: {
      label: "Restart device",
      busy: "Restarting",
      hint: "Disables and re-enables the device this report points to, which can get a stuck driver working again. You confirm every time; the software never does this on its own.",
      confirmTitle: "Restart this device?",
      confirmMessage: (target: string) => `The device behind “${target}” will be disabled and enabled again.`,
      confirmDetails: [
        "This runs once, only after you confirm, and is never repeated automatically.",
        "Administrator rights are required. The result is written to the device action records in Details."
      ],
      risks: {
        DeviceRestartNetworkInterrupted: "If it is a network adapter, network connections are interrupted for a moment.",
        DeviceRestartDisplayStateLost: "If it is a graphics adapter, the screen goes black briefly and window positions and display settings may be lost.",
        DeviceRestartMayRequireReboot: "Some devices only recover after the computer restarts."
      },
      unknownRisk: (code: string) => `Other risk (${code}).`,
      confirm: "Restart device",
      doneTitle: "Device restarted",
      refusedTitle: "Device not restarted",
      failed: "Could not restart the device.",
      reason: (text: string) => `Result: ${text}`,
      deviceResult: (text: string) => `Device result: ${text}`,
      win32Detail: (code: string) => `System error code: ${code}`,
      recorded: (sequence: string) => `Written to the device action records (number ${sequence}).`,
      notRecorded: "This request was not recorded."
    },
    reasons: {
      completed: "Completed",
      "confirmation-required": "Confirmation required",
      "report-not-found": "The report entry was not found",
      "unsupported-report": "This kind of report does not support restarting a device",
      "device-evidence-unavailable": "The report has no evidence that identifies the device",
      "invalid-settings": "The settings are invalid",
      "record-capacity-reached": "The device action records are full, so nothing was done",
      "persistence-failed": "The record could not be written, so nothing was done",
      "device-resolution-failed": "The device to restart could not be identified",
      "device-restart-failed": "Restarting the device failed",
      "notification-failed": "The device was handled, but the notification could not be sent"
    },
    results: {
      success: "Success",
      "confirmation-required": "Confirmation required",
      "invalid-instance-id": "Invalid device instance ID",
      "administrator-required": "Administrator rights required",
      "device-not-found": "Device not found",
      "device-unavailable": "Device currently unavailable",
      "disable-failed": "Disabling the device failed",
      "enable-failed": "Enabling the device again failed",
      "reboot-required": "The computer must restart",
      "verification-failed": "The check after the restart did not pass",
      "driver-not-found": "Driver not found",
      "ambiguous-device": "Several devices matched, so nothing was done",
      "invalid-module": "Invalid driver module",
      "enumeration-failed": "Listing devices failed",
      "buffer-too-small": "System buffer too small",
      "unsupported-platform": "Not supported on this system",
      interrupted: "Interrupted while in progress; the result is unknown"
    },
    unknownCode: (code: string) => `Code ${code}`,
    records: {
      tab: "Device actions",
      intro: "Each time you confirm “Restart device” on a report, the result is recorded here. Records are kept and never deleted automatically; a record in progress moves to history when the action ends.",
      loading: "Loading device action records…",
      readFailed: "Could not load the device action records",
      empty: "No device action records yet.",
      loadMore: "Load earlier records",
      loadingMore: "Loading",
      refresh: "Refresh",
      shown: (count: string) => `Records shown: ${count}.`,
      states: { active: "In progress", history: "History" },
      device: "Device instance",
      report: "Report entry",
      time: "Time",
      result: "Result",
      detail: "System error code",
      sequence: "Number"
    }
  },
  crashDumpKeys: {
    title: "Crash dump keys",
    description: "When on, holding the right Ctrl key and pressing Scroll Lock twice makes the system stop with a blue screen and write a memory dump. If the computer freezes and cannot be used normally, this keeps a record of the state for later analysis. Off by default.",
    details: [
      "It only triggers when you press these keys; otherwise it does nothing.",
      "The computer restarts immediately after it triggers, and unsaved work is lost.",
      "Changes take effect after the computer restarts and need administrator rights to write. Turning it off restores the system settings from before it was turned on."
    ],
    notProvided: "The backend did not provide this setting.",
    statusTitle: "State in the system",
    states: {
      takesEffectAfterRestart: "Written to the system; takes effect after the computer restarts.",
      off: "Not turned on.",
      restorePending: "Restoring the system settings from before it was turned on; finishes after the computer restarts.",
      configurationMismatch: "The key settings in the system do not match the choice here.",
      failed: "Writing the system settings failed."
    },
    combinations: {
      rightCtrlScrollLockTwice: "Keys: right Ctrl + Scroll Lock twice",
      custom: "Keys: custom keys set by another program or by hand"
    },
    kernelDumpMissing: "The system is not set to write kernel memory dumps, so a dump may not be written when the keys trigger.",
    reason: (text: string) => `Reason: ${text}`,
    win32Detail: (code: string) => `System error code: ${code}`,
    reasons: {
      "invalid-crash-dump-keys": "The key settings are invalid",
      "crash-dump-keys-read-failed": "Reading the system settings failed",
      "crash-dump-keys-write-failed": "Writing the system settings failed",
      "crash-dump-keys-verify-failed": "The check after writing did not pass",
      "crash-dump-keys-restore-pending": "The system settings from before are not fully restored yet",
      "invalid-crash-dump-key-snapshot": "The saved original settings are invalid",
      "crash-dump-keys-access-denied": "Administrator rights are required",
      "crash-dump-key-value-too-large": "An original value in the system is too large to save",
      "crash-dump-keys-configuration-mismatch": "Another program changed the settings in the system",
      "crash-dump-keys-bridge-failed": "The local service could not handle this setting"
    }
  },
  cpuMonopoly: {
    intro: "When a process running at high or realtime priority keeps the CPU fully busy, the system can freeze and stop responding. When on, such a process is set back to normal priority. Only the priority changes: the process is not ended or suspended, and its priority is not changed back. The process whitelist of host memory protection also applies. Off by default.",
    notProvided: "The backend did not provide the CPU monopoly protection settings.",
    unsavedNote: "There are unsaved changes; they take effect after saving.",
    rejected: (reason: string) => `The settings were not accepted: ${reason}`,
    enabledTitle: "Turn on CPU monopoly protection",
    enabledDescription: "When on, the CPU use and priority of processes are checked in the background; when off, nothing is checked.",
    thresholdTitle: "Usage threshold",
    thresholdDescription: "Timing starts when a process at high or realtime priority reaches this CPU usage, as a percentage of all processors.",
    thresholdLabel: "Percent",
    durationTitle: "Duration",
    durationDescription: "How long the usage must stay at the threshold before the process is set back to normal priority.",
    durationLabel: "Seconds",
    intervalTitle: "Check interval",
    intervalDescription: "How often the CPU use of processes is read.",
    intervalLabel: "Seconds",
    whitelistNote: "The whitelist is edited in the Host Memory Protection section.",
    statusTitle: "Current state",
    statusLoading: "Loading the CPU monopoly protection state…",
    statusUnavailable: "Could not load the CPU monopoly protection state",
    statusUnavailableDetail: "The settings can still be edited and saved; the state appears here once it is available.",
    stateActive: "Checking",
    stateInactive: (reason: string) => `Not checking: ${reason}`,
    inactiveReasons: {
      disabled: "Turned off",
      "not-running": "Protection has not started yet",
      "process-data-unavailable": "Process data cannot be read",
      "process-exit-proof-unavailable": "Whether processes have exited cannot be confirmed",
      "process-capacity": "More processes than can be tracked",
      stopped: "Stopped"
    },
    currentAction: "Action in effect",
    exclusionsTitle: "Excluded processes",
    exclusionsEmpty: "No processes are excluded right now.",
    exclusionReasons: {
      "resource-manager": "A process of this software",
      "resource-manager-process": "A process of this software",
      "protected-process": "On the whitelist",
      "critical-process": "A critical system process",
      "unsupported-platform": "Not supported on this system",
      "identity-changed": "The process has changed",
      "cannot-open": "The process cannot be accessed",
      "criticality-unavailable": "Whether it is a critical system process cannot be confirmed"
    },
    process: (name: string, processId: number) => `${name} (PID ${processId})`,
    percent: (value: string) => `${value}%`,
    seconds: (value: string) => `${value} s`,
    priorities: { High: "High", RealTime: "Realtime" },
    records: {
      tab: "CPU monopoly protection",
      intro: "CPU monopoly protection records one entry for each process it handles. After a process is set back to normal priority, its record moves to history when the process exits or you set its priority yourself; a failed action goes straight to history. Records are kept and never deleted automatically.",
      loading: "Loading CPU monopoly protection records…",
      unavailable: "Could not load the CPU monopoly protection records",
      empty: "No actions recorded yet.",
      count: (count: string, capacity: string) => `Records: ${count}; at most ${capacity}.`,
      unrecorded: (count: number) => plural("en-US", count, {
        one: "The record list is full; {count} later action was not recorded. Recording starts again after the history is cleared.",
        other: "The record list is full; {count} later actions were not recorded. Recording starts again after the history is cleared."
      }),
      outcomes: { normalized: "Set back to normal priority", failed: "Action failed" },
      states: { active: "Still in effect", history: "History" },
      time: "Time",
      cpu: "CPU usage",
      previousPriority: "Previous priority",
      threshold: "Threshold at the time",
      duration: "Duration at the time",
      reason: "Reason",
      detail: "Details",
      endedAt: "Ended",
      endReason: "End reason"
    },
    actionReasons: {
      "process-exited": "The process exited",
      "user-priority-set": "Its priority was set separately",
      "write-failed": "Writing the priority failed",
      "identity-changed": "The process has changed",
      "priority-changed": "Another program changed its priority",
      "priority-unavailable": "Its priority cannot be read",
      "priority-write-failed": "Writing the priority failed",
      "priority-readback-unavailable": "The priority could not be read back after writing",
      "priority-readback-mismatch": "The priority read back after writing does not match"
    },
    notice: {
      title: "CPU monopoly protection",
      normalized: (target: string) => `${target} was set back to normal priority.`,
      failed: (target: string, reason: string) => `Could not adjust ${target}: ${reason}`,
      openRecords: "View records"
    }
  }
};
