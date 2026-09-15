import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("tr-TR", {
  sections: { performance: "Performans", appearance: "Görünüm", systemIntegration: "Sistem entegrasyonu", debug: "Hata ayıklama", credits: "Teşekkürler" },
  saveState: { saving: "Kaydediliyor", saved: "Kaydedildi", error: "Kaydedilemedi" },
  appearance: { languageTitle: "Arayüz dili", languageDescription: "Resource Manager arayüzünde kullanılacak dili seçin. Sistem, Windows görüntüleme dilini kullanır.", languageSelectLabel: "Dil" }
});
