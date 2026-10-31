// Declaration-only generated structure.
export namespace ResourceManager {
export namespace Desktop {
  export namespace DesktopPoints {
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
      readonly overlay_sample_interval_ms: number;
    }
    export interface __fpRuntimeInitialValues {
    }
  }
}
}
