// Declaration-only generated structure.
export namespace ResourceManager {
  export namespace FrontendPoints {
    export type __fpDeepReadonly<T> = T extends (...args: never[]) => unknown ? T :
      T extends readonly unknown[] ? { readonly [K in keyof T]: __fpDeepReadonly<T[K]> } :
      T extends object ? { readonly [K in keyof T]: __fpDeepReadonly<T[K]> } : T;
    export interface __fpContractSlot {
      readonly kind: string;
      readonly unit: string | null;
      readonly integerBitWidth: number | null;
      readonly enumeration: { readonly isUnsigned: boolean; readonly bitWidth: number; readonly members: readonly { readonly name: string; readonly literal: string }[] } | null;
      readonly integerMinimum: bigint | null;
      readonly integerMaximum: bigint | null;
      readonly integerStep: bigint | null;
      readonly integerMinimumPoint: string | null;
      readonly unsignedMinimum: bigint | null;
      readonly unsignedMaximum: bigint | null;
      readonly unsignedStep: bigint | null;
      readonly unsignedMaximumPoint: string | null;
      readonly decimalMinimum: number | null;
      readonly decimalMaximum: number | null;
      readonly options: readonly string[];
      readonly choiceOptionsUnresolved: boolean;
      readonly requiredSelectionCount: number;
    }
    export interface __fpContractDescriptor {
      readonly complexContract: string | null;
      readonly slots: readonly __fpContractSlot[];
    }
    export interface __fpBuildValues {
      readonly BarFrameRate: number;
      readonly UiStyle: __fpDeepReadonly<"standard" | "soft" | "outline" | "terminal">;
      readonly Theme: __fpDeepReadonly<"system" | "light" | "dark" | "lowContrast" | "paper" | "graphite" | "blueprint" | "phosphor" | "amber" | "ice">;
      readonly AnimationMode: __fpDeepReadonly<"auto" | "none" | "normal" | "ultra">;
      readonly MotionFeel: __fpDeepReadonly<"standard" | "smooth" | "crisp">;
      readonly BarFrameRateMode: __fpDeepReadonly<"display" | "custom">;
      readonly BarColorMode: __fpDeepReadonly<"type" | "distinct">;
      readonly FontSmoothing: __fpDeepReadonly<"auto" | "system" | "grayscale" | "disabled">;
      readonly Language: __fpDeepReadonly<"system" | "zh-CN" | "zh-TW" | "en-US" | "ja-JP" | "ko-KR" | "fr-FR" | "de-DE" | "es-ES" | "es-MX" | "pt-BR" | "pt-PT" | "ru-RU" | "uk-UA" | "pl-PL" | "tr-TR" | "it-IT" | "nl-NL" | "sv-SE" | "fi-FI" | "da-DK" | "nb-NO" | "cs-CZ" | "hu-HU" | "ro-RO" | "el-GR" | "he-IL" | "ar-SA" | "hi-IN" | "id-ID" | "vi-VN" | "th-TH">;
      readonly ByteUnitMode: __fpDeepReadonly<"native" | "binary" | "decimal">;
      readonly ReadingSourceMode: __fpDeepReadonly<"preferred" | "all">;
      readonly Monitor: __fpDeepReadonly<{ readonly mode: "aotu" | "preset" | "custom"; readonly preset: "responsive" | "balanced" | "lowPower" | "quiet"; readonly customValue: number }>;
      readonly ResourceTable: __fpDeepReadonly<{ readonly mode: "aotu" | "preset" | "custom"; readonly preset: "responsive" | "balanced" | "lowPower" | "quiet"; readonly customValue: number }>;
      readonly LocalSystem: __fpDeepReadonly<{ readonly mode: "aotu" | "preset" | "custom"; readonly preset: "responsive" | "balanced" | "lowPower" | "quiet"; readonly customValue: number }>;
      readonly MonitorPresets: __fpDeepReadonly<{ readonly responsive: number; readonly balanced: number; readonly lowPower: number; readonly quiet: number }>;
      readonly ResourceTablePresets: __fpDeepReadonly<{ readonly responsive: number; readonly balanced: number; readonly lowPower: number; readonly quiet: number }>;
      readonly LocalSystemPresets: __fpDeepReadonly<{ readonly responsive: number; readonly balanced: number; readonly lowPower: number; readonly quiet: number }>;
      readonly AutoForegroundPreset: __fpDeepReadonly<"responsive" | "balanced" | "lowPower" | "quiet">;
      readonly AutoBackgroundPreset: __fpDeepReadonly<"responsive" | "balanced" | "lowPower" | "quiet">;
      readonly FontSizePx: number;
      readonly RegionWidthRatio: number;
      readonly RegionHeightRatio: number;
      readonly MarginPx: number;
      readonly SizeMode: __fpDeepReadonly<"absolutePixels" | "windowRatio">;
      readonly Anchor: __fpDeepReadonly<"topLeft" | "topRight" | "bottomLeft" | "bottomRight">;
      readonly MetricOrder: __fpDeepReadonly<readonly string[]>;
      readonly DashboardLayoutDefaults: __fpDeepReadonly<{ readonly builtInMetricIds: readonly string[]; readonly cards: readonly { readonly id: string; readonly main: string; readonly small: readonly string[]; readonly scope: "system" | "gpu"; readonly required: boolean }[]; readonly resourceBars: readonly { readonly id: string; readonly metricId: string; readonly scaleMode: "capacity" | "active"; readonly scope: "system" | "gpu"; readonly required: boolean }[]; readonly softwareColumns: readonly { readonly id: string; readonly visible: boolean; readonly width: number; readonly scope: "system" | "gpu" }[]; readonly processColumns: readonly { readonly id: string; readonly visible: boolean; readonly width: number; readonly scope: "system" | "gpu" }[] }>;
      readonly DashboardLayoutLimits: __fpDeepReadonly<{ readonly smallMetricLimit: number; readonly cardUnits: number; readonly minimumColumnWidth: number; readonly maximumColumnWidth: number; readonly defaultColumnWidth: number }>;
      readonly UpdateInApp: __fpDeepReadonly<"on" | "off">;
      readonly UpdateSystem: __fpDeepReadonly<"on" | "off">;
      readonly AccessRequestSystem: __fpDeepReadonly<"on" | "off">;
    }
    export interface __fpRuntimeInitialValues {
    }
  }
}
