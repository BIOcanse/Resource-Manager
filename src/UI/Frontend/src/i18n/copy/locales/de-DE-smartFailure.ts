import { plural } from "../../plural.ts";
import type { AppCopy } from "../index.ts";

const nativePlanStatuses: AppCopy["smartFailure"]["nativePlanStatuses"] = {
  "invalid-argument": "Ungültiges Argument",
  "abi-mismatch": "Schnittstellenversion der nativen Komponente stimmt nicht überein",
  unavailable: "Native Komponente nicht verfügbar",
  "no-data": "Keine Daten verfügbar",
  "buffer-too-small": "Pufferkapazität nicht ausreichend",
  "stale-generation": "Eingabedaten sind veraltet",
  "out-of-memory": "Nicht genügend Arbeitsspeicher",
  "capacity-exceeded": "Kapazitätsgrenze überschritten",
  "conflicting-facts": "Eingabedaten widersprechen sich",
  "feedback-mismatch": "Ausführungsrückmeldung stimmt nicht mit dem Plan überein",
  "recreate-required": "Der native Arbeitsbereich muss neu erstellt werden"
};

export const smartFailure: AppCopy["smartFailure"] = {
  title: {
    "cycle-failed": "Dieser Steuerungsabschnitt ist fehlgeschlagen",
    "native-plan-failed": "Der native Steuerungsplan ist fehlgeschlagen"
  },
  hint: {
    "cycle-failed": "Bei der Ausführung dieses Steuerungsabschnitts ist ein Fehler aufgetreten; er wurde nicht abgeschlossen. Dieser Hinweis verschwindet, sobald ein späterer Abschnitt erfolgreich abgeschlossen wird. Einzelheiten zum Fehler werden im Protokoll des lokalen Diensts festgehalten.",
    "native-plan-failed": "Die native Steuerungskomponente konnte für diesen Abschnitt keinen Plan erstellen; in diesem Abschnitt wurden keine Anpassungen vorgenommen. Der Planstatus nennt die Ursache. Dieser Hinweis verschwindet, sobald ein späterer Abschnitt erfolgreich abgeschlossen wird."
  },
  nativePlanStatus: (status: string) => `Planstatus: ${status}`,
  nativePlanStatuses,
  exclusionTotal: (count: number) => plural("de-DE", count, {
    one: "{count} Datenelement wurde in diesem Abschnitt ausgeschlossen",
    other: "{count} Datenelemente wurden in diesem Abschnitt ausgeschlossen"
  }),
  exclusionsHint: "Ausgeschlossene Daten sind Daten, die in diesem Abschnitt nicht in die Steuerungsentscheidungen eingegangen sind, weil sie fehlten, ungültig oder widersprüchlich waren. Die Übersicht zählt die Ausschlüsse nach Datenelement und Ursache; die Einzelheiten nennen die betroffenen Objekte.",
  summaryLabel: "Übersicht der Ausschlüsse",
  viewExclusions: "Ausschlüsse im Einzelnen",
  detailTitle: "In diesem Abschnitt ausgeschlossene Daten",
  detailSummary: (listed: number, total: number) => `Insgesamt ausgeschlossen: ${total}; unten aufgeführt: ${listed}.`,
  field: {
    processId: "Prozess-ID",
    processStartKey: "Prozessstartkennung",
    targetId: "Kennung des Steuerungsziels",
    target: "Steuerungsziel",
    adapterKey: "GPU-Adapterkennung"
  },
  noTarget: "Nicht auf ein bestimmtes Objekt bezogen",
  facts: {
    inventory: "Prozessliste",
    identity: "Prozessidentität",
    attribution: "Softwarezuordnung",
    "base-score": "Basiswert",
    "cpu-usage": "CPU-Auslastung des Prozesses",
    "memory-usage": "Speichernutzung des Prozesses",
    "gpu-usage": "GPU-Auslastung des Prozesses",
    "gpu-dedicated-memory": "Dedizierte VRAM-Nutzung des Prozesses",
    "gpu-private-memory-bytes": "Residenter privater GPU-Speicher des Prozesses",
    "gpu-shared-memory-bytes": "Residenter gemeinsam genutzter GPU-Speicher des Prozesses",
    "runtime-state": "Laufzustand des Prozesses",
    "cpu-core-residency": "CPU-Kernbelegung",
    "compute-score": "Rechenleistungsbewertung",
    "cpu-score": "CPU-Bewertung",
    "gpu-score": "GPU-Bewertung",
    "decision-target": "Entscheidungsziel",
    "memory-cleanup": "Speicherbereinigung",
    "native-plan": "Nativer Steuerungsplan"
  },
  reasons: {
    "dataset-not-current": "Daten stammen nicht aus der aktuellen Stichprobe",
    "unidentified-live-process": "Ein laufender Prozess konnte nicht identifiziert werden",
    "inventory-overflow": "Die Prozessliste hat ihre Kapazität überschritten",
    "emitted-count-mismatch": "Ausgabeanzahl stimmt nicht mit der Prozessliste überein",
    "inventory-count-mismatch": "Listenanzahl stimmt nicht mit der Aufzählung überein",
    "capture-count-mismatch": "Erfassungszähler sind widersprüchlich",
    "invalid-or-duplicate-inventory-identity": "Die Prozessliste enthält eine ungültige oder doppelte Prozessidentität",
    "invalid-or-duplicate-process": "Ungültige oder doppelte Prozessidentität",
    "row-missing-or-invalid": "Daten fehlen oder sind ungültig",
    "gpu-row-missing-or-invalid": "GPU-Daten fehlen oder sind ungültig",
    "compute-runtime-state-missing": "Laufzustand fehlt",
    "cpu-residency-not-covered": "Die CPU-Belegungserfassung deckt diesen Prozess nicht ab",
    "cpu-residency-invalid": "CPU-Belegungsdaten ungültig",
    "target-process-count-not-one": "Das Steuerungsziel entspricht nicht genau einem Prozess",
    "target-process-identity-missing": "Dem Steuerungsziel fehlt die Prozessidentität",
    "target-process-identity-mismatch": "Die Prozessidentität des Steuerungsziels ist widersprüchlich",
    "duplicate-target-process-identity": "Mehrere Steuerungsziele verweisen auf denselben Prozess",
    "target-runtime-state-invalid": "Der Laufzustand des Steuerungsziels ist ungültig",
    "cleanup-baseline-score-missing": "Ausgangswert der Bereinigung fehlt",
    "cleanup-current-process-facts-missing": "Aktuelle Prozessdaten fehlen",
    "cleanup-software-maximum-base-score-missing": "Der höchste Basiswert der Software fehlt",
    "ownership-software-attribution-mismatch": "Die Zuständigkeit für Änderungen stimmt nicht mit der Softwarezuordnung überein",
    "canonical-process-membership-mismatch": "Der Prozess stimmt nicht mit den Softwaremitgliedern der Bewertung überein",
    "canonical-software-score-missing": "Softwarebewertung fehlt",
    "live-identity-without-decision-facts": "Für einen laufenden Prozess fehlen Entscheidungsdaten",
    "canonical-process-not-projected": "Die Prozessbewertung ist nicht in die Steuerungseingabe eingegangen",
    "canonical-software-membership-incomplete": "Die Mitglieder der Softwarebewertung sind unvollständig",
    ...nativePlanStatuses
  }
};
