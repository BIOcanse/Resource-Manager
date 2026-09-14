import { createSettingsLocale } from "../settingsLocaleFactory";

export default createSettingsLocale("uk-UA", {
  sections: { performance: "Продуктивність", appearance: "Вигляд", systemIntegration: "Інтеграція із системою", debug: "Налагодження", credits: "Подяки" },
  saveState: { saving: "Збереження", saved: "Збережено", error: "Не вдалося зберегти" },
  appearance: { languageTitle: "Мова інтерфейсу", languageDescription: "Виберіть мову інтерфейсу Resource Manager. Варіант «Система» використовує мову інтерфейсу Windows.", languageSelectLabel: "Мова" }
});
