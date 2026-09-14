import { createSettingsLocale } from "../settingsLocaleFactory";

export default createSettingsLocale("ko-KR", {
  sections: { performance: "성능", appearance: "모양", systemIntegration: "시스템 통합", debug: "디버그", credits: "감사의 글" },
  saveState: { saving: "저장 중", saved: "저장됨", error: "저장 실패" },
  appearance: {
    languageTitle: "인터페이스 언어",
    languageDescription: "Resource Manager 인터페이스에 사용할 언어를 선택합니다. 시스템은 Windows 표시 언어를 사용합니다.",
    languageSelectLabel: "언어",
    themeOptions: { system: "시스템", light: "밝게", dark: "어둡게", lowContrast: "낮은 대비" },
    animationOptions: {
      auto: { label: "자동", description: "전면 작업 중에는 일반 애니메이션을 사용하고, 저전력 백그라운드 상태에서는 고성능 표시로 전환합니다" },
      none: { label: "애니메이션 없음", description: "모든 전환과 애니메이션을 끕니다" },
      normal: { label: "일반 애니메이션", description: "절제된 전역 전환을 사용합니다" },
      ultra: { label: "초고성능", description: "애니메이션, 그림자, 하이라이트, 흐림 효과를 끄고 렌더링 비용을 최소화합니다" }
    }
  },
  credits: {
    heroTitle: "Resource Manager 를 가능하게 만든 모든 사람과 프로젝트에 감사드립니다",
    heroBody: "이 페이지는 Resource Manager가 사용하는 오픈 소스 프로젝트, 실행 환경, 하드웨어 지원과 주요 기여를 기록합니다.",
    groups: { project: "프로젝트와 기여", runtime: "런타임 및 빌드 도구", windows: "Windows 및 진단 인터페이스", hardware: "하드웨어 모니터링 및 추가 지원" }
  }
});
