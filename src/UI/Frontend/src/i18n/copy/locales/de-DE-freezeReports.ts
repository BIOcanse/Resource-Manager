import { plural } from "../../plural.ts";
import type { AppCopy } from "../index.ts";

export const freezeReports: AppCopy["freezeReports"] = {
  systemEvents: {
    tab: "Systemereignisse",
    intro: "Der lokale Dienst sammelt aus den Systemprotokollen Ereignisse wie Speicherwiederholungen und -zurücksetzungen, Zurücksetzungen des Grafiktreibers, Speichererschöpfung, Abstürze von Desktopprozessen und Hardwarefehler und fasst sie nach Kategorie und Ziel zu Einträgen zusammen. Diese Software kann solche Probleme nicht beheben; sie werden hier nur erkannt und gemeldet. Einträge ohne neue Ereignisse über längere Zeit werden in den Verlauf verschoben.",
    loading: "Bericht zu Systemereignissen wird geladen…",
    readFailed: "Der Bericht zu Systemereignissen konnte nicht gelesen werden.",
    notObservedTitle: "Systemereignisse werden nicht aufgezeichnet",
    notObservedDetail: "Die Aufzeichnung von Systemereignissen läuft nicht: Sie wurde möglicherweise in den Einstellungen deaktiviert oder noch nicht gestartet. Bis sie läuft, werden keine Einträge erstellt.",
    unavailableTitle: "Aufzeichnung der Systemereignisse fehlgeschlagen",
    unavailableDetail: "Der lokale Dienst kann die Systemprotokolle nicht lesen. Der Grund wird oben angezeigt.",
    emptyTitle: "Keine Systemereignisse",
    emptyDetail: "In den Systemprotokollen sind keine meldepflichtigen Ereignisse aufgetreten.",
    status: {
      label: "Aufzeichnungsstatus",
      observing: "Wird aufgezeichnet",
      "not-observed": "Keine Aufzeichnung",
      unavailable: "Aufzeichnung fehlgeschlagen"
    },
    activeCount: "Aktive Einträge",
    historyCount: "Verlaufseinträge",
    machineTarget: "Gesamter Computer",
    count: "Anzahl",
    times: (count: number) => plural("de-DE", count, { one: "{count}-mal", other: "{count}-mal" }),
    firstObserved: "Zuerst aufgetreten",
    lastObserved: "Zuletzt aufgetreten",
    overview: "Übersicht",
    categoryLabel: "Kategorie",
    target: "Ziel",
    state: "Status",
    activeEntry: "Aktiv",
    historyEntry: "Verlauf",
    category: {
      storage_retry_reset: "Speicherwiederholungen und -zurücksetzungen",
      gpu_reset: "Zurücksetzung des Grafiktreibers",
      memory_exhausted: "Speichererschöpfung",
      desktop_crash: "Absturz eines Desktopprozesses",
      hardware_error: "Hardwarefehler",
      unexpected_shutdown: "Unerwartetes Herunterfahren und Bluescreen"
    },
    targetKindLabel: "Zieltyp",
    targetKind: {
      machine: "Gesamter Computer",
      physical_disk: "Physischer Datenträger",
      storage_port: "Port des Speichercontrollers",
      volume: "Volume",
      gpu_device: "Grafikgerät",
      desktop_process: "Desktopprozess",
      hardware: "Hardware"
    },
    time: "Zeit",
    channel: "Protokoll",
    channelName: {
      system: "System",
      application: "Anwendung"
    },
    provider: "Quelle",
    eventId: "Ereignis-ID",
    recordId: "Datensatz-ID",
    bugcheckCode: "Stoppcode",
    dumpPath: "Speicherabbilddatei",
    failure: (name: string) => `Die Systemprotokolle konnten nicht gelesen werden (${name}). Vorhandene Einträge bleiben erhalten; die Aufzeichnung wird fortgesetzt, sobald das Lesen wieder möglich ist.`,
    liveKernelFailure: (name: string) => `Der Ordner LiveKernelReports konnte nicht durchsucht werden (${name}). Ereignisse aus den Systemprotokollen sind nicht betroffen.`,
    unrecorded: (count: number) => plural("de-DE", count, {
      one: "{count} weiterer Eintrag wurde nicht erfasst, weil die Eintragsgrenze erreicht ist.",
      other: "{count} weitere Einträge wurden nicht erfasst, weil die Eintragsgrenze erreicht ist."
    }),
    items: "Einträge zu Systemereignissen",
    detailsButton: "Details",
    back: "Zurück zur Liste",
    eventsTitle: (count: number) => plural("de-DE", count, {
      one: "Letztes Rohereignis ({count})",
      other: "Letzte Rohereignisse ({count})"
    }),
    xml: "Ereignis-XML"
  },
  recorder: {
    tab: "Hängeraufzeichnung",
    intro: "Der lokale Dienst schreibt fortlaufend eine kleine Menge an Daten zu Arbeitsspeicher, Kernelpools, Datenträgern und Interrupts in eine Aufzeichnungsdatei fester Größe. Friert der Computer ein und wird er zwangsweise ausgeschaltet oder mit einem Bluescreen angehalten, wird beim nächsten Start durch Abgleich mit den Systemprotokollen eine Auswertung nach dem Neustart erstellt. Sie enthält die Aufzeichnungen vor dem Einfrieren, die Systemereignisse aus demselben Zeitraum und die Befunde. Diese Informationen werden hier nur erkannt und gemeldet.",
    loading: "Hängeraufzeichnung wird geladen…",
    readFailed: "Die Hängeraufzeichnung konnte nicht gelesen werden.",
    emptyTitle: "Keine Auswertungen nach dem Neustart",
    emptyDetail: "Seit dem letzten Start wurde kein unerwartetes Herunterfahren und kein Bluescreen festgestellt.",
    recorder: "Aufzeichnung",
    running: "Wird aufgezeichnet",
    stopped: "Keine Aufzeichnung",
    lastRecordAt: "Letzte Aufzeichnung",
    reviewCount: "Auswertungen nach dem Neustart",
    failure: (name: string) => `Die Aufzeichnung ist fehlgeschlagen (${name}) und schreibt keine Datensätze.`,
    stoppedNote: "Die Aufzeichnung läuft nicht; sie wurde möglicherweise in den Einstellungen deaktiviert. Ein Einfrieren während dieser Zeit kann nicht vollständig ausgewertet werden.",
    gapsTitle: "In den aktuellen Aufzeichnungen fehlende Daten",
    gap: {
      "no-sample": "Es wurde noch keine Aufzeichnung geschrieben",
      "interrupt-cpu-share": "Von Interrupts belegte Prozessorzeit",
      "longest-interrupt": "Längster einzelner Interrupt",
      "active-kernel-leak-system-item": "Ob ein systemweiter Kernel-Leck-Eintrag aktiv ist",
      "process-private-commit": "Privater Commit der Prozesse",
      "physical-disk-inventory": "Liste der physischen Datenträger",
      "physical-disk-completed-operations": "Abgeschlossene Vorgänge der physischen Datenträger",
      commit: "Commit und Commitgrenze",
      "physical-memory": "Physischer Arbeitsspeicher",
      "kernel-pools": "Ausgelagerter und nicht ausgelagerter Pool"
    },
    reviews: "Auswertungen nach dem Neustart",
    reviewTitle: (time: string) => `Unerwartetes Herunterfahren · ${time}`,
    shutdownAt: "Zeitpunkt des Herunterfahrens oder Bluescreens",
    gapDuration: "Abstand",
    bugcheckCode: "Stoppcode",
    tagsTitle: "Befunde",
    noTags: "Keine Befunde.",
    inputsIncomplete: "Einige Aufzeichnungen oder Systemprotokolle konnten bei der Auswertung nicht vollständig gelesen werden; das Ergebnis ist möglicherweise unvollständig.",
    tag: {
      bugcheck: {
        name: "Bluescreen",
        description: "Die Systemprotokolle enthalten einen Bluescreen; der Stoppcode steht in der Übersicht."
      },
      "commit-exhausted": {
        name: "Commit erschöpft",
        description: "Der aufgezeichnete Commit erreichte die Sofortgrenze des Speicherschutzes, oder das System meldete im selben Zeitraum eine Speichererschöpfung."
      },
      "physical-exhausted": {
        name: "Physischer Arbeitsspeicher erschöpft",
        description: "Der aufgezeichnete verfügbare physische Arbeitsspeicher lag unter dem in den Einstellungen festgelegten Anteil."
      },
      "pool-high": {
        name: "Kernelpool zu hoch",
        description: "Der nicht ausgelagerte Pool überschritt den festgelegten Anteil des physischen Arbeitsspeichers, oder ein systemweiter Kernel-Leck-Eintrag war aktiv."
      },
      "storage-unresponsive": {
        name: "Speicher reagiert nicht",
        description: "Im selben Zeitraum traten Speicherwiederholungen oder -zurücksetzungen auf, oder ein Datenträger erfüllte die Bedingung „reagiert nicht“ mehrmals hintereinander."
      },
      "gpu-reset": {
        name: "Zurücksetzung des Grafiktreibers",
        description: "Im selben Zeitraum wurde der Grafiktreiber zurückgesetzt."
      },
      "hardware-error": {
        name: "Hardwarefehler",
        description: "Das System meldete im selben Zeitraum einen Hardwarefehler."
      },
      "recording-stopped-normal": {
        name: "Aufzeichnung im Normalzustand beendet",
        description: "Keiner der obigen Fälle trat auf, und die letzten Aufzeichnungen waren alle unauffällig. Mögliche Ursachen sind ein blockierter Speicher- oder Filtertreiber, ein plötzlicher Stromausfall oder das Gedrückthalten des Netzschalters."
      }
    },
    details: {
      button: "Details",
      back: "Zurück zur Liste",
      loading: "Auswertung wird geladen…",
      readFailed: "Die Auswertung konnte nicht gelesen werden.",
      export: "Exportieren",
      exporting: "Wird exportiert",
      exportFailed: "Die Auswertung konnte nicht exportiert werden.",
      overview: "Übersicht",
      reviewWindow: "Auswertungszeitraum",
      timeRange: (from: string, to: string) => `${from} – ${to}`,
      samplesSeen: "Aufzeichnungen im Zeitraum",
      inputsComplete: "Daten vollständig",
      yes: "Ja",
      no: "Nein",
      dumpPath: "Speicherabbilddatei",
      thresholdsTitle: "Bei der Auswertung verwendete Schwellenwerte",
      commitImmediatePercent: "Sofortgrenze für Commit",
      physicalAvailablePercent: "Untergrenze für verfügbaren physischen Arbeitsspeicher",
      nonpagedPoolPercent: "Obergrenze für nicht ausgelagerten Pool (Anteil am physischen Arbeitsspeicher)",
      storageConsecutive: "Aufeinanderfolgende Aufzeichnungen ohne Datenträgerreaktion",
      sampleInterval: "Aufzeichnungsintervall",
      reviewWindowLength: "Länge des Auswertungszeitraums",
      curvesTitle: "Aufzeichnungsverläufe",
      curve: {
        commit: "Commit (Anteil an der Commitgrenze)",
        physicalAvailable: "Verfügbarer physischer Arbeitsspeicher (Anteil am Gesamtwert)",
        nonpagedPool: "Nicht ausgelagerter Pool",
        pagedPool: "Ausgelagerter Pool",
        diskQueue: "Warteschlangenlänge der Datenträger (höchste)",
        diskLatency: "Latenz der Datenträger (höchste)",
        interrupt: "Von Interrupts belegte Prozessorzeit"
      },
      curveRange: (min: string, max: string) => `${min} – ${max}`,
      noCurveData: "Keine Daten",
      noRecords: "Im Auswertungszeitraum gibt es keine Aufzeichnungen.",
      lastRecordTitle: "Letzte Aufzeichnung",
      time: "Zeit",
      commit: "Commit",
      physicalAvailable: "Verfügbarer physischer Arbeitsspeicher",
      ofLimit: (formattedValue: string, formattedLimit: string) => `${formattedValue} / ${formattedLimit}`,
      longestInterrupt: "Längster einzelner Interrupt",
      kernelLeakActive: "Systemweiter Kernel-Leck-Eintrag aktiv",
      processesTitle: "Prozesse mit dem höchsten privaten Commit",
      processName: (name: string, pid: string) => `${name} (PID ${pid})`,
      diskName: (id: string) => `Physischer Datenträger ${id}`,
      queueDepth: "Warteschlangenlänge",
      latency: "Latenz",
      completedOperations: "Abgeschlossene Vorgänge",
      eventsTitle: (count: number) => plural("de-DE", count, {
        one: "Systemereignis im Zeitraum ({count})",
        other: "Systemereignisse im Zeitraum ({count})"
      }),
      noEvents: "Im Auswertungszeitraum wurden keine Systemereignisse empfangen.",
      actualShutdownAt: "Tatsächlicher Zeitpunkt des Herunterfahrens"
    }
  },
  settings: {
    open: "Einstellungen",
    back: "Zurück zum Bericht",
    systemEventsTitle: "Einstellungen für Systemereignisse",
    recorderTitle: "Einstellungen für die Hängeraufzeichnung",
    readFailed: "Die Einstellungen konnten nicht gelesen werden.",
    save: "Speichern",
    saving: "Wird gespeichert",
    discard: "Änderungen verwerfen",
    saved: "Die Einstellungen wurden gespeichert.",
    saveFailed: "Die Einstellungen konnten nicht gespeichert werden.",
    invalid: "Die Einstellungen haben die Prüfung nicht bestanden und wurden nicht gespeichert. Prüfen Sie die Werte.",
    conflict: "Die Einstellungen wurden nicht gespeichert: Regeltabelle, Anzahl der Aufzeichnungen und Speicherort der Aufzeichnungsdatei können während des Betriebs nicht geändert werden. Deaktivieren Sie zuerst die betreffende Funktion und speichern Sie, und ändern Sie danach diese Einträge. Ist bereits eine Aufzeichnungsdatei vorhanden, erfordert eine geänderte Anzahl der Aufzeichnungen zusätzlich einen neuen Speicherort.",
    resourceBound: "Die Einstellungen wurden nicht gespeichert: Die benötigten Ressourcen überschreiten die Grenze. Verringern Sie die Kapazität oder die Anzahl der Aufzeichnungen.",
    draftInvalid: "Die aktuellen Änderungen sind unvollständig (zum Beispiel fehlt einer Regel der Quellname) und können nicht gespeichert werden.",
    rulesLocked: "Die Regeltabelle kann nicht geändert werden, während die Aufzeichnung von Systemereignissen aktiviert ist. Deaktivieren Sie sie zuerst und speichern Sie.",
    recorderLocked: "Dieser Eintrag kann nicht geändert werden, während die Aufzeichnung aktiviert ist. Deaktivieren Sie sie zuerst und speichern Sie.",
    units: {
      minutes: "Minuten",
      seconds: "Sekunden",
      milliseconds: "Millisekunden",
      percent: "Prozent",
      count: "Anzahl"
    },
    systemEvents: {
      enabled: {
        title: "Systemereignisse aufzeichnen",
        description: "Ereignisse aus den Systemprotokollen empfangen und zu Einträgen zusammenfassen. Nach dem Deaktivieren werden keine neuen Ereignisse empfangen; vorhandene Einträge bleiben erhalten."
      },
      historyAfter: {
        title: "In den Verlauf verschieben nach",
        description: "Ein Eintrag wird in den Verlauf verschoben, wenn nach seinem letzten Ereignis so lange kein neues Ereignis eintrifft."
      },
      eventTailCapacity: {
        title: "Gespeicherte Rohereignisse je Eintrag",
        description: "Wie viele der letzten Rohereignisse jeder Eintrag für die Detailseite speichert."
      },
      entryCapacity: {
        title: "Eintragsgrenze",
        description: "Die höchste Anzahl gespeicherter Einträge; nach Erreichen der Grenze werden neue Einträge nur gezählt, nicht erfasst."
      },
      liveKernelRoot: {
        title: "Speicherort von LiveKernelReports",
        description: "Der Ordner, in dem das System Live-Kernelabbildberichte ablegt; er dient zum Auffinden von Berichten wie Zeitüberschreitungen des Grafiktreibers."
      },
      liveKernelDirectories: {
        title: "Durchsuchte Unterordner",
        description: "Unterordner, die unter dem obigen Speicherort durchsucht werden, einer pro Zeile."
      },
      liveKernelEntryCapacity: {
        title: "Grenze für Abbildberichte",
        description: "Die höchste Anzahl von Abbildberichten, die bei jedem Durchsuchen erfasst werden."
      },
      rules: {
        title: "Ereignisregeln",
        description: "Welche Ereignisse der Systemprotokolle zu welcher Kategorie gehören und auf welchen Zieltyp sich jedes Ereignis bezieht."
      }
    },
    recorder: {
      enabled: {
        title: "Hängerdaten aufzeichnen",
        description: "Fortlaufend Aufzeichnungen schreiben und nach einem unerwarteten Herunterfahren beim nächsten Start eine Auswertung erstellen. Nach dem Deaktivieren endet die Aufzeichnung; die Aufzeichnungsdatei bleibt erhalten."
      },
      sampleInterval: {
        title: "Aufzeichnungsintervall",
        description: "Die Zeit zwischen zwei Aufzeichnungen. Je kürzer das Intervall, desto näher liegt die letzte Aufzeichnung am Zeitpunkt des Einfrierens."
      },
      reviewWindow: {
        title: "Auswertungszeitraum",
        description: "Wie weit vor der letzten Aufzeichnung die Auswertung Aufzeichnungen und Systemereignisse anzeigt."
      },
      commitImmediate: {
        title: "Sofortgrenze für Commit",
        description: "Die Grenze zur Erkennung einer Commit-Erschöpfung. Sie folgt der Sofortgrenze des Speicherschutzes und wird in dessen Einstellungen geändert."
      },
      commitImmediateNone: "Nicht festgelegt",
      physicalAvailable: {
        title: "Untergrenze für verfügbaren physischen Arbeitsspeicher",
        description: "Eine Erschöpfung des physischen Arbeitsspeichers wird festgestellt, wenn der verfügbare Anteil unter diesen Prozentsatz des Gesamtwerts fällt."
      },
      nonpagedPool: {
        title: "Obergrenze für nicht ausgelagerten Pool",
        description: "Ein zu hoher Kernelpool wird festgestellt, wenn der nicht ausgelagerte Pool diesen Prozentsatz des physischen Arbeitsspeichers überschreitet."
      },
      storageConsecutive: {
        title: "Aufeinanderfolgende Aufzeichnungen ohne Datenträgerreaktion",
        description: "Ein nicht reagierender Speicher wird festgestellt, wenn ein Datenträger die Bedingung in so vielen Aufzeichnungen hintereinander erfüllt."
      },
      slots: {
        title: "Anzahl der Aufzeichnungen",
        description: "Wie viele Aufzeichnungen die Aufzeichnungsdatei fasst; dies bestimmt, wie weit die Auswertung zurückreicht. Ist bereits eine Aufzeichnungsdatei vorhanden, erfordert eine Änderung auch einen neuen Speicherort."
      },
      ringPath: {
        title: "Speicherort der Aufzeichnungsdatei",
        description: "Der vollständige Pfad der Aufzeichnungsdatei. Leer lassen, um den Standardspeicherort zu verwenden."
      },
      ringPathDefault: "Standardspeicherort"
    },
    ruleTable: {
      channel: "Protokoll",
      provider: "Quellname",
      ids: "Ereignis-IDs",
      idsHint: "Durch Kommas getrennt",
      category: "Kategorie",
      targetKind: "Zieltyp",
      remove: "Entfernen",
      add: "Regel hinzufügen"
    }
  }
};
