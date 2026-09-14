import { createSettingsLocale } from "../settingsLocaleFactory";

export default createSettingsLocale("ja-JP", {
  sections: { performance: "パフォーマンス", appearance: "外観", systemIntegration: "システム統合", debug: "デバッグ", credits: "クレジット" },
  saveState: { saving: "保存中", saved: "保存済み", error: "保存に失敗" },
  appearance: {
    languageTitle: "表示言語",
    languageDescription: "Resource Manager の画面で使用する言語を選択します。「システム」は Windows の表示言語を使用します。",
    languageSelectLabel: "言語",
    themeOptions: { system: "システム", light: "ライト", dark: "ダーク", lowContrast: "低コントラスト" },
    animationOptions: {
      auto: { label: "自動", description: "前面操作中は標準モーションを使い、低電力のバックグラウンド状態では高性能表示に切り替えます" },
      none: { label: "アニメーションなし", description: "すべてのトランジションとアニメーションを無効化します" },
      normal: { label: "標準アニメーション", description: "控えめな全体トランジションを有効化します" },
      ultra: { label: "超高性能", description: "アニメーションと影、ハイライト、ぼかしを無効化し、描画コストを最小化します" }
    }
  },
  credits: {
    heroTitle: "Resource Manager を支えるすべての人とプロジェクトに感謝します",
    heroBody: "このページでは、Resource Manager が利用するオープンソースプロジェクト、実行環境、ハードウェア対応、主な貢献を記録します。",
    groups: { project: "プロジェクトと貢献", runtime: "ランタイムとビルドツール", windows: "Windows と診断インターフェイス", hardware: "ハードウェア監視と追加サポート" },
    linkLabels: { official: "公式", github: "GitHub", docs: "ドキュメント", license: "License", nuget: "NuGet", gpuOpen: "GPUOpen", eula: "EULA", runtime: "Runtime", aspnet: "ASP.NET Core", vite: "Vite", solidPlugin: "Solid プラグイン" }
  }
});
