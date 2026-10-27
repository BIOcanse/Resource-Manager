import { plural } from "../../plural.ts";
import type { AppCopy } from "../index.ts";

export const kernelLeakReport: AppCopy["kernelLeakReport"] = {
  tab: "Bericht zu Kernel-Lecks",
  intro: "Listet Prozesse auf, deren Kernelressourcen (derzeit Prozesshandles) anhaltend wachsen und möglicherweise unbegrenzt zunehmen, zusammen mit den Nachweisen. Das Ergebnis zeigt nur einen Verdacht an; entscheiden Sie anhand der Nachweise, ob Sie eingreifen. Die Beobachtung läuft nur, solange diese Seite angezeigt wird, und beginnt beim erneuten Öffnen der Seite von vorn.",
  loading: "Bericht zu Kernel-Lecks wird geladen…",
  readFailed: "Der Bericht zu Kernel-Lecks konnte nicht gelesen werden.",
  capturedAt: "Erfasst am",
  suspectedItems: "Verdachtsfälle",
  emptyTitle: "Keine vermuteten Kernel-Lecks gefunden",
  emptyDetail: "Dieses Ergebnis wird auch angezeigt, solange die Beobachtungsdauer für eine Beurteilung noch nicht ausreicht. Gefundene Verdachtsfälle erscheinen hier.",
  notObservedTitle: "Noch keine Stichprobe erfasst",
  unavailableTitle: "Der Bericht zu Kernel-Lecks ist nicht verfügbar",
  failure: {
    NotObserved: "Die Beobachtung hat begonnen. Ergebnisse erscheinen nach der ersten Stichprobe.",
    SourceUnavailable: "Die Ressourcendaten der Prozesse können nicht gelesen werden.",
    CapacityExceeded: "Die Anzahl der beobachteten Prozesse überschreitet die Kapazitätsgrenze. Diese Beobachtung wurde beendet.",
    InvalidObservation: "Die erfassten Daten sind ungültig. Diese Beobachtung wurde beendet."
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
    currentValue: "Aktuelle Anzahl",
    growth: "Zunahme im Beobachtungszeitraum",
    growthPerSecond: "Zuwachsrate",
    observationTime: "Beobachtungsdauer",
    sampleCount: "Stichproben"
  },
  ratePerSecond: (value: string) => `${value}/s`,
  actionsPending: "Der lokale Dienst stellt für diesen Eintrag noch keine Aktionen bereit."
};
