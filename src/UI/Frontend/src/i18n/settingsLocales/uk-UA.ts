import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("uk-UA", {
  sections: { appearance: "Вигляд", systemIntegration: "Інтеграція із системою", debug: "Налагодження", credits: "Подяки" },
  saveState: { saving: "Збереження", saved: "Збережено", error: "Не вдалося зберегти" },
  appearance: { languageTitle: "Мова інтерфейсу", languageDescription: "Виберіть мову інтерфейсу Resource Manager. Варіант «Система» використовує мову інтерфейсу Windows.", languageSelectLabel: "Мова" }
});
