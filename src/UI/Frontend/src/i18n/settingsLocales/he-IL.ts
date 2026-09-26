import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("he-IL", {
  sections: { performance: "ביצועים", appearance: "מראה", systemIntegration: "שילוב מערכת", debug: "ניפוי שגיאות", credits: "תודות" },
  saveState: { saving: "שומר", saved: "נשמר", error: "השמירה נכשלה" },
  appearance: { languageTitle: "שפת הממשק", languageDescription: "בחרו את השפה של ממשק Resource Manager. האפשרות מערכת משתמשת בשפת התצוגה של Windows.", languageSelectLabel: "שפה" }
});
