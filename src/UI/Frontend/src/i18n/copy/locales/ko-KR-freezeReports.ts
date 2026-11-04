import type { AppCopy } from "../index.ts";

export const freezeReports: AppCopy["freezeReports"] = {
  systemEvents: {
    tab: "시스템 이벤트",
    intro: "로컬 서비스는 시스템 로그에서 스토리지 재시도 및 재설정, 그래픽 드라이버 재설정, 메모리 부족, 데스크톱 프로세스 충돌, 하드웨어 오류 등의 이벤트를 수집하여 범주와 대상별로 항목으로 묶습니다. 이러한 문제는 이 소프트웨어로 해결할 수 없으므로 여기서는 감지하고 보고만 합니다. 오랫동안 새 이벤트가 없는 항목은 기록으로 이동합니다.",
    loading: "시스템 이벤트 보고서를 읽는 중…",
    readFailed: "시스템 이벤트 보고서를 읽을 수 없습니다.",
    notObservedTitle: "시스템 이벤트를 기록하지 않고 있습니다",
    notObservedDetail: "시스템 이벤트 기록이 실행되고 있지 않습니다. 설정에서 해제되었거나 아직 시작되지 않았을 수 있습니다. 실행되기 전에는 항목이 만들어지지 않습니다.",
    unavailableTitle: "시스템 이벤트 기록에 실패했습니다",
    unavailableDetail: "로컬 서비스가 시스템 로그를 읽을 수 없습니다. 원인은 위에 표시됩니다.",
    emptyTitle: "시스템 이벤트가 없습니다",
    emptyDetail: "시스템 로그에 보고해야 할 이벤트가 나타나지 않았습니다.",
    status: {
      label: "기록 상태",
      observing: "기록 중",
      "not-observed": "기록하지 않음",
      unavailable: "기록 실패"
    },
    activeCount: "활성 항목",
    historyCount: "기록 항목",
    machineTarget: "컴퓨터 전체",
    count: "횟수",
    times: (count: number) => `${count}회`,
    firstObserved: "처음 발생",
    lastObserved: "마지막 발생",
    overview: "개요",
    categoryLabel: "범주",
    target: "대상",
    state: "상태",
    activeEntry: "활성",
    historyEntry: "기록",
    category: {
      storage_retry_reset: "스토리지 재시도 및 재설정",
      gpu_reset: "그래픽 드라이버 재설정",
      memory_exhausted: "메모리 부족",
      desktop_crash: "데스크톱 프로세스 충돌",
      hardware_error: "하드웨어 오류",
      unexpected_shutdown: "예기치 않은 종료 및 블루스크린"
    },
    targetKindLabel: "대상 종류",
    targetKind: {
      machine: "컴퓨터 전체",
      physical_disk: "물리 디스크",
      storage_port: "스토리지 컨트롤러 포트",
      volume: "볼륨",
      gpu_device: "그래픽 장치",
      desktop_process: "데스크톱 프로세스",
      hardware: "하드웨어"
    },
    time: "시간",
    channel: "로그",
    channelName: {
      system: "시스템",
      application: "응용 프로그램"
    },
    provider: "원본",
    eventId: "이벤트 ID",
    recordId: "레코드 ID",
    bugcheckCode: "중지 코드",
    dumpPath: "덤프 파일",
    failure: (name: string) => `시스템 로그를 읽을 수 없습니다(${name}). 기존 항목은 유지되며, 읽기가 복구되면 기록을 다시 시작합니다.`,
    liveKernelFailure: (name: string) => `LiveKernelReports 폴더를 검사할 수 없습니다(${name}). 시스템 로그의 이벤트에는 영향이 없습니다.`,
    unrecorded: (count: number) => `항목 수 한도에 도달하여 ${count}개의 항목이 더 기록되지 않았습니다.`,
    items: "시스템 이벤트 항목",
    detailsButton: "자세히",
    back: "목록으로 돌아가기",
    eventsTitle: (count: number) => `최근 원본 이벤트(${count}개)`,
    xml: "이벤트 XML"
  },
  recorder: {
    tab: "멈춤 기록기",
    intro: "로컬 서비스는 메모리, 커널 풀, 디스크, 인터럽트에 관한 소량의 데이터를 고정 크기 기록 파일에 계속 기록합니다. 컴퓨터가 멈춘 뒤 강제로 전원이 꺼지거나 블루스크린으로 중지되면, 다음 시작 시 기록과 시스템 로그를 대조하여 재시작 후 검토를 만듭니다. 여기에는 멈춤 이전의 기록, 같은 기간의 시스템 이벤트, 판정 결과가 포함됩니다. 이 정보는 여기서 감지하고 보고만 합니다.",
    loading: "멈춤 기록기를 읽는 중…",
    readFailed: "멈춤 기록기를 읽을 수 없습니다.",
    emptyTitle: "재시작 후 검토가 없습니다",
    emptyDetail: "마지막 시작 이후 예기치 않은 종료나 블루스크린이 감지되지 않았습니다.",
    recorder: "기록기",
    running: "기록 중",
    stopped: "기록하지 않음",
    lastRecordAt: "마지막 기록",
    reviewCount: "재시작 후 검토",
    failure: (name: string) => `기록기가 실패했습니다(${name}). 기록을 쓰지 않고 있습니다.`,
    stoppedNote: "기록기가 실행되고 있지 않습니다. 설정에서 해제되었을 수 있습니다. 이 기간에 발생한 멈춤은 완전히 검토할 수 없습니다.",
    gapsTitle: "현재 기록에서 누락된 데이터",
    gap: {
      "no-sample": "아직 기록이 작성되지 않았습니다",
      "interrupt-cpu-share": "인터럽트가 차지한 프로세서 시간",
      "longest-interrupt": "가장 긴 단일 인터럽트",
      "active-kernel-leak-system-item": "시스템 수준 커널 누수 항목의 활성 여부",
      "process-private-commit": "프로세스 개인 커밋",
      "physical-disk-inventory": "물리 디스크 목록",
      "physical-disk-completed-operations": "물리 디스크 완료 작업 수",
      commit: "커밋 및 커밋 한도",
      "physical-memory": "물리 메모리",
      "kernel-pools": "페이징 풀 및 비페이징 풀"
    },
    reviews: "재시작 후 검토",
    reviewTitle: (time: string) => `예기치 않은 종료 · ${time}`,
    shutdownAt: "종료 또는 블루스크린 시간",
    gapDuration: "간격",
    bugcheckCode: "중지 코드",
    tagsTitle: "판정 결과",
    noTags: "판정 결과가 없습니다.",
    inputsIncomplete: "검토 중 일부 기록이나 시스템 로그를 완전히 읽지 못하여 결과가 불완전할 수 있습니다.",
    tag: {
      bugcheck: {
        name: "블루스크린",
        description: "시스템 로그에 블루스크린이 기록되어 있습니다. 중지 코드는 개요에 표시됩니다."
      },
      "commit-exhausted": {
        name: "커밋 부족",
        description: "기록된 커밋이 메모리 보호의 즉시 한계선에 도달했거나, 같은 기간에 시스템이 메모리 부족을 보고했습니다."
      },
      "physical-exhausted": {
        name: "물리 메모리 부족",
        description: "기록된 사용 가능한 물리 메모리가 설정에서 지정한 비율보다 낮아졌습니다."
      },
      "pool-high": {
        name: "커널 풀 과다",
        description: "비페이징 풀이 물리 메모리 대비 지정한 비율을 넘었거나, 시스템 수준 커널 누수 항목이 활성 상태였습니다."
      },
      "storage-unresponsive": {
        name: "스토리지 응답 없음",
        description: "같은 기간에 스토리지 재시도나 재설정이 발생했거나, 디스크가 응답 없음 조건을 연속으로 충족했습니다."
      },
      "gpu-reset": {
        name: "그래픽 드라이버 재설정",
        description: "같은 기간에 그래픽 드라이버가 재설정되었습니다."
      },
      "hardware-error": {
        name: "하드웨어 오류",
        description: "같은 기간에 시스템이 하드웨어 오류를 보고했습니다."
      },
      "recording-stopped-normal": {
        name: "정상 상태에서 기록 중지",
        description: "위의 경우에 해당하지 않으며 마지막 기록은 모두 정상이었습니다. 가능한 원인은 스토리지 드라이버나 필터 드라이버의 정지, 갑작스러운 전원 차단, 전원 단추 길게 누르기입니다."
      }
    },
    details: {
      button: "자세히",
      back: "목록으로 돌아가기",
      loading: "검토를 읽는 중…",
      readFailed: "검토를 읽을 수 없습니다.",
      export: "내보내기",
      exporting: "내보내는 중",
      exportFailed: "검토를 내보낼 수 없습니다.",
      overview: "개요",
      reviewWindow: "검토 기간",
      timeRange: (from: string, to: string) => `${from} ~ ${to}`,
      samplesSeen: "기간 내 기록 수",
      inputsComplete: "데이터 완전성",
      yes: "예",
      no: "아니요",
      dumpPath: "덤프 파일",
      thresholdsTitle: "검토에 사용한 임계값",
      commitImmediatePercent: "커밋 즉시 한계선",
      physicalAvailablePercent: "사용 가능한 물리 메모리 하한",
      nonpagedPoolPercent: "비페이징 풀 상한(물리 메모리 대비)",
      storageConsecutive: "디스크 응답 없음 연속 기록 수",
      sampleInterval: "기록 간격",
      reviewWindowLength: "검토 기간 길이",
      curvesTitle: "기록 추이",
      curve: {
        commit: "커밋(한도 대비)",
        physicalAvailable: "사용 가능한 물리 메모리(전체 대비)",
        nonpagedPool: "비페이징 풀",
        pagedPool: "페이징 풀",
        diskQueue: "디스크 대기열 길이(최대)",
        diskLatency: "디스크 지연 시간(최대)",
        interrupt: "인터럽트가 차지한 프로세서 시간"
      },
      curveRange: (min: string, max: string) => `${min} ~ ${max}`,
      noCurveData: "데이터 없음",
      noRecords: "검토 기간에 기록이 없습니다.",
      lastRecordTitle: "마지막 기록",
      time: "시간",
      commit: "커밋",
      physicalAvailable: "사용 가능한 물리 메모리",
      ofLimit: (formattedValue: string, formattedLimit: string) => `${formattedValue} / ${formattedLimit}`,
      longestInterrupt: "가장 긴 단일 인터럽트",
      kernelLeakActive: "시스템 수준 커널 누수 항목 활성",
      processesTitle: "개인 커밋이 가장 많은 프로세스",
      processName: (name: string, pid: string) => `${name}(PID ${pid})`,
      diskName: (id: string) => `물리 디스크 ${id}`,
      queueDepth: "대기열 길이",
      latency: "지연 시간",
      completedOperations: "완료 작업 수",
      eventsTitle: (count: number) => `기간 내 시스템 이벤트(${count}개)`,
      noEvents: "검토 기간에 수신된 시스템 이벤트가 없습니다.",
      actualShutdownAt: "실제 종료 시간"
    }
  },
  settings: {
    open: "설정",
    back: "보고서로 돌아가기",
    systemEventsTitle: "시스템 이벤트 설정",
    recorderTitle: "멈춤 기록기 설정",
    readFailed: "설정을 읽을 수 없습니다.",
    save: "저장",
    saving: "저장 중",
    discard: "변경 취소",
    saved: "설정을 저장했습니다.",
    saveFailed: "설정을 저장할 수 없습니다.",
    invalid: "설정이 검증을 통과하지 못하여 저장되지 않았습니다. 값을 확인하십시오.",
    conflict: "설정이 저장되지 않았습니다. 규칙 표, 기록 수, 기록 파일 위치는 실행 중에 변경할 수 없습니다. 먼저 해당 기능을 해제하고 저장한 다음 이 항목을 변경하십시오. 기록 파일이 이미 있으면 기록 수를 변경할 때 새 위치도 지정해야 합니다.",
    resourceBound: "설정이 저장되지 않았습니다. 필요한 리소스가 한도를 넘습니다. 용량이나 기록 수를 줄이십시오.",
    draftInvalid: "현재 변경 내용이 불완전하여(예: 규칙에 원본 이름이 없음) 저장할 수 없습니다.",
    rulesLocked: "시스템 이벤트 기록이 켜져 있는 동안에는 규칙 표를 변경할 수 없습니다. 먼저 해제하고 저장하십시오.",
    recorderLocked: "기록기가 켜져 있는 동안에는 이 항목을 변경할 수 없습니다. 먼저 해제하고 저장하십시오.",
    units: {
      minutes: "분",
      seconds: "초",
      milliseconds: "밀리초",
      percent: "퍼센트",
      count: "개"
    },
    systemEvents: {
      enabled: {
        title: "시스템 이벤트 기록",
        description: "시스템 로그에서 이벤트를 받아 항목으로 묶습니다. 해제하면 새 이벤트를 받지 않으며 기존 항목은 유지됩니다."
      },
      historyAfter: {
        title: "기록으로 이동하기까지의 시간",
        description: "마지막 이벤트 이후 이 시간 동안 새 이벤트가 없으면 항목이 기록으로 이동합니다."
      },
      eventTailCapacity: {
        title: "항목별 보존 원본 이벤트 수",
        description: "각 항목이 세부 정보 페이지를 위해 보존하는 최근 원본 이벤트의 수입니다."
      },
      entryCapacity: {
        title: "항목 수 한도",
        description: "보존하는 항목의 최대 수입니다. 한도에 도달하면 새 항목은 수만 세고 기록하지 않습니다."
      },
      liveKernelRoot: {
        title: "LiveKernelReports 위치",
        description: "시스템이 라이브 커널 덤프 보고서를 저장하는 폴더로, 그래픽 드라이버 시간 초과 등의 보고서를 찾는 데 사용합니다."
      },
      liveKernelDirectories: {
        title: "검사할 하위 폴더",
        description: "위 위치 아래에서 검사할 하위 폴더로, 한 줄에 하나씩 지정합니다."
      },
      liveKernelEntryCapacity: {
        title: "덤프 보고서 한도",
        description: "검사할 때마다 기록하는 덤프 보고서의 최대 수입니다."
      },
      rules: {
        title: "이벤트 규칙",
        description: "시스템 로그의 어떤 이벤트가 어느 범주에 속하는지, 각 이벤트가 어떤 종류의 대상을 가리키는지 정합니다."
      }
    },
    recorder: {
      enabled: {
        title: "멈춤 데이터 기록",
        description: "기록을 계속 쓰고, 예기치 않은 종료 후 다음 시작 시 검토를 만듭니다. 해제하면 기록이 중지되며 기록 파일은 유지됩니다."
      },
      sampleInterval: {
        title: "기록 간격",
        description: "두 기록 사이의 시간입니다. 짧을수록 마지막 기록이 멈춤 시점에 가까워집니다."
      },
      reviewWindow: {
        title: "검토 기간",
        description: "검토에서 마지막 기록 이전 얼마 동안의 기록과 시스템 이벤트를 표시할지 지정합니다."
      },
      commitImmediate: {
        title: "커밋 즉시 한계선",
        description: "커밋 부족을 판정하는 데 쓰는 한계선입니다. 메모리 보호의 즉시 한계선을 따르며 메모리 보호 설정에서 변경합니다."
      },
      commitImmediateNone: "설정되지 않음",
      physicalAvailable: {
        title: "사용 가능한 물리 메모리 하한",
        description: "사용 가능한 물리 메모리가 전체 대비 이 비율보다 낮아지면 물리 메모리 부족으로 판정합니다."
      },
      nonpagedPool: {
        title: "비페이징 풀 상한",
        description: "비페이징 풀이 물리 메모리 대비 이 비율을 넘으면 커널 풀 과다로 판정합니다."
      },
      storageConsecutive: {
        title: "디스크 응답 없음 연속 기록 수",
        description: "디스크가 이 수만큼의 기록에서 연속으로 조건을 충족하면 스토리지 응답 없음으로 판정합니다."
      },
      slots: {
        title: "기록 수",
        description: "기록 파일에 담을 수 있는 기록의 수로, 검토가 거슬러 올라갈 수 있는 범위를 정합니다. 기록 파일이 이미 있으면 변경할 때 새 위치도 지정해야 합니다."
      },
      ringPath: {
        title: "기록 파일 위치",
        description: "기록 파일의 전체 경로입니다. 비워 두면 기본 위치를 사용합니다."
      },
      ringPathDefault: "기본 위치"
    },
    ruleTable: {
      channel: "로그",
      provider: "원본 이름",
      ids: "이벤트 ID",
      idsHint: "쉼표로 구분",
      category: "범주",
      targetKind: "대상 종류",
      remove: "제거",
      add: "규칙 추가"
    }
  }
};
