import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("de-DE", {
  sections: { performance: "Leistung", appearance: "Darstellung", systemIntegration: "Systemintegration", debug: "Debug", credits: "Danksagung" },
  saveState: { saving: "Speichern", saved: "Gespeichert", error: "Speichern fehlgeschlagen" },
  appearance: {
    languageTitle: "Sprache der Benutzeroberfläche",
    languageDescription: "Wählen Sie die Sprache der Resource-Manager-Oberfläche. System verwendet die Windows-Anzeigesprache.",
    languageSelectLabel: "Sprache",
    themeOptions: { system: "System", light: "Hell", dark: "Dunkel", lowContrast: "Niedriger Kontrast" }
  },
  credits: { heroTitle: "Danke an alle Menschen und Projekte, die Resource Manager möglich machen", groups: { project: "Projekt und Beitrag", runtime: "Runtime und Build-Tools", windows: "Windows- und Diagnose-Schnittstellen", hardware: "Hardwareüberwachung und optionale Unterstützung" } }
});
