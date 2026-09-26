import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("id-ID", {
  sections: { performance: "Kinerja", appearance: "Tampilan", systemIntegration: "Integrasi sistem", debug: "Debug", credits: "Kredit" },
  saveState: { saving: "Menyimpan", saved: "Tersimpan", error: "Gagal menyimpan" },
  appearance: { languageTitle: "Bahasa antarmuka", languageDescription: "Pilih bahasa antarmuka Resource Manager. Sistem menggunakan bahasa tampilan Windows.", languageSelectLabel: "Bahasa" }
});
