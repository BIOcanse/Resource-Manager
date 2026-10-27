import type { AppCopy } from "../index.ts";

export const smartFailure: AppCopy["smartFailure"] = {
  title: {
    "cycle-failed": "이 스케줄링 구간이 실패했습니다",
    "native-plan-failed": "네이티브 스케줄링 계획이 실패했습니다"
  },
  hint: {
    "cycle-failed": "이 스케줄링 구간을 실행하는 중에 오류가 발생하여 완료되지 않았습니다. 이후 구간이 정상적으로 완료되면 이 알림은 사라집니다. 오류의 자세한 내용은 로컬 서비스 로그에 기록됩니다.",
    "native-plan-failed": "네이티브 스케줄링 구성 요소가 이 구간의 계획을 만들지 못하여 이 구간에서는 조정을 실행하지 않았습니다. 계획 상태에 원인이 표시됩니다. 이후 구간이 정상적으로 완료되면 이 알림은 사라집니다."
  },
  nativePlanStatus: (status: string) => `계획 상태: ${status}`,
  nativePlanStatuses: {
    "invalid-argument": "인수가 잘못되었습니다",
    "abi-mismatch": "네이티브 구성 요소의 인터페이스 버전이 일치하지 않습니다",
    unavailable: "네이티브 구성 요소를 사용할 수 없습니다",
    "no-data": "사용할 수 있는 데이터가 없습니다",
    "buffer-too-small": "버퍼 용량이 부족합니다",
    "stale-generation": "입력 데이터가 오래되었습니다",
    "out-of-memory": "메모리가 부족합니다",
    "capacity-exceeded": "용량 한도를 초과했습니다",
    "conflicting-facts": "입력 데이터가 서로 모순됩니다",
    "feedback-mismatch": "실행 결과 보고가 계획과 일치하지 않습니다",
    "recreate-required": "네이티브 작업 영역을 다시 만들어야 합니다"
  },
  exclusionTotal: (count: number) => `이 구간에서 제외된 데이터 ${count}개`,
  exclusionsHint: "제외된 데이터는 누락되었거나 유효하지 않거나 서로 일치하지 않아 이 구간의 스케줄링 판단에 사용되지 않은 데이터입니다. 요약은 데이터 항목과 원인별 개수를 보여 주고, 세부 정보는 해당 대상을 보여 줍니다.",
  summaryLabel: "제외 요약",
  viewExclusions: "제외 세부 정보",
  detailTitle: "이 구간에서 제외된 데이터",
  detailSummary: (listed: number, total: number) => `총 ${total}개가 제외되었으며 아래에 ${listed}개를 표시합니다.`,
  field: {
    processId: "프로세스 ID",
    processStartKey: "프로세스 시작 식별자",
    targetId: "스케줄링 대상 식별자",
    target: "스케줄링 대상"
  },
  noTarget: "특정 대상에 해당하지 않음",
  facts: {
    inventory: "프로세스 목록",
    identity: "프로세스 식별 정보",
    "base-score": "기본 점수",
    attribution: "소프트웨어 귀속",
    "runtime-state": "스케줄링 대상의 실행 상태",
    "cpu-core-residency": "CPU 코어 상주",
    "cpu-score": "CPU 점수",
    "gpu-score": "GPU 점수",
    "compute-score": "연산 점수",
    "memory-cleanup": "메모리 정리",
    "decision-target": "판단 대상",
    "native-plan": "네이티브 스케줄링 계획",
    CpuUsage: "프로세스 CPU 사용률",
    MemoryUsage: "프로세스 메모리 사용량",
    GpuUsage: "프로세스 GPU 사용률",
    GpuDedicatedMemory: "프로세스 전용 VRAM 사용량",
    RuntimeState: "프로세스 실행 상태"
  },
  reasons: {
    "dataset-not-current": "데이터가 현재 샘플이 아닙니다",
    "unidentified-live-process": "식별할 수 없는 실행 중인 프로세스가 있습니다",
    "inventory-overflow": "프로세스 목록이 용량을 초과했습니다",
    "emitted-count-mismatch": "출력 개수가 프로세스 목록과 일치하지 않습니다",
    "inventory-count-mismatch": "목록 개수가 열거된 개수와 일치하지 않습니다",
    "capture-count-mismatch": "수집 개수가 일치하지 않습니다",
    "invalid-or-duplicate-inventory-identity": "프로세스 목록에 잘못되었거나 중복된 프로세스 식별 정보가 있습니다",
    "invalid-or-duplicate-process": "프로세스 식별 정보가 잘못되었거나 중복되었습니다",
    "row-missing-or-invalid": "데이터가 누락되었거나 유효하지 않습니다",
    "gpu-row-missing-or-invalid": "GPU 데이터가 누락되었거나 유효하지 않습니다",
    "compute-runtime-state-missing": "실행 상태가 없습니다",
    "cpu-residency-not-covered": "CPU 상주 샘플링이 이 프로세스를 포함하지 않습니다",
    "cpu-residency-invalid": "CPU 상주 데이터가 유효하지 않습니다",
    "target-process-count-not-one": "스케줄링 대상에 해당하는 프로세스가 하나가 아닙니다",
    "target-process-identity-missing": "스케줄링 대상에 프로세스 식별 정보가 없습니다",
    "target-process-identity-mismatch": "스케줄링 대상의 프로세스 식별 정보가 일치하지 않습니다",
    "duplicate-target-process-identity": "여러 스케줄링 대상이 같은 프로세스를 가리킵니다",
    "target-runtime-state-invalid": "스케줄링 대상의 실행 상태가 유효하지 않습니다",
    "cleanup-baseline-score-missing": "정리 기준 점수가 없습니다",
    "cleanup-current-process-facts-missing": "프로세스의 현재 데이터가 없습니다",
    "cleanup-software-maximum-base-score-missing": "소프트웨어의 최고 기본 점수가 없습니다",
    "ownership-software-attribution-mismatch": "변경 소유 기록이 소프트웨어 귀속과 일치하지 않습니다",
    "canonical-process-membership-mismatch": "프로세스가 점수의 소프트웨어 구성원과 일치하지 않습니다",
    "canonical-software-score-missing": "소프트웨어 점수가 없습니다",
    "live-identity-without-decision-facts": "실행 중인 프로세스에 판단용 데이터가 없습니다",
    "canonical-process-not-projected": "프로세스 점수가 스케줄링 입력에 반영되지 않았습니다",
    "canonical-software-membership-incomplete": "소프트웨어 점수의 구성원이 불완전합니다"
  }
};
