import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("ko-KR", {
  "updates": {
    "modeTitle": "업데이트 방식",
    "modeDescription": "끄기에서는 자동 확인을 하지 않습니다. 수동 확인과 수동 버전 선택은 모든 방식에서 사용할 수 있습니다.",
    "modeOptions": [{ id: "off", label: "끄기", description: "자동 확인, 다운로드, 설치를 하지 않습니다." }, { id: "notify", label: "확인 후 알림만", description: "자동으로 확인하고 새 버전을 알리며 다운로드하지 않습니다." }, { id: "automatic", label: "자동 다운로드 및 적용", description: "자동으로 확인, 다운로드, 검증하고 창을 닫은 뒤 적용합니다. 지금 적용도 선택할 수 있습니다." }],
    "periodicTitle": "주기적 확인",
    "periodicDescription": "시작 시 확인한 뒤 이 간격으로 다시 확인합니다.",
    "internetNote": "자동 확인은 인터넷에 연결될 때까지 기다리며, 간격은 연결된 시간만 셉니다.",
    "offReason": "업데이트 방식이 끄기이므로 자동 확인을 하지 않고 이 두 항목은 적용되지 않습니다. 값은 유지됩니다.",
    "intervalTitle": "확인 간격(시간)",
    "intervalLabel": (minimum: number, maximum: number) => `간격(시간, ${minimum}–${maximum})`,
    "inAppNoticeTitle": "화면 내 새 버전 알림",
    "inAppNoticeDescription": "화면이 실행 중일 때 새 버전을 한 번 알립니다. 같은 버전은 다시 알리지 않습니다.",
    "accessRequestNoticeTitle": "새 액세스 요청 시스템 알림",
    "accessRequestNoticeDescription": "화면이 보이지 않을 때 승인을 기다리는 새 신뢰 액세스 요청이 도착하면 Windows 알림을 표시합니다.",
    "systemNoticeTitle": "Windows 시스템 알림",
    "systemNoticeDescription": "앱이 보이지 않을 때 시스템 알림을 한 번 보냅니다. 이 스위치는 설정만 저장하며 실행은 데스크톱 셸(트레이)이 합니다.",
    "manualCheckNote": "수동 확인은 인터넷을 기다리지 않고 바로 실행되며 모든 방식에서 사용할 수 있습니다.",
    "waitingForInternet": "인터넷 연결을 기다리는 중입니다. 연결되면 바로 확인합니다.",
    "availableVersion": "발견된 새 버전: ",
    "nextCheck": "다음 확인: ",
    "nextCheckAfterInternet": "연결 후 계산 시작",
    "nextCheckOff": "사용 안 함",
    "connectedElapsed": "마지막 확인 이후 연결 시간: ",
    "noticeTitle": "새 버전이 있습니다",
    "noticeMessage": (version: string) => `Resource Manager ${version}(으)로 업데이트할 수 있습니다.`,
    "noticeReadyMessage": (version: string) => `Resource Manager ${version}의 다운로드와 검증이 끝나 지금 적용할 수 있습니다.`,
    "noticeOpenUpdates": "업데이트 및 버전 열기",
    "installedVersionTitle": "설치된 버전",
    "unknownVersion": "알 수 없음",
    "checking": "확인 중…",
    "checkForUpdates": "업데이트 확인",
    "historyIncomplete": "릴리스 기록을 아직 모두 읽지 못했습니다.",
    "historyStale": "이전에 읽은 릴리스 목록을 표시합니다. 다시 연결한 후 재시도하세요.",
    "versionsTitle": "제품 버전",
    "versionsDescription": "최신순으로 표시합니다. 각 계열은 기본적으로 최신 릴리스를 표시합니다.",
    "lastChecked": "마지막 확인: ",
    "selectedTarget": "선택한 대상 버전: ",
    "prepare": "선택한 업데이트 준비",
    "confirmPrepare": (version: string) => `버전 ${version}을 준비할까요? 패키지를 검증하고 모든 데스크톱 화면이 종료될 때까지 기다린 뒤 서비스를 중지합니다. 데이터를 보존하고 변환하여 새 버전을 검증합니다. 실패하면 이전 버전을 복원합니다.`,
    "prepareFailed": "업데이트를 준비할 수 없습니다.",
    "waitingForExit": "업데이트가 준비되었습니다. 적용하려면 트레이에서 모든 Resource Manager 데스크톱 세션을 종료하세요.",
    "stageLabel": "업데이트 상태",
    "unknownStage": "알 수 없는 업데이트 상태",
    "stages": {
      "idle": "대기",
      "checking": "업데이트 확인 중",
      "downloading": "패키지 다운로드 중",
      "verifying": "패키지 검증 중",
      "waitingForExit": "데스크톱 세션 종료 대기 중",
      "completed": "업데이트 완료",
      "error": "업데이트 실패"
    },
    "otherVersions": "기타 버전",
    "installedUnknown": "설치된 버전을 알 수 없어 업그레이드 순서를 확인할 수 없습니다.",
    "stable": "안정 버전",
    "preview": "미리 보기 버전",
    "noLatest": "사용 가능한 최신 릴리스가 없습니다.",
    "noStable": "사용 가능한 안정 릴리스가 없습니다.",
    "latest": "최신 릴리스",
    "latestStable": "최신 안정 릴리스",
    "verified": "검증된 릴리스",
    "unavailableReasons": {
      "installedVersionUnknown": "설치된 버전을 알 수 없어 업그레이드 선택이 비활성화되었습니다.",
      "notNewer": "설치된 버전보다 새로운 릴리스만 선택할 수 있습니다."
    }
  },
  "navigationLabel": "설정 항목",
  "sections": {
    "performance": "성능",
    "memoryProtection": "호스트 메모리 보호",
    "appearance": "모양",
    "systemIntegration": "시스템 통합",
    "updates": "업데이트 및 버전",
    "debug": "디버그",
    "credits": "감사의 글"
  },
  "saveState": {
    "saving": "저장 중",
    "saved": "저장됨",
    "constrained": "저장됨. 현재 사용할 수 없는 실행 옵션은 제한되었습니다",
    "partial": "저장됨. 실행 계획의 게시 또는 전달이 완료되지 않았습니다",
    "error": "저장 실패",
    "dirty": "저장되지 않음",
    "conflict": "다른 곳에서 설정이 변경되었습니다. 저장 전에 다시 불러오세요"
  },
  "loadState": {
    "loading": "저장된 설정 불러오는 중…",
    "errorTitle": "설정을 사용할 수 없음",
    "errorDescription": "기본값을 저장된 설정인 것처럼 표시하거나 사용하지 않습니다. 로컬 서비스를 복구한 후 다시 시도하세요.",
    "staleTitle": "설정이 동기화되지 않음",
    "staleDescription": "이 사본은 오래된 것일 수 있습니다. 다시 불러오기에 성공하기 전에는 변경사항을 제출하지 않습니다."
  },
  "actions": {
    "save": "저장",
    "restoreDefaults": "기본값 복원",
    "retry": "다시 시도",
    "reload": "다시 불러오기",
    "reapply": "실행 계획 다시 게시"
  },
  "performance": {
    "preciseGpuPlacementTitle": "정확한 GPU 선택",
    "preciseGpuPlacementDescription": "지원되는 렌더링 프로세스에서 GPU를 정확히 지정할 수 있습니다. 실행 중 전환은 프로세스 호환성에 따라 달라지며 앱별로 설정할 수 있습니다.",
    "preciseGpuPlacementModeOptions": {
      "basic": {
        "label": "꺼짐",
        "description": "Windows가 지원하는 GPU 기본 설정만 사용합니다."
      },
      "precise": {
        "label": "켜짐",
        "description": "소프트웨어에 사용할 GPU를 정확히 선택할 수 있도록 합니다."
      }
    },
    "automaticMemoryCleanupTitle": "긴급 정리 임계값",
    "automaticMemoryCleanupDescription": "백그라운드 정리는 리소스 부하에 따라 단계별로 수행됩니다. 실제 메모리 또는 커밋 용량의 잔여량이 이 값보다 낮아지면 긴급 정리를 수행합니다. 최적화 모드에 메모리 관리가 포함된 경우에만 적용됩니다.",
    "physicalMemoryAutomaticCleanupLabel": "물리 메모리 여유 %",
    "virtualMemoryAutomaticCleanupLabel": "커밋 여유 %",
    "memoryOptimizationTargetTitle": "메모리 최적화 목표",
    "memoryOptimizationTargetDescription": "사용량이 목표를 넘으면 1단계의 느린 해제를 계속하며 긴급 정리는 실행하지 않습니다.",
    "physicalMemoryOptimizationTargetLabel": "물리 메모리 목표 사용률 %",
    "virtualMemoryOptimizationTargetLabel": "목표 커밋 사용률 %",
    "gpuOverflowTitle": "외장 GPU 초과 임계값",
    "gpuOverflowDescription": "어느 한 임계값에 도달하면 점수가 낮은 백그라운드 프로세스를 외장 GPU에서 내장 GPU로 옮깁니다. 스케줄링이 활성화되어 있고 내장 GPU를 사용할 수 있어야 합니다.",
    "gpuOverflowUsageLabel": "외장 GPU 사용률 %",
    "gpuOverflowDedicatedMemoryLabel": "GPU 전용 메모리 사용률 %",
    "realtimeActionBudgetTitle": "실시간 작업 한도",
    "realtimeActionBudgetDescription": "라운드별 실시간 작업 수를 고정 개수 또는 해당 라운드에서 식별된 모든 프로세스 수의 백분율(올림)로 제한합니다. 모드를 바꿔도 두 값은 유지됩니다.",
    "realtimeActionBudgetModeOptions": [{"id":"fixed","label":"고정 개수"},{"id":"percent","label":"프로세스 비율"}],
    "realtimeActionBudgetCountLabel": "고정 작업 수",
    "realtimeActionBudgetPercentLabel": "모든 프로세스 대비 비율 %",
    "refreshCadenceTitle": "정보 갱신 빈도",
    "refreshCadenceDescription": "모니터링 정보와 상태의 갱신 속도를 설정합니다. 빠르게 갱신할수록 리소스를 조금 더 사용합니다.",
    "presetNumericModeOptions": [
      {
        "id": "aotu",
        "label": "자동",
        "description": "현재 성능 모드와 창 상태에 따라 갱신 빈도를 선택합니다."
      },
      {
        "id": "preset",
        "label": "프리셋 고정",
        "description": "선택한 프리셋의 갱신 빈도를 항상 사용합니다."
      },
      {
        "id": "custom",
        "label": "사용자 지정",
        "description": "직접 입력한 밀리초 값을 항상 사용합니다."
      }
    ],
    "refreshCadencePresetLabel": "프리셋",
    "refreshCadenceCustomLabel": "사용자 지정 (ms)",
    "refreshCadencePresetOptions": [
      {
        "id": "responsive",
        "label": "응답 우선",
        "description": "갱신 속도를 높여 전경의 상호작용을 우선합니다."
      },
      {
        "id": "balanced",
        "label": "균형",
        "description": "일상적인 사용에 적합한 갱신 빈도입니다."
      },
      {
        "id": "lowPower",
        "label": "저전력",
        "description": "백그라운드 저전력 전략입니다."
      },
      {
        "id": "quiet",
        "label": "조용함",
        "description": "필요한 갱신을 유지하면서 빈도를 최소화합니다."
      }
    ],
    "refreshCadenceItems": {
      "monitor": {
        "label": "모니터링 값 및 막대",
        "description": "대시보드의 실시간 값과 리소스 막대를 갱신합니다."
      },
      "resourceTable": {
        "label": "리소스 표",
        "description": "소프트웨어 및 프로세스 리소스 목록을 갱신합니다."
      },
      "localSystem": {
        "label": "로컬 시스템",
        "description": "가동 시간과 같은 저빈도 로컬 상태를 갱신합니다."
      }
    }
  },
  "appearance": {
    "cacheDescription": "\uBAA8\uC591, \uC0C8\uB85C \uACE0\uCE68 \uAC04\uACA9, \uC624\uBC84\uB808\uC774 \uBAA8\uC591\uC740 \uC774 \uAE30\uAE30\uC5D0 \uCE90\uC2DC\uB429\uB2C8\uB2E4. \uCE90\uC2DC\uB97C \uC9C0\uC6B0\uBA74 \uAE30\uBCF8\uAC12\uC73C\uB85C \uB3CC\uC544\uAC11\uB2C8\uB2E4.",
    "resetCache": "\uD654\uBA74 \uD658\uACBD\uC124\uC815 \uCD08\uAE30\uD654",
    "uiStyleTitle": "인터페이스 스타일",
    "uiStyleDescription": "컨트롤의 모서리, 선, 그림자, 글꼴을 바꿉니다. 레이아웃은 그대로입니다. 스타일마다 고유한 색 구성이 있습니다.",
    "themeTitle": "색 구성",
    "themeDescription": "현재 스타일의 색 구성 중에서 선택합니다. 시스템은 Windows의 밝게/어둡게 설정을 따릅니다.",
    "animationStyleTitle": "애니메이션 스타일",
    "animationStyleDescription": "전환이 재생되는 방식과 컨트롤이 제자리에 놓이는 방식을 선택합니다. 초고성능은 애니메이션 없음에 더해 그림자, 하이라이트, 흐림 효과도 끕니다.",
    "autoDisableAnimationsTitle": "자체 스케줄링에 따라 애니메이션 자동 끄기",
    "autoDisableAnimationsDescription": "켜면 화면이 전면으로 돌아온 순간 최근 입력이 없고 자체 스케줄링 결과가 절약이 필요하다고 판단하면 애니메이션 없음으로 바꾸고, 그렇지 않으면 선택한 스타일을 사용합니다. 전면에서 사용하는 동안에는 바꾸지 않습니다. 애니메이션 없음이나 초고성능을 선택하면 효과가 없습니다.",
    "animationStyleOptions": {
      "smooth": { "label": "부드러움", "description": "macOS와 모바일 운영체제처럼 스프링 동작으로 조작 속도를 이어 가고, 언제든 붙잡을 수 있으며, 스크롤 끝에서 튕겨 돌아옵니다" },
      "desktop": { "label": "데스크톱 표준", "description": "일반적인 Windows 데스크톱 소프트웨어처럼 짧게 감속하는 전환이며, 속도를 이어 가지 않고 튕김도 없습니다" },
      "terminal": { "label": "터미널 단계", "description": "완급 없이 정해진 단계 수로 끝까지 이동합니다. 문자 터미널이 다시 그려지는 것과 비슷합니다" },
      "none": { "label": "애니메이션 없음", "description": "모든 전환과 등장이 같은 프레임에서 끝납니다. 모양은 그대로입니다" },
      "ultra": { "label": "초고성능", "description": "애니메이션 없음에 더해 그림자, 하이라이트, 흐림 효과를 꺼서 부담을 최소화합니다" }
    },
    "resourceBarFrameRateTitle": "리소스 막대 애니메이션 프레임 속도",
    "resourceBarFrameRateDescription": "리소스 막대 전환 애니메이션을 초당 몇 프레임 그릴지 정합니다. 화면에 맞춤은 모니터 주사율로 그리고, 사용자 지정은 프레임 수를 제한해 부하를 줄입니다. 애니메이션 없음과 초고성능에서는 적용되지 않습니다.",
    "resourceBarFrameRateModeOptions": [
      {
        "id": "display",
        "label": "화면에 맞춤",
        "description": "화면이 새로 고쳐질 때마다 한 프레임을 그립니다."
      },
      {
        "id": "custom",
        "label": "사용자 지정",
        "description": "초당 설정한 프레임 수(10–240)까지만 그립니다."
      }
    ],
    "resourceBarFrameRateCustomLabel": "프레임 속도 (FPS)",
    "barColorTitle": "막대 색상",
    "barColorDescription": "소프트웨어 유형별 고정 색상 또는 소프트웨어별 다른 색상을 사용합니다.",
    "byteUnitTitle": "용량 단위",
    "byteUnitDescription": "1 GiB = 1024 MiB, 1 GB = 1000 MB입니다. 전환하면 단위 표시뿐 아니라 숫자 자체도 변환됩니다.",
    "readingSourceTitle": "측정값 소스",
    "readingSourceDescription": "모니터링 콘솔과 장치 세부 정보에서 소스가 여러 개인 측정값을 어떻게 표시할지 정합니다. 기본적으로 이 컴퓨터에서 가장 정확한 소스만 표시하며, 사용 가능한 모든 소스를 표시하면 가장 정확한 소스에 ‘우선’이 붙습니다. 스케줄링은 이 설정과 관계없이 항상 가장 정확한 소스를 사용합니다.",
    "readingSourceModeOptions": [
      {
        "id": "preferred",
        "label": "가장 정확한 것만",
        "description": "각 측정값에 대해 이 컴퓨터에서 가장 정확한 소스만 표시합니다."
      },
      {
        "id": "all",
        "label": "사용 가능한 모두",
        "description": "각 측정값의 사용 가능한 소스를 모두 표시하고 가장 정확한 소스를 표시합니다."
      }
    ],
    "fontSmoothingTitle": "텍스트 가장자리 다듬기",
    "fontSmoothingDescription": "텍스트 선명도와 UI 부하를 자동으로 조절하거나 렌더링 모드를 고정합니다.",
    "languageTitle": "인터페이스 언어",
    "languageDescription": "Resource Manager 인터페이스에 사용할 언어를 선택합니다. 시스템은 Windows 표시 언어를 사용합니다.",
    "settingsLanguageTitle": "인터페이스 언어",
    "settingsLanguageDescription": "트레이와 창 제목을 포함한 전체 인터페이스의 언어를 변경합니다. 시스템을 선택하면 Windows 표시 언어를 사용합니다.",
    "languageSelectLabel": "언어",
    "uiStyleOptions": {
      "standard": "표준",
      "classic": "클래식",
      "soft": "부드럽게",
      "outline": "윤곽선",
      "terminal": "터미널"
    },
    "themeOptions": {
      "system": "시스템",
      "light": "밝게",
      "dark": "어둡게",
      "lowContrast": "낮은 대비",
      "paper": "종이",
      "graphite": "흑연",
      "blueprint": "청사진",
      "phosphor": "형광 녹색",
      "amber": "호박색",
      "ice": "아이스"
    },
    "barColorOptions": {
      "type": {
        "label": "유형별 고정",
        "description": "시스템, 연동 소프트웨어, 일반 소프트웨어별로 색상을 구분하며 인접 영역에 구분선을 표시합니다"
      },
      "distinct": {
        "label": "개별 색상",
        "description": "소프트웨어마다 다른 색상을 사용하며 유형 의미와 구분선은 없습니다"
      }
    },
    "byteUnitOptions": {
      "native": {
        "label": "하드웨어에 맞춤",
        "description": "메모리, VRAM, 캐시는 GiB로 표시하고(용량이 2의 거듭제곱), 디스크 용량, 파일 크기, 전송량은 제조사 표기에 맞춰 GB로 표시합니다"
      },
      "binary": {
        "label": "항상 GiB",
        "description": "항상 1024로 나누고 KiB / MiB / GiB로 표시합니다"
      },
      "decimal": {
        "label": "항상 GB",
        "description": "항상 1000으로 나누고 kB / MB / GB로 표시합니다"
      }
    },
    "fontSmoothingOptions": {
      "auto": {
        "label": "자동",
        "description": "일반적으로 시스템 ClearType을 사용하고 초고성능 모드에서는 다듬기 부하를 줄입니다"
      },
      "system": {
        "label": "시스템 ClearType",
        "description": "Windows 기본 서브픽셀 렌더링으로 가장 선명한 텍스트를 표시합니다"
      },
      "grayscale": {
        "label": "회색조",
        "description": "회색조 안티앨리어싱으로 더 부드럽게 표시하며 일부 고해상도 화면에 적합합니다"
      },
      "disabled": {
        "label": "꺼짐",
        "description": "추가 텍스트 다듬기를 끄고 글리프를 직접 래스터화합니다"
      }
    },
  },
  "systemIntegration": {
    "crashRecoveryTitle": "충돌 후 다시 시작",
    "crashRecoveryDescription": "백그라운드 서비스가 충돌하면 자동으로 다시 시작합니다. 정상 종료 시에는 다시 시작하지 않습니다.",
    "crashRestartDelayTitle": "다시 시작 전 대기 시간(초)",
    "crashRestartCountTitle": "허용되는 다시 시작 횟수",
    "crashCounterResetDescription": "충돌 없이 {days}일이 지나면 횟수가 초기화됩니다. 한도에 도달한 후에는 서비스를 수동으로 시작하세요.",
    "crashCounterResetTitle": "횟수 초기화 기간(일)",
    "crashRecoveryActualTitle": "현재 실제 설정",
    "crashRecoveryUnavailable": "자동 복구 설정을 확인할 수 없습니다.",
    "crashRecoveryApplied": "자동 복구 설정이 적용되었습니다.",
    "crashRecoverySkipped": "현재 실행 방식에서는 자동 복구 설정을 적용하지 않습니다.",
    "crashRecoveryFailed": "자동 복구 설정을 적용하지 못했습니다.",
    "crashRecoveryMismatch": "현재 실제 설정이 선택한 설정과 다릅니다.",
    "crashRecoveryNonCrashWarning": "현재 설정은 오류를 반환한 정상 종료 후에도 다시 시작합니다.",
    "crashRecoveryWriteFailed": "자동 복구 설정을 기록하지 못했습니다.",
    "crashRecoveryReadFailed": "자동 복구 설정을 읽지 못했습니다.",
    "taskManagerTitle": "작업 관리자 단축키 대체",
    "autoStartTitle": "부팅 시 백엔드 서비스 시작",
    "autoStartDescription": "Windows가 시작될 때 백엔드 서비스를 자동으로 시작합니다.",
    "loginStartTitle": "로그인 후 트레이에 표시",
    "loginStartDescription": "현재 사용자가 로그인하면 인터페이스를 시작합니다. 백엔드 서비스는 별도로 설정합니다.",
    "loginStartUnavailable": "현재 사용자의 로그인 시 시작 설정을 읽거나 저장할 수 없습니다.",
    "loginStartLoading": "로그인 시 시작 설정을 읽는 중…",
    "loginStartWriteFailed": "로그인 시 시작 설정을 저장하지 못했습니다. 스위치에는 다시 읽은 실제 상태가 표시됩니다.",
    "loginStartRetry": "다시 시도",
    "taskManagerDescription": "Ctrl+Shift+Esc를 대체합니다. 등록 후 Resource Manager가 실행 중이 아니면 시작하고, 실행 중이면 기본 창을 표시해 앞으로 가져옵니다. Ctrl+Alt+Del은 변경하지 않습니다.",
    "forceTerminateTitle": "강제 종료 단축키",
    "forceTerminateDescription": "현재 전경 앱과 보이는 응답 없는 창이 있는 모든 앱을 종료합니다. 기본적으로 비활성화되어 있습니다.",
    "forceTerminateWarning": "이 단축키는 프로세스를 즉시 종료하며 저장하지 않은 작업은 잃게 됩니다. 활성화하려면 Ctrl, Alt 또는 Win과 일반 키를 함께 지정해야 합니다.",
    "hotkeyEmpty": "설정된 키 없음",
    "hotkeyAddKey": "키 추가",
    "hotkeyKeyLabel": "키",
    "hotkeyRemoveKey": "키 제거",
    "hotkeyUnordered": "동시에 누르기",
    "hotkeyOrdered": "이전 키 먼저 누르기",
    "publicServiceTitle": "로컬 공용 서비스",
    "publicServiceDescription": "로컬 소프트웨어가 Resource Manager의 공유 기능을 사용하도록 허용합니다.",
    "publicFileIndexTitle": "공유 소프트웨어 파일 색인",
    "publicFileIndexDescription": "Resource Manager의 기존 색인을 재사용하며 조회할 때 다시 스캔하지 않습니다.",
    "publicDatabaseServiceTitle": "SQLite 데이터베이스 서비스",
    "publicDatabaseServiceDescription": "로컬 프로그램에 격리된 명명 데이터베이스, 매개변수화된 쿼리와 일괄 트랜잭션을 제공합니다.",
    "publicAiModelCatalogTitle": "통합 AI 모델 목록",
    "publicAiModelCatalogDescription": "Resource Manager가 모델 목록과 스케줄링을 관리하고 LM Studio가 모델을 실행합니다.",
    "lmStudioEndpointTitle": "LM Studio 엔드포인트",
    "lmStudioEndpointDescription": "이 컴퓨터의 주소만 허용합니다. 다른 앱은 Resource Manager를 통해 연결합니다.",
    "lmStudioAutoStartTitle": "필요할 때 LM Studio 시작",
    "lmStudioAutoStartDescription": "소프트웨어가 모델을 요청하고 LM Studio가 실행 중이 아니면 시작합니다. 모델은 자동으로 불러오지 않습니다.",
    "aiGatewayTitle": "로컬 AI 호환 키",
    "aiGatewayDescription": "LM Studio를 직접 지원하지 않는 소프트웨어용으로 OpenAI 또는 Anthropic 호환 키를 생성합니다. 요청은 이 PC에서 실행하는 오픈 모델에만 전달됩니다.",
    "aiGatewayOpenAiProfile": "OpenAI 호환",
    "aiGatewayAnthropicProfile": "Anthropic 호환",
    "aiGatewayNamePlaceholder": "용도 이름 (선택 사항)",
    "aiGatewayGenerate": "키 생성",
    "aiGatewayGenerating": "처리 중",
    "aiGatewayOneTimeTitle": "키 생성됨",
    "aiGatewayOneTimeDescription": "키는 한 번만 표시됩니다. 다시 볼 수 없으므로 지금 저장하세요.",
    "aiGatewayApiKeyLabel": "API 키",
    "aiGatewayBaseUrlLabel": "기본 URL",
    "aiGatewayCopy": "복사",
    "aiGatewayRevoke": "키 취소",
    "aiGatewayEmpty": "생성된 호환 키가 없습니다.",
    "aiGatewayLoadFailed": "로컬 AI 키를 불러올 수 없음",
    "trustedAccess": {
      "title": "신뢰된 액세스 키",
      "description": "일반 액세스 키와 일회성 권한 요청을 관리합니다.",
      "loading": "액세스 키를 불러오는 중…",
      "loadFailed": "신뢰된 액세스 키를 불러올 수 없습니다.",
      "actionFailed": "신뢰된 액세스 작업에 실패했습니다.",
      "retry": "다시 시도",
      "failureReasons": {
        "unauthorized": "로컬 서비스가 이 화면의 키를 받아들이지 않습니다. 기본 키가 사용 중지되었거나 방금 교체되었거나 무효일 수 있습니다. 다시 시도할 수 있으며, 기본 키가 사용 중지된 경우 관리자 권한으로 다시 사용하도록 설정하세요.",
        "scope-required": "현재 키에 ‘액세스 키 관리’ 범위가 없어 이 작업을 할 수 없습니다.",
        "default-scope-denied": "기본 키를 변경하려면 모든 기본 범위를 가진 키나 관리자 권한이 필요합니다."
      },
      "defaultTitle": "기본 키 파일",
      "defaultDescription": "네이티브 셸이 제어된 브리지를 통해 기본 키를 읽고 요청에 첨부합니다.",
      "defaultEnabled": "사용",
      "defaultDisabled": "사용 안 함",
      "defaultKeyName": "기본 키",
      "defaultFilePath": "파일 위치",
      "defaultFileLocation": "<설치 폴더>\\UserData\\Security\\default-key",
      "defaultSecretLocalOnly": "기본 키는 이 PC의 네이티브 셸만 읽습니다. 여기에는 키 내용이 표시되지 않습니다.",
      "defaultAccessLossWarning": "기본 키를 끄면 즉시 폐기되어 이 키에 의존하는 화면 액세스가 끊깁니다. 네이티브 셸이 다시 연결하지 못하면 독립 키 또는 관리자 권한 상승으로 복구하세요. 키를 교체하면 이전 키는 무효화되고 네이티브 셸이 기본 키 파일을 다시 읽어 연결합니다.",
      "defaultDisabledRecovery": "기본 키가 사용 중지되었습니다. 이 화면은 기본 키로 로컬 서비스에 접근하므로 이후 요청은 거부됩니다. 복구하려면 관리자 권한으로 기본 키를 다시 사용하도록 설정하세요.",
      "enableDefaultElevated": "관리자 권한으로 기본 키 다시 사용",
      "disableDefault": "기본 키 사용 중지 및 폐기",
      "regenerateDefault": "기본 키 재생성",
      "confirmDisableTitle": "기본 키를 사용 중지하고 폐기할까요?",
      "confirmDisableMessage": "이 화면은 기본 키로 로컬 서비스에 접근합니다. 사용 중지하면 기본 키가 즉시 무효가 되고 모든 페이지가 접근할 수 없게 되며, 관리자 권한으로 기본 키를 다시 사용하도록 설정해야만 복구됩니다. 복구 전에 화면을 닫으면 다시 열었을 때 화면에서 복구할 수 없습니다.",
      "confirmRegenerateTitle": "기본 키를 다시 생성할까요?",
      "confirmRegenerateMessage": "이전 기본 키는 즉시 무효가 됩니다. 네이티브 셸이 기본 키 파일을 다시 읽고 다시 연결할 때까지 화면의 요청이 거부될 수 있습니다.",
      "confirmRotateTitle": (name: string) => `‘${name}’ 키를 교체할까요?`,
      "confirmRotateMessage": "이전 키는 즉시 무효가 되며, 새 키로 바꿀 때까지 이 키를 쓰는 프로그램은 거부됩니다. 새 키는 한 번만 표시됩니다.",
      "confirmRevokeTitle": (name: string) => `‘${name}’ 키를 폐기할까요?`,
      "confirmRevokeMessage": "키가 즉시 무효가 되고 이 키를 쓰는 프로그램은 거부됩니다. 되돌릴 수 없습니다.",
      "createTitle": "독립 키 만들기",
      "createDescription": "키 범위를 선택합니다. 새 키는 한 번만 표시되며 개별적으로 교체하거나 폐기할 수 있습니다.",
      "nameLabel": "이름",
      "namePlaceholder": "예: 로컬 스크립트",
      "createScopesLabel": "허용할 범위",
      "noScopesAvailable": "현재 새 키에 허용할 수 있는 범위가 없습니다.",
      "create": "독립 키 생성",
      "working": "처리 중…",
      "oneTimeTitle": "독립 키가 생성되었습니다",
      "oneTimeDescription": "지금 복사하여 저장하세요. 다시 확인할 수 없습니다.",
      "oneTimeAnnouncement": "키가 생성되었습니다. 지금 복사해서 저장하세요.",
      "apiKeyLabel": "키",
      "copy": "복사",
      "copied": "복사됨",
      "copyFailed": "키를 복사할 수 없습니다.",
      "dismissSecret": "일회성 키 숨기기",
      "createdAt": "생성 시간",
      "keysTitle": "독립 키",
      "keysEmpty": "생성된 독립 키가 없습니다.",
      "revoke": "폐기",
      "rotate": "교체",
      "rotateKeyLabel": (name: string) => `‘${name}’ 교체`,
      "revokeKeyLabel": (name: string) => `‘${name}’ 폐기`,
      "requestsTitle": "일회성 권한 요청",
      "requestsDescription": "타사 프로그램이 요청하면 선택한 이름과 요청 범위가 표시됩니다.",
      "reviewRequests": "요청 검토",
      "requestsLoading": "권한 요청을 기다리는 중…",
      "requestsUnavailable": "권한 요청 구독을 사용할 수 없어 지금은 새 요청을 받을 수 없습니다.",
      "requestsLoadFailed": "대기 중인 권한 요청을 읽을 수 없습니다.",
      "requestsStale": "구독을 다시 연결하는 중이라 요청 목록이 최신이 아닐 수 있습니다.",
      "requestsEmpty": "대기 중인 요청이 없습니다.",
      "requestNamed": (name: string) => `요청 이름: ${name}`,
      "requestCount": (count: number) => `대기 중인 요청: ${count}개`,
      "expiresAt": "만료 시간",
      "observedExecutable": "관측된 실행 파일 경로",
      "observedProcess": "PID",
      "submittedAt": "요청 시간",
      "observedCallerIsInformational": "관측된 경로와 PID는 참고 정보이며 권한 부여 대상의 신원이 아닙니다.",
      "requestScopesLabel": "요청된 범위",
      "requestUnavailableScopeHelp": "현재 권한 범위를 벗어난 요청 범위는 선택할 수 없습니다. 승인 시 요청에 포함되고 현재 부여할 수 있는 범위만 제출됩니다.",
      "approve": "선택한 범위 승인",
      "deny": "거부",
      "denyReasonLabel": "거부 사유",
      "requestActionFailed": "권한 결정을 제출할 수 없습니다.",
      "requestApproved": "승인되었습니다. 키가 요청 프로그램에 전달되었습니다.",
      "requestDenied": "거부되었습니다.",
      "requestDeniedWithReason": (reason: string) => `거부되었습니다(이유: ${reason}).`,
      "requestExpired": "요청이 만료되었습니다.",
      "requestOutcome": (name: string, result: string) => `${name}: ${result}`,
      "denialReasons": {
        "not-approved": "승인하지 않음",
        "unsupported": "지원되지 않음",
        "duplicate": "중복 요청",
        "not-needed": "필요하지 않음"
      },
      "scopeLabels": {
        "read": "API 읽기",
        "metrics.read": "메트릭 읽기",
        "processes.read": "프로세스 읽기",
        "session": "세션 작업",
        "adapters.register.self": "현재 사용자의 어댑터 등록",
        "desktop.publish": "데스크톱 관측 게시",
        "gpu.resolve": "GPU 배치 확인",
        "gpu.report": "GPU 시작 결과 보고",
        "keys.manage": "액세스 키 관리",
        "machine.write": "시스템 설정 변경",
        "injection.configure": "시작 주입 설정"
      },
      "unknownScope": "알 수 없는 범위(이 버전에서는 설명할 수 없어 여기서 부여할 수 없음)"
    }
  },
  "debug": {
    "debugModeTitle": "디버그 모드",
    "debugModeDescription": "로컬 진단 옵션을 표시하고 활성화합니다. 문제를 조사할 때만 켜세요.",
    "debugLogTitle": "디버그 로그 저장",
    "debugLogDescription": "문제 조사에 필요한 정보를 기록합니다. 디스크와 프로세서 시간을 조금 사용할 수 있습니다.",
    "hostManagerSmartCoordinatorScoreOnlyTitle": "분석만 하고 변경하지 않음",
    "hostManagerSmartCoordinatorScoreOnlyDescription": "모니터링과 스케줄링 결과 생성을 계속하되 소프트웨어 및 하드웨어 상태는 변경하지 않습니다.",
    "hostManagerSmartCoordinatorPerformanceLogTitle": "스케줄링 성능 기록",
    "hostManagerSmartCoordinatorPerformanceLogDescription": "스케줄링 시간과 부하를 기록하여 Resource Manager 자체의 오버헤드를 조사합니다."
  },
  "credits": {
    "heroTitle": "Resource Manager 를 가능하게 만든 모든 사람과 프로젝트에 감사드립니다",
    "heroBody": "이 페이지는 Resource Manager가 사용하는 오픈 소스 프로젝트, 실행 환경, 하드웨어 지원과 주요 기여를 기록합니다.",
    "dependencyListTitle": "전체 의존성 감사 목록",
    "dependencyListBody": "아래 모든 패키지의 작성자와 유지보수 담당자에게 감사드립니다. 목록에는 프런트엔드 잠금 파일과 확인된 .NET 패키지가 빌드, 테스트, 선택적 의존성을 포함해 나열됩니다. 목록에 있다고 해서 모두 이 컴퓨터에서 로드되거나 배포되는 것은 아닙니다.",
    "groups": {
      "project": "프로젝트와 기여",
      "runtime": "런타임 및 빌드 도구",
      "windows": "Windows 및 진단 인터페이스",
      "data": "오프라인 데이터",
      "build": "빌드 도구",
      "hardware": "하드웨어 모니터링 및 추가 지원"
    },
    "roles": {
      "project": "프로젝트",
      "frontendFramework": "프런트엔드 UI 프레임워크",
      "icons": "인터페이스 아이콘",
      "serialization": "Solid 직렬화 의존성",
      "webView": "데스크톱 웹 컨테이너",
      "database": "로컬 데이터베이스",
      "nativeCompiler": "네이티브 코어 컴파일러",
      "softwareCatalog": "소프트웨어 메타데이터 출처",
      "buildToolchain": "프런트엔드 빌드 도구",
      "typeSystem": "프런트엔드 타입 시스템",
      "buildRuntime": "빌드용 런타임 및 타입 정의",
      "dotnetRuntime": "로컬 앱 실행 환경",
      "hookLibrary": "Windows 호환 지원",
      "startupInjectionLibrary": "소프트웨어 시작 호환성",
      "traceLibrary": "시스템 성능 분석",
      "windowsManagement": "Windows 시스템 정보",
      "nvidiaTelemetry": "NVIDIA GPU 측정 정보",
      "nvidiaExtension": "NVIDIA GPU 확장 인터페이스",
      "amdTelemetry": "AMD GPU/iGPU 측정 정보",
      "signedHardwareAccess": "서명된 하드웨어 접근",
      "hardwareBridge": "범용 하드웨어 센서 연결",
      "externalReference": "외부 측정 참조 및 선택적 도우미",
      "deviceIdDatabase": "하드웨어 ID 이름 데이터베이스"
    },
    "linkLabels": {
      "official": "공식 사이트",
      "github": "GitHub",
      "docs": "문서",
      "nuget": "NuGet",
      "npm": "npm",
      "license": "라이선스"
    },
    "sourceLabel": "출처",
    "dependencyColumns": {
      "name": "이름",
      "version": "버전",
      "license": "라이선스",
      "source": "출처"
    }
  }
});
