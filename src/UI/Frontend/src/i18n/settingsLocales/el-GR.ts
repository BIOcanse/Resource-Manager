import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("el-GR", {
  sections: { performance: "Απόδοση", appearance: "Εμφάνιση", systemIntegration: "Ενσωμάτωση συστήματος", debug: "Αποσφαλμάτωση", credits: "Ευχαριστίες" },
  saveState: { saving: "Αποθήκευση", saved: "Αποθηκεύτηκε", error: "Η αποθήκευση απέτυχε" },
  appearance: { languageTitle: "Γλώσσα περιβάλλοντος", languageDescription: "Επιλέξτε τη γλώσσα του Resource Manager. Η επιλογή Συστήματος χρησιμοποιεί τη γλώσσα εμφάνισης των Windows.", languageSelectLabel: "Γλώσσα" }
});
