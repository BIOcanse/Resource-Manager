import type { AppCopy } from "../index.ts";

export const deviceActions: AppCopy["deviceActions"] = {
  restart: {
    label: "장치 다시 시작",
    busy: "다시 시작하는 중",
    hint: "이 보고서가 가리키는 장치를 사용 안 함으로 했다가 다시 사용하도록 합니다. 멈춘 드라이버가 다시 동작할 수 있습니다. 매번 직접 확인해야 하며, 소프트웨어가 자동으로 실행하지 않습니다.",
    confirmTitle: "이 장치를 다시 시작할까요?",
    confirmMessage: (target: string) => `“${target}”에 해당하는 장치를 사용 안 함으로 했다가 다시 사용하도록 합니다.`,
    confirmDetails: [
      "확인한 뒤 한 번만 실행하며 자동으로 반복하지 않습니다.",
      "관리자 권한이 필요합니다. 결과는 세부 정보의 장치 작업 기록에 남습니다."
    ],
    risks: {
      DeviceRestartNetworkInterrupted: "네트워크 어댑터라면 네트워크 연결이 잠시 끊깁니다.",
      DeviceRestartDisplayStateLost: "그래픽 어댑터라면 화면이 잠시 꺼지고 창 위치와 디스플레이 설정이 사라질 수 있습니다.",
      DeviceRestartMayRequireReboot: "일부 장치는 컴퓨터를 다시 시작해야 복구됩니다."
    },
    unknownRisk: (code: string) => `기타 위험 (${code}).`,
    confirm: "장치 다시 시작",
    doneTitle: "장치를 다시 시작했습니다",
    refusedTitle: "장치를 다시 시작하지 않았습니다",
    failed: "장치를 다시 시작하지 못했습니다.",
    reason: (text: string) => `결과: ${text}`,
    deviceResult: (text: string) => `장치 결과: ${text}`,
    win32Detail: (code: string) => `시스템 오류 코드: ${code}`,
    recorded: (sequence: string) => `장치 작업 기록에 남겼습니다 (번호 ${sequence}).`,
    notRecorded: "이 요청은 기록되지 않았습니다."
  },
  reasons: {
    completed: "완료",
    "confirmation-required": "확인이 필요합니다",
    "report-not-found": "보고서 항목을 찾을 수 없습니다",
    "unsupported-report": "이 종류의 보고서는 장치 다시 시작을 지원하지 않습니다",
    "device-evidence-unavailable": "보고서에 장치를 특정할 정보가 없습니다",
    "invalid-settings": "설정이 올바르지 않습니다",
    "record-capacity-reached": "장치 작업 기록이 가득 차서 실행하지 않았습니다",
    "persistence-failed": "기록을 쓰지 못해 실행하지 않았습니다",
    "device-resolution-failed": "다시 시작할 장치를 특정하지 못했습니다",
    "device-restart-failed": "장치를 다시 시작하지 못했습니다",
    "notification-failed": "장치는 처리했지만 알림을 보내지 못했습니다"
  },
  results: {
    success: "성공",
    "confirmation-required": "확인이 필요합니다",
    "invalid-instance-id": "장치 인스턴스 ID가 올바르지 않습니다",
    "administrator-required": "관리자 권한이 필요합니다",
    "device-not-found": "장치를 찾을 수 없습니다",
    "device-unavailable": "지금은 장치를 사용할 수 없습니다",
    "disable-failed": "장치를 사용 안 함으로 하지 못했습니다",
    "enable-failed": "장치를 다시 사용하도록 하지 못했습니다",
    "reboot-required": "컴퓨터를 다시 시작해야 합니다",
    "verification-failed": "다시 시작한 뒤 확인을 통과하지 못했습니다",
    "driver-not-found": "드라이버를 찾을 수 없습니다",
    "ambiguous-device": "여러 장치가 해당되어 실행하지 않았습니다",
    "invalid-module": "드라이버 모듈이 올바르지 않습니다",
    "enumeration-failed": "장치 목록을 가져오지 못했습니다",
    "buffer-too-small": "시스템 버퍼가 너무 작습니다",
    "unsupported-platform": "이 시스템에서는 지원하지 않습니다",
    interrupted: "실행 중 중단되어 결과를 알 수 없습니다"
  },
  unknownCode: (code: string) => `코드 ${code}`,
  records: {
    tab: "장치 작업 기록",
    intro: "보고서에서 “장치 다시 시작”을 확인할 때마다 그 결과를 여기에 기록합니다. 기록은 계속 보관되며 자동으로 삭제되지 않습니다. 진행 중인 기록은 작업이 끝나면 기록 보관으로 옮겨집니다.",
    loading: "장치 작업 기록을 불러오는 중…",
    readFailed: "장치 작업 기록을 불러오지 못했습니다",
    empty: "아직 장치 작업 기록이 없습니다.",
    loadMore: "이전 기록 불러오기",
    loadingMore: "불러오는 중",
    refresh: "새로 고침",
    shown: (count: string) => `표시된 기록: ${count}개.`,
    states: { active: "진행 중", history: "기록 보관" },
    device: "장치 인스턴스",
    report: "보고서 항목",
    time: "시간",
    result: "결과",
    detail: "시스템 오류 코드",
    sequence: "번호"
  }
};

