import type { AppCopy } from "../index.ts";
export const targetedReport: AppCopy["targetedReport"] = {
  tab: "대상 보고서", title: "대상 소프트웨어 모니터링 보고서",
  intro: "이 소프트웨어의 모든 프로세스에서 앱 Present 프레임 시간과 리소스 사용량을 기록합니다.",
  maximumSeconds: "최대 기록 시간(초)", start: "기록 시작", stop: "기록 중지",
  recording: "기록 중", completed: "완료", refresh: "보고서 새로 고침",
  empty: "기록된 보고서가 없습니다.", loading: "보고서 불러오는 중…",
  failed: "기록 보고서 작업에 실패했습니다.", delete: "보고서 삭제",
  confirmDelete: "이 기록 보고서를 삭제할까요?", exportJson: "JSON 내보내기", exportCsv: "CSV 내보내기",
  frameTime: "시간별 프레임 시간", distribution: "FPS 분포", resources: "리소스 추이",
  noFrames: "사용 가능한 프레임 간격이 없습니다.", applicationPresent: "앱 Present 프레임",
  incomplete: "기록에 누락이 있어 통계가 실제보다 낮을 수 있습니다.",
  averageFps: "평균 FPS", onePercentLow: "1% Low", pointOnePercentLow: "0.1% Low",
  p50: "P50 프레임 시간", p95: "P95 프레임 시간", p99: "P99 프레임 시간",
  maxFrameTime: "최대 프레임 시간", frameCount: "프레임 간격 수",
  software: "대상 소프트웨어", system: "시스템", seconds: "초",
  stopReasons: { user: "수동 중지", processExit: "프로세스 종료", maxDuration: "최대 시간 도달",
    interrupted: "서비스 중단", captureError: "기록 오류" }
};
