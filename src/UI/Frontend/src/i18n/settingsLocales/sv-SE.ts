import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("sv-SE", {
  sections: { performance: "Prestanda", appearance: "Utseende", systemIntegration: "Systemintegration", debug: "Felsökning", credits: "Tack" },
  saveState: { saving: "Sparar", saved: "Sparat", error: "Det gick inte att spara" },
  appearance: { languageTitle: "Gränssnittsspråk", languageDescription: "Välj språk för Resource Manager-gränssnittet. System använder Windows visningsspråk.", languageSelectLabel: "Språk" }
});
