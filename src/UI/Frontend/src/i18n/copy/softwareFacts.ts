// Each bundle selects its own static copy; facts remain independent of language.
export const softwareFactsCopy = {
  "zh-CN": {
    endpointState: { online: "在线", unreachable: "不可达", unsupported: "不支持" },
    source: { dependency: "组件依赖", windows_registry: "Windows 卸载注册表", self: "内置适配与本地资源自管", adapter: "适配注册与控制端点", controlled: "L0 受控注册", portable: "运行时便携软件", manual: "手动分类与补录", identity_catalog: "软件身份目录", policy: "本机策略组", runtime: "运行时发现" },
    processCount: (count: number) => `${count} 个识别进程`,
    serviceCount: (count: number) => `${count} 个服务`,
    executableCount: (count: number) => `${count} 个便携程序入口`,
    rootConfirmed: "便携软件根目录已确认"
  },
  "zh-TW": {
    endpointState: { online: "線上", unreachable: "無法連線", unsupported: "不支援" },
    source: { dependency: "元件相依項目", windows_registry: "Windows 解除安裝登錄", self: "內建適配與本機資源自管", adapter: "適配註冊與控制端點", controlled: "L0 受控註冊", portable: "執行階段可攜式軟體", manual: "手動分類與補登", identity_catalog: "軟體身分目錄", policy: "本機原則群組", runtime: "執行階段探索" },
    processCount: (count: number) => `${count} 個識別處理程序`,
    serviceCount: (count: number) => `${count} 個服務`,
    executableCount: (count: number) => `${count} 個可攜式程式入口`,
    rootConfirmed: "可攜式軟體根目錄已確認"
  },
  "en-US": {
    endpointState: { online: "Online", unreachable: "Unreachable", unsupported: "Unsupported" },
    source: { dependency: "Component dependency", windows_registry: "Windows uninstall registry", self: "Built-in adaptation and local resource management", adapter: "Adapter registration and control endpoint", controlled: "L0 controlled registration", portable: "Runtime portable software", manual: "Manual classification and registration", identity_catalog: "Software identity catalog", policy: "Local policy group", runtime: "Runtime discovery" },
    processCount: (count: number) => `${count} recognized processes`,
    serviceCount: (count: number) => `${count} services`,
    executableCount: (count: number) => `${count} portable executables`,
    rootConfirmed: "Portable software root confirmed"
  },
  "ja-JP": {
    endpointState: { online: "オンライン", unreachable: "到達不可", unsupported: "非対応" },
    source: { dependency: "コンポーネント依存関係", windows_registry: "Windows アンインストール登録", self: "組み込み連携とローカルリソース管理", adapter: "連携登録と制御エンドポイント", controlled: "L0 制御対象登録", portable: "実行時に検出したポータブルソフトウェア", manual: "手動分類と登録", identity_catalog: "ソフトウェア識別カタログ", policy: "ローカルポリシーグループ", runtime: "実行時の検出" },
    processCount: (count: number) => `識別プロセス ${count} 件`,
    serviceCount: (count: number) => `サービス ${count} 件`,
    executableCount: (count: number) => `ポータブル実行ファイル ${count} 件`,
    rootConfirmed: "ポータブルソフトウェアのルートを確認済み"
  },
  "ko-KR": {
    endpointState: { online: "온라인", unreachable: "연결할 수 없음", unsupported: "지원되지 않음" },
    source: { dependency: "구성 요소 종속성", windows_registry: "Windows 제거 레지스트리", self: "기본 제공 연동 및 로컬 리소스 관리", adapter: "연동 등록 및 제어 엔드포인트", controlled: "L0 제어 대상 등록", portable: "실행 중 발견한 포터블 소프트웨어", manual: "수동 분류 및 등록", identity_catalog: "소프트웨어 식별 카탈로그", policy: "로컬 정책 그룹", runtime: "실행 중 발견" },
    processCount: (count: number) => `식별된 프로세스 ${count}개`,
    serviceCount: (count: number) => `서비스 ${count}개`,
    executableCount: (count: number) => `포터블 실행 파일 ${count}개`,
    rootConfirmed: "포터블 소프트웨어 루트 확인됨"
  },
  "fr-FR": {
    endpointState: { online: "En ligne", unreachable: "Inaccessible", unsupported: "Non pris en charge" },
    source: { dependency: "Dépendance de composant", windows_registry: "Registre de désinstallation Windows", self: "Adaptation intégrée et gestion locale des ressources", adapter: "Inscription de l’adaptateur et point de contrôle", controlled: "Inscription contrôlée L0", portable: "Logiciel portable détecté à l’exécution", manual: "Classification et inscription manuelles", identity_catalog: "Catalogue d’identité des logiciels", policy: "Groupe de stratégie locale", runtime: "Détection à l’exécution" },
    processCount: (count: number) => `${count} processus reconnus`,
    serviceCount: (count: number) => `${count} services associés`,
    executableCount: (count: number) => `${count} exécutables portables`,
    rootConfirmed: "Racine du logiciel portable confirmée"
  },
  "de-DE": {
    endpointState: { online: "Verbunden", unreachable: "Nicht erreichbar", unsupported: "Nicht unterstützt" },
    source: { dependency: "Komponentenabhängigkeit", windows_registry: "Windows-Deinstallationsregistrierung", self: "Integrierte Anpassung und lokale Ressourcenverwaltung", adapter: "Adapterregistrierung und Steuerungsendpunkt", controlled: "Kontrollierte L0-Registrierung", portable: "Zur Laufzeit erkannte portable Software", manual: "Manuelle Klassifizierung und Registrierung", identity_catalog: "Softwareidentitätskatalog", policy: "Lokale Richtliniengruppe", runtime: "Laufzeiterkennung" },
    processCount: (count: number) => `${count} erkannte Prozesse`,
    serviceCount: (count: number) => `${count} Dienste`,
    executableCount: (count: number) => `${count} portable Programmdateien`,
    rootConfirmed: "Stammverzeichnis der portablen Software bestätigt"
  },
  "es-ES": {
    endpointState: { online: "En línea", unreachable: "Inaccesible", unsupported: "No compatible" },
    source: { dependency: "Dependencia de componente", windows_registry: "Registro de desinstalación de Windows", self: "Adaptación integrada y gestión local de recursos", adapter: "Registro de adaptador y punto de control", controlled: "Registro controlado L0", portable: "Software portátil detectado en ejecución", manual: "Clasificación y registro manuales", identity_catalog: "Catálogo de identidad de software", policy: "Grupo de directivas locales", runtime: "Detección en ejecución" },
    processCount: (count: number) => `${count} procesos reconocidos`,
    serviceCount: (count: number) => `${count} servicios`,
    executableCount: (count: number) => `${count} ejecutables portátiles`,
    rootConfirmed: "Directorio raíz del software portátil confirmado"
  },
  "ru-RU": {
    endpointState: { online: "Подключено", unreachable: "Недоступно", unsupported: "Не поддерживается" },
    source: { dependency: "Зависимость компонента", windows_registry: "Реестр удаления Windows", self: "Встроенная адаптация и локальное управление ресурсами", adapter: "Регистрация адаптера и точка управления", controlled: "Управляемая регистрация L0", portable: "Переносное ПО, обнаруженное во время работы", manual: "Ручная классификация и регистрация", identity_catalog: "Каталог идентификации ПО", policy: "Группа локальной политики", runtime: "Обнаружение во время работы" },
    processCount: (count: number) => `Распознано процессов: ${count}`,
    serviceCount: (count: number) => `Служб: ${count}`,
    executableCount: (count: number) => `Переносных исполняемых файлов: ${count}`,
    rootConfirmed: "Корневой каталог переносного ПО подтверждён"
  }
};
