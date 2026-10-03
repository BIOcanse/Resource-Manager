import type { AppCopy } from "../index.ts";
export const targetedReport: AppCopy["targetedReport"] = {
  tab: "Gezielte Berichte", title: "Berichte zur gezielten Überwachung",
  intro: "Erfasst Present-Bildzeiten und Ressourcennutzung aller Prozesse dieser Software.",
  maximumSeconds: "Maximale Aufnahmedauer (Sekunden)", start: "Aufnahme starten", stop: "Aufnahme beenden",
  recording: "Aufnahme läuft", completed: "Abgeschlossen", refresh: "Berichte aktualisieren",
  empty: "Noch keine Aufnahmen vorhanden.", loading: "Bericht wird geladen…",
  failed: "Berichtsvorgang fehlgeschlagen.", delete: "Bericht löschen",
  confirmDelete: "Diesen Aufnahmebericht löschen?", exportJson: "JSON exportieren", exportCsv: "CSV exportieren",
  frameTime: "Bildzeit im Zeitverlauf", distribution: "FPS-Verteilung", resources: "Ressourcenverlauf",
  noFrames: "Keine Bildintervalle verfügbar.", applicationPresent: "Present-Bilder der Anwendung",
  incomplete: "Die Aufnahme enthält Lücken; Statistiken können zu niedrig sein.",
  averageFps: "Durchschnittliche FPS", onePercentLow: "1 % Low", pointOnePercentLow: "0,1 % Low",
  p50: "P50-Bildzeit", p95: "P95-Bildzeit", p99: "P99-Bildzeit",
  maxFrameTime: "Maximale Bildzeit", frameCount: "Bildintervalle",
  software: "Zielsoftware", system: "System", seconds: "Sekunden",
  stopReasons: { user: "Manuell beendet", processExit: "Prozesse beendet", maxDuration: "Maximale Dauer erreicht",
    interrupted: "Dienst unterbrochen", captureError: "Aufnahmefehler" }
};
