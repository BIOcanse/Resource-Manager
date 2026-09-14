import { createSettingsLocale } from "../settingsLocaleFactory";

export default createSettingsLocale("fi-FI", {
  sections: { performance: "Suorituskyky", appearance: "Ulkoasu", systemIntegration: "Järjestelmäintegraatio", debug: "Virheenkorjaus", credits: "Kiitokset" },
  saveState: { saving: "Tallennetaan", saved: "Tallennettu", error: "Tallennus epäonnistui" },
  appearance: { languageTitle: "Käyttöliittymän kieli", languageDescription: "Valitse Resource Managerin käyttöliittymän kieli. Järjestelmä käyttää Windowsin näyttökieltä.", languageSelectLabel: "Kieli" }
});
