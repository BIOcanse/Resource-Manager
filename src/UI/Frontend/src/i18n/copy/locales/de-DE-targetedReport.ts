import type { AppCopy } from "../index.ts";
export const targetedReport: AppCopy["targetedReport"] = {
  tab: "Berichte zur gezielten Überwachung", title: "Berichte zur gezielten Überwachung",
  chooseSoftware: "Software für die Aufzeichnung wählen",
  noSoftware: "Keine registrierte Software. Registrieren Sie das Ziel zuerst in der Softwareverwaltung.",
  oneAtATime: "Es kann nur eine Aufzeichnung gleichzeitig laufen. Beenden Sie zuerst die aktuelle Aufzeichnung.",
  workflow: "Wählen Sie registrierte, laufende Software oder starten Sie in deren Detailübersicht. Die Vorgabe beträgt 600 Sekunden, einstellbar von 1 bis 3600. Manuelles Stoppen, das Ende der Prozesse oder das Zeitlimit schließen den Bericht ab. Die letzten 50 abgeschlossenen Aufzeichnungen bleiben gespeichert.",
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
