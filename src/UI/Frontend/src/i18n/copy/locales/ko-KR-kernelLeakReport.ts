import type { AppCopy } from "../index.ts";

export const kernelLeakReport: AppCopy["kernelLeakReport"] = {
  tab: "커널 누수 보고서",
  intro: "커널 리소스(현재는 프로세스 핸들)가 계속 증가하여 무한히 증가하는 것으로 의심되는 프로세스와 판정 근거를 나열합니다. 판정 결과는 의심을 나타낼 뿐이며, 처리 여부는 판정 근거를 바탕으로 결정하십시오. 이 페이지가 표시되는 동안에만 관찰하며, 페이지를 다시 열면 관찰을 처음부터 다시 시작합니다.",
  loading: "커널 누수 보고서를 읽는 중…",
  readFailed: "커널 누수 보고서를 읽지 못했습니다",
  capturedAt: "수집 시각",
  suspectedItems: "의심 항목",
  emptyTitle: "커널 누수 의심 항목이 없습니다",
  emptyDetail: "관찰 시간이 아직 판정하기에 부족한 경우에도 이 결과가 표시됩니다. 의심 항목이 발견되면 여기에 표시됩니다.",
  notObservedTitle: "아직 첫 샘플을 수집하지 않았습니다",
  unavailableTitle: "커널 누수 보고서를 사용할 수 없습니다",
  failure: {
    NotObserved: "관찰을 시작했습니다. 첫 샘플을 수집한 후 결과를 표시합니다.",
    SourceUnavailable: "프로세스의 리소스 데이터를 읽을 수 없습니다.",
    CapacityExceeded: "관찰하는 프로세스 수가 용량 한도를 초과하여 이번 관찰을 중지했습니다.",
    InvalidObservation: "샘플 데이터가 유효하지 않아 이번 관찰을 중지했습니다."
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
    currentValue: "현재 수",
    growth: "관찰 기간 중 증가",
    growthPerSecond: "증가율",
    observationTime: "관찰 시간",
    sampleCount: "샘플 수"
  },
  ratePerSecond: (value: string) => `${value}/초`,
  actionsPending: "이 항목에 대한 처리 작업은 아직 로컬 서비스에서 제공하지 않습니다."
};
