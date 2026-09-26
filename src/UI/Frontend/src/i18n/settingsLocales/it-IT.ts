import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("it-IT", {
  sections: { performance: "Prestazioni", appearance: "Aspetto", systemIntegration: "Integrazione di sistema", debug: "Debug", credits: "Ringraziamenti" },
  saveState: { saving: "Salvataggio", saved: "Salvato", error: "Salvataggio non riuscito" },
  appearance: { languageTitle: "Lingua dell’interfaccia", languageDescription: "Scegli la lingua dell’interfaccia di Resource Manager. Sistema usa la lingua di visualizzazione di Windows.", languageSelectLabel: "Lingua" }
});
