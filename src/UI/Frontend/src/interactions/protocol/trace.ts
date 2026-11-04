// Trace entries contain complete trigger chains for display and batched local delivery.

export type TraceKind = "输入" | "宿主" | "到位" | "参数" | "检查" | "错误" | "未读取" | "移除";

export interface TraceEntry {
  seq: number;
  t: number;
  kind: TraceKind;
  /** Component name; empty for input-layer or global entries. */
  component: string;
  summary: string;
  lines: string[];
  /** Consecutive identical entries merge in the view. */
  group?: string;
  /** One-based transition row used for highlighting. */
  row?: number;
}

export type TraceSink = (entry: TraceEntry) => void;

export class Trace {
  private seq = 0;
  private readonly sinks: TraceSink[] = [];

  subscribe(sink: TraceSink): void {
    this.sinks.push(sink);
  }

  write(entry: Omit<TraceEntry, "seq">): TraceEntry {
    const full = { seq: ++this.seq, ...entry };
    for (const s of this.sinks) s(full);
    return full;
  }
}
