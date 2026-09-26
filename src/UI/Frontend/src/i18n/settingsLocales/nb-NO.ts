import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("nb-NO", {
  sections: { performance: "Ytelse", appearance: "Utseende", systemIntegration: "Systemintegrasjon", debug: "Feilsøking", credits: "Takk" },
  saveState: { saving: "Lagrer", saved: "Lagret", error: "Kunne ikke lagre" },
  appearance: { languageTitle: "Språk i grensesnittet", languageDescription: "Velg språket i Resource Manager-grensesnittet. System bruker visningsspråket i Windows.", languageSelectLabel: "Språk" }
});
