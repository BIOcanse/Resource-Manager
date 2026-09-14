import { createSettingsLocale } from "../settingsLocaleFactory";

export default createSettingsLocale("vi-VN", {
  sections: { performance: "Hiệu năng", appearance: "Giao diện", systemIntegration: "Tích hợp hệ thống", debug: "Gỡ lỗi", credits: "Lời cảm ơn" },
  saveState: { saving: "Đang lưu", saved: "Đã lưu", error: "Lưu thất bại" },
  appearance: { languageTitle: "Ngôn ngữ giao diện", languageDescription: "Chọn ngôn ngữ dùng trong giao diện Resource Manager. Hệ thống dùng ngôn ngữ hiển thị của Windows.", languageSelectLabel: "Ngôn ngữ" }
});
