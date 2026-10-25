import type { BackendMessage } from "../../types.ts";

/** Backend facts only. Presentation belongs to presentation/metricPresentation.ts. */
export interface RawMetricIdentity {
  fieldCode?: number;
  fieldName?: string | null;
  deviceIdentity?: string | null;
  sourceId?: string | null;
  valueKind?: string | null;
}

export interface RawMetricDefinition extends RawMetricIdentity {
  id: string;
  unit: string;
  requiredComponentId?: string | null;
  selectable?: boolean;
  disabledReason?: BackendMessage | null;
  scopeKind?: string | null;
  scopeKey?: string | null;
}

export interface RawMetricValue extends RawMetricIdentity {
  id: string;
  unit: string;
  numericValue: number | null;
  /** Exact signed64/unsigned128 decimal text; never parse through Number. */
  integerValue?: string | null;
  /** System.Text.Json byte[] encoding. */
  bytesValue?: string | null;
  booleanValue?: boolean | null;
  percent?: number | null;
  total?: number | null;
  status: number;
  observedAt?: string | null;
  sampleDurationMilliseconds?: number | null;
}
