import { createSettingsLocale } from "../settingsLocaleFactory";

export default createSettingsLocale("hu-HU", {
  sections: { performance: "Teljesítmény", appearance: "Megjelenés", systemIntegration: "Rendszerintegráció", debug: "Hibakeresés", credits: "Köszönet" },
  saveState: { saving: "Mentés", saved: "Mentve", error: "A mentés sikertelen" },
  appearance: { languageTitle: "Felület nyelve", languageDescription: "Válassza ki a Resource Manager felületének nyelvét. A Rendszer a Windows megjelenítési nyelvét használja.", languageSelectLabel: "Nyelv" }
});
