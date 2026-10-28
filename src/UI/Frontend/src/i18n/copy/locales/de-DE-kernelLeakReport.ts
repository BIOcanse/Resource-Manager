import { plural } from "../../plural.ts";
import type { AppCopy } from "../index.ts";

export const kernelLeakReport: AppCopy["kernelLeakReport"] = {
  tab: "Bericht zu Kernel-Lecks",
  intro: "Der lokale Dienst beobachtet während seiner Laufzeit fortlaufend die Kernelressourcen jedes Prozesses (derzeit Prozesshandles) und listet Prozesse auf, deren Ressourcen anhaltend wachsen und möglicherweise unbegrenzt zunehmen, zusammen mit den Nachweisen. Das Ergebnis zeigt nur einen Verdacht an; entscheiden Sie anhand der Nachweise, ob Sie eingreifen.",
  loading: "Bericht zu Kernel-Lecks wird geladen…",
  readFailed: "Der Bericht zu Kernel-Lecks konnte nicht gelesen werden.",
  capturedAt: "Erfasst am",
  suspectedItems: "Verdachtsfälle",
  notObservedTitle: "Kernel-Lecks werden nicht beobachtet",
  notObservedDetail: "Die Beobachtung von Kernel-Lecks läuft derzeit nicht: Sie ist möglicherweise ausgeschaltet, ihr Beobachtungsplan ist noch nicht veröffentlicht, oder sie wurde beendet. Solange sie nicht läuft, werden keine Stichproben erfasst und kein Bericht erstellt.",
  observingTitle: "Beobachtung läuft",
  observingDetail: "Die Bewertung beginnt, sobald Beobachtungsdauer und Anzahl der Stichproben dieses Durchlaufs die Anforderungen erfüllen.",
  emptyTitle: "Keine vermuteten Kernel-Lecks gefunden",
  emptyDetail: "Beobachtungsdauer und Anzahl der Stichproben dieses Durchlaufs erfüllen die Anforderungen; keine Kernelressource wächst anhaltend.",
  unavailableTitle: "Dieser Beobachtungsdurchlauf ist fehlgeschlagen",
  failure: {
    SourceUnavailable: "Die Ressourcendaten der Prozesse konnten nicht gelesen werden.",
    CapacityExceeded: "Die Anzahl der beobachteten Prozesse hat die Kapazitätsgrenze überschritten; dieser Durchlauf wurde beendet.",
    InvalidObservation: "Die Stichprobendaten waren ungültig; dieser Durchlauf wurde beendet."
  },
  observation: {
    title: "Aktueller Durchlauf",
    startedAt: "Beginn des Durchlaufs",
    elapsed: "Beobachtungsdauer",
    samples: "Stichproben",
    notStarted: "Noch keine Stichprobe",
    reachedOfRequired: (reached: string, required: string) => `${reached} (erforderlich: ${required})`
  },
  previousRun: {
    title: "Vorheriger Durchlauf fehlgeschlagen",
    reason: "Grund",
    startedAt: "Beginn des Durchlaufs",
    failedAt: "Fehlgeschlagen am"
  },
  restartPolicy: {
    NextInterval: "Der lokale Dienst beginnt beim nächsten Abtastintervall automatisch einen neuen Durchlauf."
  },
  unavailableHolders: (count: number) => plural("de-DE", count, {
    one: "Die Ressourcendaten von {count} Prozess konnten nicht gelesen werden; er wird bei dieser Beurteilung nicht berücksichtigt.",
    other: "Die Ressourcendaten von {count} Prozessen konnten nicht gelesen werden; sie werden bei dieser Beurteilung nicht berücksichtigt."
  }),
  resourceKind: {
    ProcessHandles: "Prozesshandles"
  },
  holder: {
    processId: "PID",
    productName: "Produktname",
    services: "Dienste",
    imagePath: "Abbildpfad",
    noProductName: "Nicht angegeben",
    noServices: "Kein zugehöriger Dienst",
    unavailable: "Nicht lesbar",
    processEnded: "Prozess beendet"
  },
  evidence: {
    currentValue: "Aktueller Wert",
    growth: "Zunahme im Beobachtungszeitraum",
    growthPerSecond: "Zuwachsrate",
    observationTime: "Beobachtungsdauer",
    sampleCount: "Stichproben"
  },
  ratePerSecond: (value: string) => `${value}/s`,
  actions: {
    title: "Aktionen",
    name: {
      RestartHolder: "Besitzerprozess neu starten",
      ForceReleaseHandles: "Handles erzwungen freigeben"
    },
    riskLevel: {
      Warning: "Warnung",
      High: "Hoch"
    },
    riskText: (level: string, risk: string) => `Risiko: ${level}. ${risk}`,
    riskLabel: (name: string) => `Risiko: ${name}`
  }
};