export const crashDumpKeys: AppCopy["crashDumpKeys"] = {
  title: "크래시 덤프 키",
  description: "켜면 오른쪽 Ctrl 키를 누른 채 Scroll Lock 키를 두 번 눌렀을 때 시스템이 파란 화면으로 멈추고 메모리 덤프를 기록합니다. 컴퓨터가 멈춰 정상적으로 쓸 수 없을 때 나중에 분석할 수 있도록 상태를 남깁니다. 기본값은 꺼짐입니다.",
  details: [
    "이 키를 누를 때만 동작하며, 평소에는 아무 일도 하지 않습니다.",
    "동작하면 컴퓨터가 바로 다시 시작되어 저장하지 않은 작업이 사라집니다.",
    "변경은 컴퓨터를 다시 시작한 뒤 적용되며, 쓰려면 관리자 권한이 필요합니다. 끄면 켜기 전의 시스템 설정으로 되돌립니다."
  ],
  notProvided: "백엔드가 이 설정을 제공하지 않았습니다.",
  statusTitle: "시스템 안의 상태",
  states: {
    takesEffectAfterRestart: "시스템에 기록했습니다. 컴퓨터를 다시 시작하면 적용됩니다.",
    off: "켜져 있지 않습니다.",
    restorePending: "켜기 전의 시스템 설정으로 되돌리는 중입니다. 컴퓨터를 다시 시작하면 끝납니다.",
    configurationMismatch: "시스템의 키 설정이 여기서 고른 값과 다릅니다.",
    failed: "시스템 설정을 쓰지 못했습니다."
  },
  combinations: {
    rightCtrlScrollLockTwice: "키: 오른쪽 Ctrl + Scroll Lock 두 번",
    custom: "키: 다른 프로그램이나 직접 설정한 사용자 지정 키"
  },
  kernelDumpMissing: "시스템이 커널 메모리 덤프를 기록하도록 설정되어 있지 않아, 동작해도 덤프가 기록되지 않을 수 있습니다.",
  reason: (text: string) => `이유: ${text}`,
  win32Detail: (code: string) => `시스템 오류 코드: ${code}`,
  reasons: {
    "invalid-crash-dump-keys": "키 설정이 올바르지 않습니다",
    "crash-dump-keys-read-failed": "시스템 설정을 읽지 못했습니다",
    "crash-dump-keys-write-failed": "시스템 설정을 쓰지 못했습니다",
    "crash-dump-keys-verify-failed": "쓴 뒤 확인을 통과하지 못했습니다",
    "crash-dump-keys-restore-pending": "켜기 전의 시스템 설정으로 아직 다 되돌리지 못했습니다",
    "invalid-crash-dump-key-snapshot": "저장한 원래 설정이 올바르지 않습니다",
    "crash-dump-keys-access-denied": "관리자 권한이 필요합니다",
    "crash-dump-key-value-too-large": "시스템의 원래 값이 너무 커서 저장할 수 없습니다",
    "crash-dump-keys-configuration-mismatch": "다른 프로그램이 시스템 설정을 바꿨습니다",
    "crash-dump-keys-bridge-failed": "로컬 서비스가 이 설정을 처리하지 못했습니다"
  }
};

