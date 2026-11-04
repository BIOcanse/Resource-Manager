import { plural } from "../../plural.ts";
import type { AppCopy } from "../zh/index.ts";

export const enFreezeReportsCopy: Pick<AppCopy, "freezeReports"> = {
  freezeReports: {
    systemEvents: {
      tab: "System events",
      intro: "The local service collects events such as storage retries and resets, display driver resets, memory exhaustion, desktop process crashes and hardware errors from the system logs, and merges them into entries by category and target. This software cannot resolve these problems, so they are only detected and reported here; entries without new events for a long time move to history.",
      loading: "Loading the system event report…",
      readFailed: "The system event report could not be read.",
      notObservedTitle: "System events are not being recorded",
      notObservedDetail: "System event recording is not running: it may have been turned off in the settings or has not started yet. No entries are created until it runs.",
      unavailableTitle: "System event recording failed",
      unavailableDetail: "The local service cannot read the system logs. The reason is shown above.",
      emptyTitle: "No system events",
      emptyDetail: "No events that need to be reported have appeared in the system logs.",
      status: {
        label: "Recording status",
        observing: "Recording",
        "not-observed": "Not recording",
        unavailable: "Recording failed"
      },
      activeCount: "Active entries",
      historyCount: "History entries",
      machineTarget: "Whole computer",
      count: "Occurrences",
      times: (count: number) => plural("en-US", count, { one: "{count} time", other: "{count} times" }),
      firstObserved: "First seen",
      lastObserved: "Last seen",
      overview: "Overview",
      categoryLabel: "Category",
      target: "Target",
      state: "State",
      activeEntry: "Active",
      historyEntry: "History",
      category: {
        storage_retry_reset: "Storage retries and resets",
        gpu_reset: "Display driver reset",
        memory_exhausted: "Memory exhaustion",
        desktop_crash: "Desktop process crash",
        hardware_error: "Hardware error",
        unexpected_shutdown: "Unexpected shutdown and stop error"
      },
      targetKindLabel: "Target type",
      targetKind: {
        machine: "Whole computer",
        physical_disk: "Physical disk",
        storage_port: "Storage controller port",
        volume: "Volume",
        gpu_device: "Graphics device",
        desktop_process: "Desktop process",
        hardware: "Hardware"
      },
      time: "Time",
      channel: "Log",
      channelName: {
        system: "System",
        application: "Application"
      },
      provider: "Source",
      eventId: "Event ID",
      recordId: "Record ID",
      bugcheckCode: "Stop code",
      dumpPath: "Dump file",
      failure: (name: string) => `The system logs could not be read (${name}). Existing entries are kept, and recording continues once reading recovers.`,
      liveKernelFailure: (name: string) => `The LiveKernelReports folder could not be scanned (${name}). Events from the system logs are not affected.`,
      unrecorded: (count: number) => plural("en-US", count, {
        one: "{count} more entry was not recorded because the entry limit was reached.",
        other: "{count} more entries were not recorded because the entry limit was reached."
      }),
      items: "System event entries",
      detailsButton: "Details",
      back: "Back to list",
      eventsTitle: (count: number) => plural("en-US", count, {
        one: "Latest raw event ({count})",
        other: "Latest raw events ({count})"
      }),
      xml: "Event XML"
    },
    recorder: {
      tab: "Freeze recorder",
      intro: "The local service continuously writes a small amount of memory, kernel pool, disk and interrupt data to a fixed-size record file. If the computer freezes and is forcibly shut down or stops with an error, a post-restart review is created at the next startup by comparing the records with the system logs. It lists the records before the freeze, the system events from the same period and the findings. This information is only detected and reported here.",
      loading: "Loading the freeze recorder…",
      readFailed: "The freeze recorder could not be read.",
      emptyTitle: "No post-restart reviews",
      emptyDetail: "No unexpected shutdown or stop error has been found since the last startup.",
      recorder: "Recorder",
      running: "Recording",
      stopped: "Not recording",
      lastRecordAt: "Last record",
      reviewCount: "Post-restart reviews",
      failure: (name: string) => `The recorder has failed (${name}) and is not writing records.`,
      stoppedNote: "The recorder is not running; it may have been turned off in the settings. A freeze that occurs while it is off cannot be fully reviewed.",
      gapsTitle: "Data missing from the current records",
      gap: {
        "no-sample": "No record has been written yet",
        "interrupt-cpu-share": "Processor time used by interrupts",
        "longest-interrupt": "Longest single interrupt",
        "active-kernel-leak-system-item": "Whether a system-level kernel leak entry is active",
        "process-private-commit": "Private commit of processes",
        "physical-disk-inventory": "List of physical disks",
        "physical-disk-completed-operations": "Completed operations of physical disks",
        commit: "Commit charge and commit limit",
        "physical-memory": "Physical memory",
        "kernel-pools": "Paged and nonpaged pools"
      },
      reviews: "Post-restart reviews",
      reviewTitle: (time: string) => `Unexpected shutdown · ${time}`,
      shutdownAt: "Shutdown or stop error time",
      gapDuration: "Interval",
      bugcheckCode: "Stop code",
      tagsTitle: "Findings",
      noTags: "No findings.",
      inputsIncomplete: "Some records or system logs could not be read completely during the review, so the result may be incomplete.",
      tag: {
        bugcheck: {
          name: "Stop error",
          description: "The system logs recorded a stop error; the stop code is shown in the overview."
        },
        "commit-exhausted": {
          name: "Commit exhausted",
          description: "The commit charge in the records reached the immediate line of memory protection, or the system reported memory exhaustion in the same period."
        },
        "physical-exhausted": {
          name: "Physical memory exhausted",
          description: "Available physical memory in the records fell below the percentage set in the settings."
        },
        "pool-high": {
          name: "Kernel pool too high",
          description: "The nonpaged pool exceeded the set percentage of physical memory, or a system-level kernel leak entry was active."
        },
        "storage-unresponsive": {
          name: "Storage not responding",
          description: "Storage retries or resets occurred in the same period, or a disk met the not-responding condition several times in a row."
        },
        "gpu-reset": {
          name: "Display driver reset",
          description: "A display driver reset occurred in the same period."
        },
        "hardware-error": {
          name: "Hardware error",
          description: "The system reported a hardware error in the same period."
        },
        "recording-stopped-normal": {
          name: "Records stopped while normal",
          description: "None of the above occurred and the last records were all normal. Possible causes are a stalled storage or filter driver, a sudden power loss, or holding down the power button."
        }
      },
      details: {
        button: "Details",
        back: "Back to list",
        loading: "Loading the review…",
        readFailed: "The review could not be read.",
        export: "Export",
        exporting: "Exporting",
        exportFailed: "The review could not be exported.",
        overview: "Overview",
        reviewWindow: "Review period",
        timeRange: (from: string, to: string) => `${from} – ${to}`,
        samplesSeen: "Records in the period",
        inputsComplete: "Data complete",
        yes: "Yes",
        no: "No",
        dumpPath: "Dump file",
        thresholdsTitle: "Thresholds used in the review",
        commitImmediatePercent: "Commit immediate line",
        physicalAvailablePercent: "Available physical memory floor",
        nonpagedPoolPercent: "Nonpaged pool ceiling (of physical memory)",
        storageConsecutive: "Consecutive disk not-responding records",
        sampleInterval: "Record interval",
        reviewWindowLength: "Review period length",
        curvesTitle: "Record curves",
        curve: {
          commit: "Commit charge (of commit limit)",
          physicalAvailable: "Available physical memory (of total)",
          nonpagedPool: "Nonpaged pool",
          pagedPool: "Paged pool",
          diskQueue: "Disk queue depth (highest)",
          diskLatency: "Disk latency (highest)",
          interrupt: "Processor time used by interrupts"
        },
        curveRange: (min: string, max: string) => `${min} – ${max}`,
        noCurveData: "No data",
        noRecords: "There are no records in the review period.",
        lastRecordTitle: "Last record",
        time: "Time",
        commit: "Commit charge",
        physicalAvailable: "Available physical memory",
        ofLimit: (formattedValue: string, formattedLimit: string) => `${formattedValue} / ${formattedLimit}`,
        longestInterrupt: "Longest single interrupt",
        kernelLeakActive: "System-level kernel leak entry active",
        processesTitle: "Processes with the most private commit",
        processName: (name: string, pid: string) => `${name} (PID ${pid})`,
        diskName: (id: string) => `Physical disk ${id}`,
        queueDepth: "Queue depth",
        latency: "Latency",
        completedOperations: "Completed operations",
        eventsTitle: (count: number) => plural("en-US", count, {
          one: "System event in the period ({count})",
          other: "System events in the period ({count})"
        }),
        noEvents: "No system events were received in the review period.",
        actualShutdownAt: "Actual shutdown time"
      }
    },
    settings: {
      open: "Settings",
      back: "Back to report",
      systemEventsTitle: "System event settings",
      recorderTitle: "Freeze recorder settings",
      readFailed: "The settings could not be read.",
      save: "Save",
      saving: "Saving",
      discard: "Discard changes",
      saved: "The settings have been saved.",
      saveFailed: "The settings could not be saved.",
      invalid: "The settings did not pass validation and were not saved. Check the values.",
      conflict: "The settings were not saved: the rule table, the number of records and the record file location cannot be changed while running. Turn off the related feature and save first, then change these items. If a record file already exists, changing the number of records also requires a new record file location.",
      resourceBound: "The settings were not saved: the required resources exceed the limit. Reduce the capacity or number of records.",
      draftInvalid: "The current changes are incomplete (for example, a rule has no source name) and cannot be saved.",
      rulesLocked: "The rule table cannot be changed while system event recording is on. Turn it off and save first.",
      recorderLocked: "This item cannot be changed while the recorder is on. Turn it off and save first.",
      units: {
        minutes: "Minutes",
        seconds: "Seconds",
        milliseconds: "Milliseconds",
        percent: "Percentage",
        count: "Number"
      },
      systemEvents: {
        enabled: {
          title: "Record system events",
          description: "Receive events from the system logs and merge them into entries. When turned off, no new events are received and existing entries are kept."
        },
        historyAfter: {
          title: "Move to history after",
          description: "An entry moves to history when no new event arrives for this long after its last event."
        },
        eventTailCapacity: {
          title: "Raw events kept per entry",
          description: "How many of the latest raw events each entry keeps for the details page."
        },
        entryCapacity: {
          title: "Entry limit",
          description: "The maximum number of entries kept; after the limit is reached, new entries are only counted, not recorded."
        },
        liveKernelRoot: {
          title: "LiveKernelReports location",
          description: "The folder where the system stores live kernel dump reports, used to find reports such as display driver timeouts."
        },
        liveKernelDirectories: {
          title: "Scanned subfolders",
          description: "Subfolders scanned under the location above, one per line."
        },
        liveKernelEntryCapacity: {
          title: "Dump report limit",
          description: "The maximum number of dump reports recorded in each scan."
        },
        rules: {
          title: "Event rules",
          description: "Which system log events belong to which category, and which type of target each event refers to."
        }
      },
      recorder: {
        enabled: {
          title: "Record freeze data",
          description: "Write records continuously and create a review at the next startup after an unexpected shutdown. When turned off, recording stops and the record file is kept."
        },
        sampleInterval: {
          title: "Record interval",
          description: "The time between two records. A shorter interval brings the last record closer to the moment of the freeze."
        },
        reviewWindow: {
          title: "Review period",
          description: "How long before the last record the review shows records and system events."
        },
        commitImmediate: {
          title: "Commit immediate line",
          description: "The line used to find commit exhaustion. It follows the immediate line of memory protection and is changed in the memory protection settings."
        },
        commitImmediateNone: "Not set",
        physicalAvailable: {
          title: "Available physical memory floor",
          description: "Physical memory exhaustion is found when available physical memory falls below this percentage of the total."
        },
        nonpagedPool: {
          title: "Nonpaged pool ceiling",
          description: "Kernel pool too high is found when the nonpaged pool exceeds this percentage of physical memory."
        },
        storageConsecutive: {
          title: "Consecutive disk not-responding records",
          description: "Storage not responding is found when a disk meets the not-responding condition in this many records in a row."
        },
        slots: {
          title: "Number of records",
          description: "How many records the record file holds, which decides how far back the review can look. If a record file already exists, changing this also requires a new record file location."
        },
        ringPath: {
          title: "Record file location",
          description: "The full path of the record file. Leave it empty to use the default location."
        },
        ringPathDefault: "Default location"
      },
      ruleTable: {
        channel: "Log",
        provider: "Source name",
        ids: "Event IDs",
        idsHint: "Separated by commas",
        category: "Category",
        targetKind: "Target type",
        remove: "Remove",
        add: "Add rule"
      }
    }
  }
};
