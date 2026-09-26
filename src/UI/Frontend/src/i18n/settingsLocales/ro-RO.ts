import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("ro-RO", {
  sections: { performance: "Performanță", appearance: "Aspect", systemIntegration: "Integrare sistem", debug: "Depanare", credits: "Mulțumiri" },
  saveState: { saving: "Se salvează", saved: "Salvat", error: "Salvarea a eșuat" },
  appearance: { languageTitle: "Limba interfeței", languageDescription: "Alegeți limba interfeței Resource Manager. Sistem folosește limba de afișare Windows.", languageSelectLabel: "Limbă" }
});