export const cpuMonopoly: AppCopy["cpuMonopoly"] = {
  intro: "우선순위가 높음 또는 실시간인 프로세스가 CPU를 계속 가득 쓰면 시스템이 멈추고 응답하지 않을 수 있습니다. 켜면 그런 프로세스를 보통 우선순위로 되돌립니다. 우선순위만 바꾸며, 프로세스를 끝내거나 일시 중지하지 않고 우선순위를 다시 바꾸지도 않습니다. 호스트 메모리 보호의 프로세스 제외 목록도 적용됩니다. 기본값은 꺼짐입니다.",
  notProvided: "백엔드가 CPU 독점 보호 설정을 제공하지 않았습니다.",
  unsavedNote: "저장하지 않은 변경이 있습니다. 저장하면 적용됩니다.",
  rejected: (reason: string) => `설정이 받아들여지지 않았습니다: ${reason}`,
  enabledTitle: "CPU 독점 보호 켜기",
  enabledDescription: "켜면 각 프로세스의 CPU 사용률과 우선순위를 백그라운드에서 확인합니다. 끄면 확인하지 않습니다.",
  thresholdTitle: "사용률 임계값",
  thresholdDescription: "우선순위가 높음 또는 실시간인 프로세스의 CPU 사용률(전체 프로세서 대비 비율)이 이 값에 도달하면 시간을 재기 시작합니다.",
  thresholdLabel: "퍼센트",
  durationTitle: "지속 시간",
  durationDescription: "사용률이 임계값에 머문 상태가 이만큼 이어지면 보통 우선순위로 되돌립니다.",
  durationLabel: "초",
  intervalTitle: "확인 간격",
  intervalDescription: "각 프로세스의 CPU 사용률을 읽는 간격입니다.",
  intervalLabel: "초",
  whitelistNote: "제외 목록은 “호스트 메모리 보호” 섹션에서 편집합니다.",
  statusTitle: "현재 상태",
  statusLoading: "CPU 독점 보호 상태를 불러오는 중…",
  statusUnavailable: "CPU 독점 보호 상태를 불러오지 못했습니다",
  statusUnavailableDetail: "설정은 계속 편집하고 저장할 수 있습니다. 상태를 받으면 여기에 표시합니다.",
  stateActive: "확인 중",
  stateInactive: (reason: string) => `확인하지 않음: ${reason}`,
  inactiveReasons: {
    disabled: "꺼짐",
    "not-running": "보호가 아직 시작되지 않았습니다",
    "process-data-unavailable": "프로세스 데이터를 읽을 수 없습니다",
    "process-exit-proof-unavailable": "프로세스가 끝났는지 확인할 수 없습니다",
    "process-capacity": "추적할 수 있는 수보다 프로세스가 많습니다",
    stopped: "멈춤"
  },
  currentAction: "적용 중인 처리",
  exclusionsTitle: "제외된 프로세스",
  exclusionsEmpty: "지금 제외된 프로세스가 없습니다.",
  exclusionReasons: {
    "resource-manager": "이 소프트웨어의 프로세스",
    "resource-manager-process": "이 소프트웨어의 프로세스",
    "protected-process": "제외 목록에 있음",
    "critical-process": "시스템 핵심 프로세스",
    "unsupported-platform": "이 시스템에서는 지원하지 않습니다",
    "identity-changed": "프로세스가 바뀌었습니다",
    "cannot-open": "이 프로세스에 접근할 수 없습니다",
    "criticality-unavailable": "시스템 핵심 프로세스인지 확인할 수 없습니다"
  },
  process: (name: string, processId: number) => `${name} (PID ${processId})`,
  percent: (value: string) => `${value}%`,
  seconds: (value: string) => `${value}초`,
  priorities: { High: "높음", RealTime: "실시간" },
  records: {
    tab: "CPU 독점 보호 기록",
    intro: "CPU 독점 보호는 처리한 프로세스마다 기록을 하나 남깁니다. 보통 우선순위로 되돌린 뒤 프로세스가 끝나거나 직접 우선순위를 정하면 기록은 기록 보관으로 옮겨집니다. 실패한 처리는 처음부터 기록 보관에 있습니다. 기록은 계속 보관되며 자동으로 삭제되지 않습니다.",
    loading: "CPU 독점 보호 기록을 불러오는 중…",
    unavailable: "CPU 독점 보호 기록을 불러오지 못했습니다",
    empty: "아직 처리 기록이 없습니다.",
    count: (count: string, capacity: string) => `기록 ${count}개, 최대 ${capacity}개까지 보관합니다.`,
    unrecorded: (count: number) => `기록이 가득 차서 이후 처리 ${count}건이 기록되지 않았습니다. 기록 보관을 지우면 다시 기록합니다.`,
    outcomes: { normalized: "보통 우선순위로 되돌림", failed: "처리 실패" },
    states: { active: "적용 중", history: "기록 보관" },
    time: "시간",
    cpu: "CPU 사용률",
    previousPriority: "이전 우선순위",
    threshold: "당시 임계값",
    duration: "당시 지속 시간",
    reason: "이유",
    detail: "세부 정보",
    endedAt: "끝난 시간",
    endReason: "끝난 이유"
  },
  actionReasons: {
    "process-exited": "프로세스가 끝났습니다",
    "user-priority-set": "우선순위가 따로 설정되었습니다",
    "write-failed": "우선순위를 쓰지 못했습니다",
    "identity-changed": "프로세스가 바뀌었습니다",
    "priority-changed": "다른 프로그램이 우선순위를 바꿨습니다",
    "priority-unavailable": "프로세스의 우선순위를 읽을 수 없습니다",
    "priority-write-failed": "우선순위를 쓰지 못했습니다",
    "priority-readback-unavailable": "쓴 뒤 우선순위를 다시 읽지 못했습니다",
    "priority-readback-mismatch": "쓴 뒤 다시 읽은 우선순위가 다릅니다"
  },
  notice: {
    title: "CPU 독점 보호",
    normalized: (target: string) => `${target}을(를) 보통 우선순위로 되돌렸습니다.`,
    failed: (target: string, reason: string) => `${target}을(를) 조정하지 못했습니다: ${reason}`,
    openRecords: "기록 보기"
  }
};
