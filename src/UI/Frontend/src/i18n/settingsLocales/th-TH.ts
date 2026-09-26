import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("th-TH", {
  sections: { performance: "ประสิทธิภาพ", appearance: "รูปลักษณ์", systemIntegration: "การผสานระบบ", debug: "ดีบัก", credits: "ขอบคุณ" },
  saveState: { saving: "กำลังบันทึก", saved: "บันทึกแล้ว", error: "บันทึกไม่สำเร็จ" },
  appearance: { languageTitle: "ภาษาของอินเทอร์เฟซ", languageDescription: "เลือกภาษาที่ใช้ในอินเทอร์เฟซ Resource Manager โดยตัวเลือกระบบจะใช้ภาษาที่แสดงของ Windows", languageSelectLabel: "ภาษา" }
});
