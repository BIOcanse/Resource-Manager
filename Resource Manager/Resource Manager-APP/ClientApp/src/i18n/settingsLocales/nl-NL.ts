import { createSettingsLocale } from "../settingsLocaleFactory";

export default createSettingsLocale("nl-NL", {
  sections: { performance: "Prestaties", appearance: "Uiterlijk", systemIntegration: "Systeemintegratie", debug: "Debug", credits: "Dankwoord" },
  saveState: { saving: "Opslaan", saved: "Opgeslagen", error: "Opslaan mislukt" },
  appearance: { languageTitle: "Interfacetaal", languageDescription: "Kies de taal van de Resource Manager-interface. Systeem gebruikt de weergavetaal van Windows.", languageSelectLabel: "Taal" }
});
