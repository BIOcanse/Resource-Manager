import type { AppCopy } from "../index.ts";

export const kernelLeakReport: AppCopy["kernelLeakReport"] = {
  tab: "커널 누수 보고서",
  intro: "로컬 서비스는 실행되는 동안 각 프로세스가 보유한 커널 리소스(현재는 프로세스 핸들)를 계속 관찰하며, 계속 증가하여 무한히 증가하는 것으로 의심되는 프로세스와 판정 근거를 나열합니다. 판정 결과는 의심을 나타낼 뿐이며, 처리 여부는 판정 근거를 바탕으로 결정하십시오.",
  loading: "커널 누수 보고서를 읽는 중…",
  readFailed: "커널 누수 보고서를 읽지 못했습니다",
  capturedAt: "수집 시각",
  suspectedItems: "의심 항목",
  historyItems: "기록 항목",
  unrecordedItems: "한도 도달로 기록되지 않음",
  items: "보고서 항목",
  historyEntry: "기록",
  notObservedTitle: "커널 누수를 관찰하고 있지 않습니다",
  notObservedDetail: "커널 누수 관찰이 현재 실행되고 있지 않습니다. 꺼져 있거나, 관찰 계획이 아직 게시되지 않았거나, 중지되었을 수 있습니다. 실행되기 전에는 샘플을 수집하지 않으며 보고서도 생성하지 않습니다.",
  observingTitle: "관찰 중",
  observingDetail: "이번 관찰의 시간과 샘플 수가 모두 요구 사항을 충족하면 판정을 시작합니다.",
  emptyTitle: "커널 누수 의심 항목이 없습니다",
  emptyDetail: "이번 관찰의 시간과 샘플 수가 요구 사항을 충족했으며, 계속 증가하는 커널 리소스는 없습니다.",
  unavailableTitle: "이번 관찰에 실패했습니다",
  failure: {
    SourceUnavailable: "프로세스의 리소스 데이터를 읽을 수 없습니다.",
    CapacityExceeded: "관찰 중인 프로세스 수가 용량 한도를 초과하여 이번 관찰을 중지했습니다.",
    InvalidObservation: "샘플 데이터가 유효하지 않아 이번 관찰을 중지했습니다."
  },
  observation: {
    title: "이번 관찰",
    startedAt: "시작 시각",
    elapsed: "관찰 시간",
    samples: "샘플 수",
    notStarted: "아직 샘플 없음",
    reachedOfRequired: (reached: string, required: string) => `${reached}(필요: ${required})`
  },
  previousRun: {
    title: "이전 관찰 실패",
    reason: "원인",
    startedAt: "시작 시각",
    failedAt: "실패 시각"
  },
  restartPolicy: {
    NextInterval: "로컬 서비스는 다음 샘플링 간격에 새 관찰을 자동으로 시작합니다."
  },
  unavailableHolders: (count: number) => `프로세스 ${count}개의 리소스 데이터를 읽지 못해 이번 판정에 포함하지 않았습니다.`,
  resourceKind: {
    ProcessHandles: "프로세스 핸들"
  },
  holder: {
    processId: "PID",
    productName: "제품 이름",
    services: "서비스",
    imagePath: "이미지 경로",
    noProductName: "제공되지 않음",
    noServices: "해당 서비스 없음",
    unavailable: "읽을 수 없음",
    processEnded: "프로세스 종료됨"
  },
  evidence: {
    currentValue: "현재 값",
    growth: "관찰 기간 중 증가",
    growthPerSecond: "증가율",
    observationTime: "관찰 시간",
    sampleCount: "샘플 수",
    firstObserved: "처음 감지",
    lastObserved: "마지막 감지"
  },
  ratePerSecond: (value: string) => `${value}/초`,
  actions: {
    title: "처리 작업",
    name: {
      RestartHolder: "보유 프로세스 다시 시작",
      ForceReleaseHandles: "핸들 강제 해제"
    },
    riskLevel: {
      Warning: "경고",
      High: "높음"
    },
    riskText: (level: string, risk: string) => `위험: ${level}. ${risk}`,
    riskLabel: (name: string) => `${name} 위험`
  }
};
