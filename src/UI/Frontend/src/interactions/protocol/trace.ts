// 日志：每条记录一段完整的触发链路。页面显示，同时成批写到本地服务。

export type TraceKind = "输入" | "宿主" | "到位" | "参数" | "检查" | "错误" | "未读取" | "移除";

export interface TraceEntry {
  seq: number;
  t: number;
  kind: TraceKind;
  /** 哪个组件；输入层或全局的记录为空。 */
  component: string;
  summary: string;
  lines: string[];
  /** 连续出现、内容相同的记录在页面上合并显示。 */
  group?: string;
  /** 命中的转移表行（从 1 开始），页面用来高亮。 */
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
