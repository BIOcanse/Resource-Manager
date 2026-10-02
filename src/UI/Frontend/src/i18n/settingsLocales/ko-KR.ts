import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("ko-KR", {
  "updates": {
    "autoUpdateTitle": "자동 업데이트",
    "autoUpdateDescription": "자동 업데이트 설정을 저장합니다. 신뢰할 수 있는 릴리스 서명 지원 전에는 자동 설치하지 않습니다. 수동 업데이트는 사용할 수 있습니다.",
    "installedVersionTitle": "설치된 버전",
    "unknownVersion": "알 수 없음",
    "checking": "확인 중…",
    "checkForUpdates": "업데이트 확인",
    "catalogFailed": "릴리스 목록을 읽을 수 없음: ",
    "historyIncomplete": "릴리스 기록을 아직 모두 읽지 못했습니다.",
    "historyStale": "이전에 읽은 릴리스 목록을 표시합니다. 다시 연결한 후 재시도하세요.",
    "versionsTitle": "제품 버전",
    "versionsDescription": "최신순으로 표시합니다. 각 계열은 기본적으로 최신 릴리스를 표시합니다.",
    "lastChecked": "마지막 확인: ",
    "selectedTarget": "선택한 대상 버전: ",
    "prepare": "선택한 업데이트 준비 및 설치",
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
      "无法确认当前安装版本，升级选择已关闭。": "설치된 버전을 알 수 없어 업그레이드 선택이 비활성화되었습니다.",
      "只能选择高于当前安装版本的发行版。": "설치된 버전보다 새로운 릴리스만 선택할 수 있습니다."
    }
  },
  "navigationLabel": "설정 항목",
  "sections": {
    "performance": "성능",
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
    "partial": "저장되었지만 일부는 적용되지 않음",
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
    "reapply": "다시 적용"
  },
  "performance": {
    "smartMonitoringTitle": "필요할 때 모니터링",
    "smartMonitoringDescription": (seconds: number) => `데이터를 사용하는 동안만 모니터링하고, ${seconds}초 동안 사용하지 않으면 중지합니다.`,
    "adaptiveBooleanModeOptions": [
      {
        "id": "auto",
        "label": "자동",
        "description": "현재 성능 모드에 따라 자동으로 선택합니다."
      },
      {
        "id": "enabled",
        "label": "항상 켜기",
        "description": "이 설정을 항상 활성화합니다."
      },
      {
        "id": "disabled",
        "label": "항상 끄기",
        "description": "이 설정을 항상 비활성화합니다."
      }
    ],
    "gpuPerformanceUseCasesTitle": "GPU 용도",
    "gpuPerformanceUseCasesDescription": "GPU 성능을 적절히 비교하기 위해 주요 작업을 선택하세요. 여러 항목을 선택할 수 있습니다.",
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
    "automaticMemoryCleanupTitle": "자동 정리 임계값",
    "automaticMemoryCleanupDescription": "리소스가 부족하고 안전한 이동을 계속할 수 없을 때 백그라운드 정리를 시작합니다.",
    "physicalMemoryAutomaticCleanupLabel": "물리 메모리 여유 %",
    "virtualMemoryAutomaticCleanupLabel": "커밋 여유 %",
    "memoryOptimizationTargetTitle": "메모리 최적화 목표",
    "memoryOptimizationTargetDescription": "사용량이 목표를 넘으면 1단계의 느린 해제를 계속하며 긴급 정리는 실행하지 않습니다.",
    "physicalMemoryOptimizationTargetLabel": "물리 메모리 목표 사용률 %",
    "virtualMemoryOptimizationTargetLabel": "목표 커밋 사용률 %",
    "pauseHiddenTitle": "창이 숨겨졌을 때 갱신",
    "pauseHiddenDescription": "일반 모드에서는 창이 숨겨진 동안 UI 갱신을 멈추고 표시되면 즉시 재개할 수 있습니다.",
    "frontendHiddenRefreshModeOptions": [
      {
        "id": "auto",
        "label": "자동",
        "description": "현재 성능 모드에 따라 자동으로 결정합니다."
      },
      {
        "id": "pauseWhenHidden",
        "label": "숨겨지면 일시 중지",
        "description": "창이 숨겨진 동안 UI 갱신을 일시 중지합니다."
      },
      {
        "id": "continueWhenHidden",
        "label": "계속 갱신",
        "description": "창이 숨겨진 동안에도 UI 갱신을 계속합니다."
      }
    ],
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
    "refreshCadenceCustomLabel": "사용자 지정 ms",
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
      "management": {
        "label": "구성 요소 및 소프트웨어",
        "description": "구성 요소 상태, 소프트웨어 목록 및 작업 진행률을 갱신합니다."
      },
      "discovery": {
        "label": "소프트웨어 이동",
        "description": "이동 진행률 및 사용 가능한 콘텐츠를 갱신합니다."
      },
      "optimization": {
        "label": "성능 최적화",
        "description": "최적화 보고서와 스케줄링 상태를 갱신합니다."
      },
      "localSystem": {
        "label": "로컬 시스템",
        "description": "가동 시간과 같은 저빈도 로컬 상태를 갱신합니다."
      }
    }
  },
  "appearance": {
    "themeTitle": "색상 모드",
    "themeDescription": "시스템, 밝게, 어둡게 또는 낮은 대비 테마를 선택합니다.",
    "animationTitle": "애니메이션",
    "animationDescription": "전체 애니메이션, 애니메이션 없음 또는 UI 부하를 줄이는 간소화된 효과를 선택합니다.",
    "resourceBarHardwareAccelerationTitle": "리소스 막대 하드웨어 가속",
    "resourceBarHardwareAccelerationDescription": "그래픽 카드로 리소스 막대 애니메이션을 개선합니다. 표시 문제가 있으면 끄세요.",
    "resourceBarHardwareAccelerationModeOptions": [
      {
        "id": "auto",
        "label": "자동",
        "description": "전경에서는 부드러운 표시를 유지하고 저전력 백그라운드 상태에서는 부하를 줄입니다."
      },
      {
        "id": "enabled",
        "label": "항상 켜기",
        "description": "리소스 막대에 하드웨어 가속을 항상 사용합니다."
      },
      {
        "id": "disabled",
        "label": "항상 끄기",
        "description": "리소스 막대에 호환 렌더링을 항상 사용합니다."
      }
    ],
    "barColorTitle": "막대 색상",
    "barColorDescription": "소프트웨어 유형별 고정 색상 또는 소프트웨어별 다른 색상을 사용합니다.",
    "byteUnitTitle": "용량 단위",
    "byteUnitDescription": "1 GiB = 1024 MiB, 1 GB = 1000 MB입니다. 전환하면 단위 표시뿐 아니라 숫자 자체도 변환됩니다.",
    "fontSmoothingTitle": "텍스트 가장자리 다듬기",
    "fontSmoothingDescription": "텍스트 선명도와 UI 부하를 자동으로 조절하거나 렌더링 모드를 고정합니다.",
    "languageTitle": "인터페이스 언어",
    "languageDescription": "Resource Manager 인터페이스에 사용할 언어를 선택합니다. 시스템은 Windows 표시 언어를 사용합니다.",
    "settingsLanguageTitle": "인터페이스 언어",
    "settingsLanguageDescription": "트레이와 창 제목을 포함한 전체 인터페이스의 언어를 변경합니다. 시스템을 선택하면 Windows 표시 언어를 사용합니다.",
    "languageSelectLabel": "언어",
    "themeOptions": {
      "system": "시스템",
      "light": "밝게",
      "dark": "어둡게",
      "lowContrast": "낮은 대비"
    },
    "animationOptions": {
      "auto": {
        "label": "자동",
        "description": "전면 작업 중에는 일반 애니메이션을 사용하고, 저전력 백그라운드 상태에서는 고성능 표시로 전환합니다"
      },
      "normal": {
        "label": "일반 애니메이션",
        "description": "절제된 전역 전환을 사용합니다"
      },
      "none": {
        "label": "애니메이션 없음",
        "description": "모든 전환과 애니메이션을 끕니다"
      },
      "ultra": {
        "label": "초고성능",
        "description": "애니메이션, 그림자, 하이라이트, 흐림 효과를 끄고 렌더링 비용을 최소화합니다"
      }
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
    "gpuPerformanceUseCaseOptions": {
      "general": {
        "label": "일반",
        "description": "래스터 성능 기본 점수에 작은 세대 보너스를 항상 추가합니다"
      },
      "ai": {
        "label": "AI",
        "description": "세대 가중치를 높이고 NVIDIA 생태계, VRAM 용량 및 대역폭에 높은 가중치를 부여합니다"
      },
      "gaming": {
        "label": "게임",
        "description": "가장 큰 세대 보너스와 작은 VRAM 용량 보너스를 적용합니다"
      }
    }
  },
  "systemIntegration": {
    "taskManagerTitle": "작업 관리자 단축키 대체",
    "autoStartTitle": "Windows 시작 시 실행",
    "autoStartDescription": "시작 시 서비스를 실행하고 로그인 후 트레이에 유지합니다.",
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
    "aiGatewayLoadFailed": "로컬 AI 키를 불러올 수 없음"
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
      "etwToolkit": "Windows 성능 분석 도구",
      "nvidiaTelemetry": "NVIDIA GPU 측정 정보",
      "nvidiaExtension": "NVIDIA GPU 확장 인터페이스",
      "amdTelemetry": "AMD GPU/iGPU 측정 정보",
      "amdCpuSdk": "AMD CPU 센서 SDK",
      "intelTelemetry": "Intel CPU / MSR 측정 정보",
      "amdSmuBoundary": "AMD SMU 접근 경계",
      "hardwareBridge": "범용 하드웨어 센서 연결",
      "notebookEc": "노트북 팬 지원",
      "externalReference": "외부 측정 참조 및 선택적 도우미",
      "latencyDiagnostics": "지연 진단 도우미",
      "deviceIdDatabase": "하드웨어 ID 이름 데이터베이스"
    },
    "notes": {
      "windowsFoundation": "운영체제가 제공하는 Win32, Shell, COM, Direct3D/DXGI, PDH, ETW, 서비스, 암호화 및 네트워킹에 대해 Microsoft와 Windows 개발팀에 감사드립니다.",
      "microsoftTools": "컴파일, 게시, 패키지 관리 및 자동화를 지원하는 .NET SDK, MSBuild, NuGet와 PowerShell의 작성자와 유지보수 담당자에게 감사드립니다.",
      "managedServices": "서비스 수명 주기 통합과 기반 API를 제공한 Microsoft와 .NET 기여자에게 감사드립니다. 이 대상의 TypeExtensions는 의존성 확인용 자리표시자이며 추가 DLL이 아닙니다.",
      "nativeToolchain": "네이티브 컴파일과 실행 기반을 제공한 GCC 및 MinGW 작성자와 유지보수 담당자에게 감사드립니다. 일부 구성 요소는 libgcc/libstdc++를 정적으로 링크하며 재배포 검토는 별도로 수행합니다.",
      "buildContributors": "Solid 컴파일 및 갱신 도구, 소스 맵, 브라우저 데이터와 소형 유틸리티를 포함한 모든 빌드 의존성의 작성자와 유지보수 담당자에게 감사드립니다. 전체 목록에는 선택적 플랫폼을 포함한 모든 고정 패키지와 버전이 유지됩니다.",
      "testContributors": "브라우저 자동화, 회귀 테스트 및 커버리지를 제공한 Playwright, xUnit.net, Coverlet와 Microsoft 테스트 플랫폼 기여자에게 감사드립니다. 이들은 개발 도구이며 기본 제품의 실행 의존성이 아닙니다.",
      "openHardwareMonitor": "하드웨어 센서 인터페이스를 제공한 OpenHardwareMonitor 작성자와 기여자에게 감사드립니다. 설치된 외부 제공자가 WMI 데이터를 제공할 수 있으며 번들 포함을 뜻하지 않습니다.",
      "upstreamContributors": "ANTLR, Unicode, Mono, Brotli와 LLVM 기여자를 포함해 원본 .NET 및 WebView2 고지에 나오는 모든 작성자에게 감사드립니다. 원본 전체를 보존하며 이는 실행 중 DLL별 목록이 아닙니다.",
      "resourceManager": "Copyright (c) 2026 BIOcanse. 이 프로젝트는 Apache-2.0을 사용합니다. 타사 구성 요소에는 각각의 라이선스가 유지됩니다.",
      "solid": "현재 경량 웹 셸 프런트엔드의 반응형 구성 요소 모델을 제공합니다.",
      "lucide": "인터페이스는 lucide-solid를 사용하며 Lucide ISC 라이선스와 Feather 파생 아이콘의 MIT 라이선스 및 저작자 표시를 보존합니다.",
      "seroval": "Solid의 간접 의존성이며 MIT 라이선스와 Alexis Munsayac 저작자 표시를 보존합니다.",
      "webView2": "데스크톱 컨테이너는 WebView2 SDK를 사용합니다. 시스템 WebView2 Evergreen Runtime은 Microsoft가 자체 조건으로 별도 배포합니다.",
      "sqlite": "로컬 데이터 저장에 사용합니다. Microsoft.Data.Sqlite는 MIT, SQLitePCLRaw 3.x는 Apache-2.0을 사용합니다. SourceGear가 배포하는 SQLite 코어는 업스트림에서 퍼블릭 도메인으로 공개했습니다.",
      "zig": "네이티브 코어를 컴파일하며 사용자가 앱을 실행할 때 컴파일러는 필요하지 않습니다.",
      "softwareCatalog": "오프라인 목록은 MIT 라이선스의 WinGet 메타데이터와 CC0 Wikidata 구조화 데이터를 사용합니다. 요약과 중국어 번역은 프로젝트에서 작성한 편집 자료입니다.",
      "vite": "개발 빌드와 Solid 프런트엔드 패키징을 처리합니다.",
      "typescript": "인터페이스 코드와 설정 모델의 신뢰성을 높입니다.",
      "node": "프런트엔드 빌드와 개발 도구를 지원합니다.",
      "dotnet": "Resource Manager의 로컬 실행 환경을 제공합니다.",
      "minHook": "후킹과 명령어 디코딩을 제공한 Tsuda Kageyu, 기여자 및 HDE 작성자 Vyacheslav Patkov에게 감사드립니다. 원본 라이선스와 저작자 표시를 전체 보존합니다.",
      "detours": "소프트웨어 시작 호환성에 사용하는 Microsoft Research 오픈소스 프로젝트입니다. 원본 라이선스와 저작권 고지를 보존합니다.",
      "traceEvent": "Windows 시스템 성능 정보에 대한 접근을 제공합니다.",
      "systemManagement": "Windows 시스템 및 하드웨어 정보에 대한 접근을 제공합니다.",
      "wpt": "Windows 성능 조사용 Microsoft 진단 도구이며 별도로 설치합니다. 기본 앱과 함께 설치되지 않습니다.",
      "nvml": "NVIDIA GPU 클럭, 메모리, 전력 및 온도 정보를 제공합니다.",
      "nvapi": "NVIDIA GPU 팬, 냉각 및 전기 정보를 추가합니다.",
      "adlx": "AMD GPU 사용률, 클럭, 전력, 온도 및 전압 정보를 제공합니다.",
      "ryzenMaster": "AMD 프로세서 전력, 전압, 전류 및 온도 정보를 추가합니다. 설치와 라이선스에는 명시적인 확인이 필요합니다.",
      "intelPcm": "Intel 프로세서 전력, 주파수 및 온도 정보를 추가합니다.",
      "pawnIo": "읽기 전용 AMD SMU PM 테이블 측정에 쓰는 공식 서명 드라이버 경로이며 알리지 않고 설치하지 않습니다.",
      "libreHardwareMonitor": "선택적으로 CPU 및 시스템 팬, 메모리, 메인보드, VRM, 칩셋 온도와 전압 모니터링 항목을 제공합니다.",
      "notebookFanControl": "범용 하드웨어 모니터와 GPU 드라이버 모두 노트북 팬 값을 제공하지 않을 때 사용하는 선택적 경로입니다.",
      "afterburner": "외부 비교 및 참조와 선택적 도우미로 사용하며 기본 실행 의존성이 아닙니다.",
      "latencyMon": "비교 도구로 ISR, DPC, 하드 페이지 폴트 및 드라이버 지연 진단을 돕습니다.",
      "usbIds": "USB VID/PID 제조사와 장치 이름을 로컬 오프라인으로 제공합니다. 장치 세부정보를 요청할 때만 불러옵니다.",
      "pciIds": "PCI VEN/DEV/SUBSYS 제조사, 장치 및 하위 시스템 이름을 로컬 오프라인으로 제공합니다. 장치 세부정보를 요청할 때만 불러옵니다."
    },
    "linkLabels": {
      "official": "Website",
      "github": "GitHub",
      "docs": "Docs",
      "license": "라이선스",
      "nuget": "NuGet",
      "gpuOpen": "GPUOpen",
      "eula": "EULA",
      "runtime": "런타임",
      "aspnet": "ASP.NET Core",
      "vite": "Vite",
      "solidPlugin": "Solid plugin"
    }
  }
});
