import type { ConcreteAppLanguageMode } from "../settingsTypes.ts";

interface ControlPageCopy {
  guide: string; channelsTitle: string; presentDevice: string; unknownControl: string; unknownChannel: string;
  selectSetting: string; incompleteSetting: string;
  channels: Record<string, string>;
}

// Cover every persisted language ID; the selectable language list is managed separately.
export const controlPageCopy: Record<ConcreteAppLanguageMode, ControlPageCopy> = {
  "zh-CN": {
    guide: "选好管理方式和设定后，点击该设备的“应用”。保存配置或曲线不会应用；离开页面会丢弃未应用的设定。",
    channelsTitle: "写入通道", presentDevice: "设备仍已连接，不能删除记录。断开设备后可删除。",
    unknownControl: "未命名控制项", unknownChannel: "其他通道", selectSetting: "请选择要应用的项目并填写设定。", incompleteSetting: "请先填写已选项目的数值或曲线。",
    channels: { nvapi: "NVIDIA 驱动提供的显卡调节接口。", "nvapi-drs": "NVIDIA 驱动的配置项。", nvml: "NVIDIA 的设备管理接口。",
      "oem-ec": "通过整机厂商的控制器调节硬件，限制由该机型的固件决定。", "amd-smu": "AMD 处理器的电源与性能控制器。",
      "fan-core": "通过风扇控制组件调节转速。", igcl: "Intel 显卡的控制接口。", "intel-msr": "Intel 处理器的控制寄存器。", adlx: "AMD 显卡驱动的控制接口。" }
  },
  "en-US": {
    guide: "Choose the management mode and settings, then Apply for that device. Saving a preset or curve does not apply it. Leaving this page discards unapplied settings.",
    channelsTitle: "Write channels", presentDevice: "This device is connected. Disconnect it before deleting its record.",
    unknownControl: "Unnamed control", unknownChannel: "Other channel", selectSetting: "Select an item and enter its settings before applying.", incompleteSetting: "Enter a value or curve for each selected item.",
    channels: { nvapi: "GPU controls provided by the NVIDIA driver.", "nvapi-drs": "NVIDIA driver profile settings.", nvml: "NVIDIA device management interface.",
      "oem-ec": "Controls through the computer manufacturer's controller; limits depend on the model's firmware.", "amd-smu": "AMD processor power and performance controller.",
      "fan-core": "Fan speed controls provided by the fan control component.", igcl: "Intel graphics control interface.", "intel-msr": "Intel processor control registers.", adlx: "AMD graphics driver control interface." }
  },
  "zh-TW": {
    guide: "選好管理方式與設定後，按該裝置的「套用」。儲存設定檔或曲線不會套用；離開頁面會捨棄尚未套用的設定。",
    channelsTitle: "寫入通道", presentDevice: "裝置仍已連接，無法刪除記錄。中斷連接後即可刪除。",
    unknownControl: "未命名控制項", unknownChannel: "其他通道", selectSetting: "請選擇要套用的項目並填寫設定。", incompleteSetting: "請先填寫已選項目的數值或曲線。",
    channels: { nvapi: "NVIDIA 驅動程式提供的顯示卡調節介面。", "nvapi-drs": "NVIDIA 驅動程式的設定項目。", nvml: "NVIDIA 的裝置管理介面。",
      "oem-ec": "透過整機廠商的控制器調節硬體，限制由該機型的韌體決定。", "amd-smu": "AMD 處理器的電源與效能控制器。",
      "fan-core": "透過風扇控制元件調節轉速。", igcl: "Intel 顯示卡的控制介面。", "intel-msr": "Intel 處理器的控制暫存器。", adlx: "AMD 顯示卡驅動程式的控制介面。" }
  },
  "de-DE": {
    guide: "Wählen Sie Verwaltung und Einstellungen und klicken Sie beim Gerät auf Anwenden. Gespeicherte Profile oder Kurven werden dabei nicht angewendet. Beim Verlassen der Seite werden nicht angewendete Einstellungen verworfen.",
    channelsTitle: "Schreibkanäle", presentDevice: "Das Gerät ist verbunden. Trennen Sie es, bevor Sie seinen Eintrag löschen.",
    unknownControl: "Unbenannte Einstellung", unknownChannel: "Anderer Kanal", selectSetting: "Wählen Sie eine Einstellung aus und geben Sie ihre Werte ein.", incompleteSetting: "Geben Sie für jede ausgewählte Einstellung einen Wert oder eine Kurve ein.",
    channels: { nvapi: "GPU-Steuerung über den NVIDIA-Treiber.", "nvapi-drs": "Profileinstellungen des NVIDIA-Treibers.", nvml: "Schnittstelle zur NVIDIA-Geräteverwaltung.",
      "oem-ec": "Steuerung über den Controller des Computerherstellers; die Grenzen hängen von der Firmware des Modells ab.", "amd-smu": "Controller für Leistung und Energieversorgung von AMD-Prozessoren.",
      "fan-core": "Lüfterdrehzahlsteuerung über die Lüfterkomponente.", igcl: "Intel-Grafiksteuerung.", "intel-msr": "Steuerregister des Intel-Prozessors.", adlx: "Steuerschnittstelle des AMD-Grafiktreibers." }
  },
  "es-ES": {
    guide: "Elija el modo de gestión y los ajustes y pulse Aplicar en el dispositivo. Guardar un perfil o una curva no los aplica. Al salir de esta página se descartan los ajustes sin aplicar.",
    channelsTitle: "Canales de escritura", presentDevice: "El dispositivo está conectado. Desconéctelo antes de eliminar su registro.",
    unknownControl: "Control sin nombre", unknownChannel: "Otro canal", selectSetting: "Seleccione un ajuste e introduzca sus valores antes de aplicarlo.", incompleteSetting: "Introduzca un valor o una curva para cada ajuste seleccionado.",
    channels: { nvapi: "Controles de GPU del controlador NVIDIA.", "nvapi-drs": "Ajustes de perfiles del controlador NVIDIA.", nvml: "Interfaz de gestión de dispositivos NVIDIA.",
      "oem-ec": "Control a través del fabricante del equipo; los límites dependen del firmware del modelo.", "amd-smu": "Control de energía y rendimiento del procesador AMD.",
      "fan-core": "Control de velocidad mediante el componente de ventiladores.", igcl: "Interfaz de control de gráficos Intel.", "intel-msr": "Registros de control del procesador Intel.", adlx: "Interfaz de control del controlador gráfico AMD." }
  },
  "fr-FR": {
    guide: "Choisissez le mode de gestion et les réglages, puis cliquez sur Appliquer pour l’appareil. Enregistrer un profil ou une courbe ne les applique pas. Quitter cette page abandonne les réglages non appliqués.",
    channelsTitle: "Canaux d’écriture", presentDevice: "Cet appareil est connecté. Déconnectez-le avant de supprimer son enregistrement.",
    unknownControl: "Réglage sans nom", unknownChannel: "Autre canal", selectSetting: "Sélectionnez un réglage et saisissez ses valeurs avant de l’appliquer.", incompleteSetting: "Saisissez une valeur ou une courbe pour chaque réglage sélectionné.",
    channels: { nvapi: "Réglages du GPU fournis par le pilote NVIDIA.", "nvapi-drs": "Profils du pilote NVIDIA.", nvml: "Interface de gestion des appareils NVIDIA.",
      "oem-ec": "Commande via le contrôleur du fabricant de l’ordinateur ; les limites dépendent du micrologiciel du modèle.", "amd-smu": "Contrôleur d’alimentation et de performances du processeur AMD.",
      "fan-core": "Réglage de la vitesse par le composant de contrôle des ventilateurs.", igcl: "Interface de contrôle graphique Intel.", "intel-msr": "Registres de contrôle du processeur Intel.", adlx: "Interface de contrôle du pilote graphique AMD." }
  },
  "ja-JP": {
    guide: "管理方法と設定を選び、対象デバイスの「適用」を押してください。設定やカーブの保存だけでは適用されません。ページを離れると未適用の設定は破棄されます。",
    channelsTitle: "書き込み経路", presentDevice: "デバイスは接続中です。接続を解除してから記録を削除してください。",
    unknownControl: "名前のない設定", unknownChannel: "その他の経路", selectSetting: "適用する項目を選び、値を入力してください。", incompleteSetting: "選択した各項目に値またはカーブを設定してください。",
    channels: { nvapi: "NVIDIA ドライバーが提供する GPU 制御インターフェース。", "nvapi-drs": "NVIDIA ドライバーのプロファイル設定。", nvml: "NVIDIA のデバイス管理インターフェース。",
      "oem-ec": "PC メーカーのコントローラーを経由します。制限は機種のファームウェアに依存します。", "amd-smu": "AMD プロセッサーの電力・性能コントローラー。",
      "fan-core": "ファン制御コンポーネントによる回転数の調整。", igcl: "Intel グラフィックスの制御インターフェース。", "intel-msr": "Intel プロセッサーの制御レジスター。", adlx: "AMD グラフィックスドライバーの制御インターフェース。" }
  },
  "ko-KR": {
    guide: "관리 방식과 설정을 선택한 다음 해당 장치에서 적용을 누르십시오. 프로필이나 곡선을 저장해도 적용되지는 않습니다. 페이지를 떠나면 적용하지 않은 설정은 삭제됩니다.",
    channelsTitle: "쓰기 경로", presentDevice: "장치가 연결되어 있습니다. 연결을 해제한 후 기록을 삭제하십시오.",
    unknownControl: "이름 없는 제어 항목", unknownChannel: "기타 경로", selectSetting: "적용할 항목을 선택하고 값을 입력하십시오.", incompleteSetting: "선택한 각 항목에 값이나 곡선을 입력하십시오.",
    channels: { nvapi: "NVIDIA 드라이버에서 제공하는 GPU 제어 인터페이스입니다.", "nvapi-drs": "NVIDIA 드라이버 프로필 설정입니다.", nvml: "NVIDIA 장치 관리 인터페이스입니다.",
      "oem-ec": "컴퓨터 제조사의 컨트롤러를 사용하며, 제한은 해당 모델의 펌웨어에 따라 결정됩니다.", "amd-smu": "AMD 프로세서의 전력 및 성능 컨트롤러입니다.",
      "fan-core": "팬 제어 구성 요소를 통해 회전 속도를 조절합니다.", igcl: "Intel 그래픽 제어 인터페이스입니다.", "intel-msr": "Intel 프로세서 제어 레지스터입니다.", adlx: "AMD 그래픽 드라이버 제어 인터페이스입니다." }
  },
  "es-MX": {
    guide: "Seleccione el modo de administración y los ajustes; después, haga clic en Aplicar para el dispositivo. Guardar un perfil o una curva no los aplica. Al salir de esta página, se descartan los ajustes que no se hayan aplicado.",
    channelsTitle: "Canales de escritura", presentDevice: "El dispositivo está conectado. Desconéctelo antes de eliminar su registro.",
    unknownControl: "Control sin nombre", unknownChannel: "Otro canal", selectSetting: "Seleccione un elemento e ingrese sus ajustes antes de aplicarlos.", incompleteSetting: "Ingrese un valor o una curva para cada elemento seleccionado.",
    channels: { nvapi: "Controles de la GPU proporcionados por el controlador de NVIDIA.", "nvapi-drs": "Ajustes de los perfiles del controlador de NVIDIA.", nvml: "Interfaz de administración de dispositivos de NVIDIA.",
      "oem-ec": "Control mediante el controlador del fabricante de la computadora; los límites dependen del firmware del modelo.", "amd-smu": "Controlador de energía y rendimiento del procesador AMD.",
      "fan-core": "Control de velocidad mediante el componente de control de ventiladores.", igcl: "Interfaz de control de gráficos Intel.", "intel-msr": "Registros de control del procesador Intel.", adlx: "Interfaz de control del controlador de gráficos AMD." }
  },
  "pt-BR": {
    guide: "Selecione o modo de gerenciamento e as configurações e clique em Aplicar no dispositivo. Salvar um perfil ou uma curva não os aplica. Ao sair desta página, as configurações não aplicadas serão descartadas.",
    channelsTitle: "Canais de gravação", presentDevice: "O dispositivo está conectado. Desconecte-o antes de excluir seu registro.",
    unknownControl: "Controle sem nome", unknownChannel: "Outro canal", selectSetting: "Selecione um item e preencha suas configurações antes de aplicar.", incompleteSetting: "Informe um valor ou uma curva para cada item selecionado.",
    channels: { nvapi: "Controles da GPU fornecidos pelo driver da NVIDIA.", "nvapi-drs": "Configurações de perfil do driver da NVIDIA.", nvml: "Interface de gerenciamento de dispositivos da NVIDIA.",
      "oem-ec": "Controle pelo controlador do fabricante do computador; os limites dependem do firmware do modelo.", "amd-smu": "Controlador de energia e desempenho do processador AMD.",
      "fan-core": "Ajuste de rotação pelo componente de controle de ventoinhas.", igcl: "Interface de controle gráfico da Intel.", "intel-msr": "Registradores de controle do processador Intel.", adlx: "Interface de controle do driver gráfico da AMD." }
  },
  "pt-PT": {
    guide: "Selecione o modo de gestão e as definições e clique em Aplicar no dispositivo. Guardar um perfil ou uma curva não os aplica. Ao sair desta página, as definições não aplicadas serão descartadas.",
    channelsTitle: "Canais de escrita", presentDevice: "O dispositivo está ligado. Desligue-o antes de eliminar o respetivo registo.",
    unknownControl: "Controlo sem nome", unknownChannel: "Outro canal", selectSetting: "Selecione um item e preencha as respetivas definições antes de aplicar.", incompleteSetting: "Introduza um valor ou uma curva para cada item selecionado.",
    channels: { nvapi: "Controlos da GPU disponibilizados pelo controlador da NVIDIA.", "nvapi-drs": "Definições de perfil do controlador da NVIDIA.", nvml: "Interface de gestão de dispositivos da NVIDIA.",
      "oem-ec": "Controlo através do controlador do fabricante do computador; os limites dependem do firmware do modelo.", "amd-smu": "Controlador de energia e desempenho do processador AMD.",
      "fan-core": "Ajuste da rotação através do componente de controlo das ventoinhas.", igcl: "Interface de controlo gráfico da Intel.", "intel-msr": "Registos de controlo do processador Intel.", adlx: "Interface de controlo do controlador gráfico da AMD." }
  },
  "uk-UA": {
    guide: "Виберіть спосіб керування та налаштування, а потім натисніть «Застосувати» для пристрою. Збереження профілю або кривої не застосовує їх. Після виходу з цієї сторінки незастосовані налаштування буде відкинуто.",
    channelsTitle: "Канали запису", presentDevice: "Пристрій підключено. Відключіть його перед видаленням запису.",
    unknownControl: "Налаштування без назви", unknownChannel: "Інший канал", selectSetting: "Виберіть пункт і введіть його налаштування перед застосуванням.", incompleteSetting: "Введіть значення або криву для кожного вибраного пункту.",
    channels: { nvapi: "Засоби керування графічним процесором, які надає драйвер NVIDIA.", "nvapi-drs": "Налаштування профілів драйвера NVIDIA.", nvml: "Інтерфейс керування пристроями NVIDIA.",
      "oem-ec": "Керування через контролер виробника комп’ютера; обмеження залежать від мікропрограми моделі.", "amd-smu": "Контролер живлення та продуктивності процесора AMD.",
      "fan-core": "Регулювання швидкості обертання через компонент керування вентиляторами.", igcl: "Інтерфейс керування графікою Intel.", "intel-msr": "Регістри керування процесором Intel.", adlx: "Інтерфейс керування графічного драйвера AMD." }
  },
  "pl-PL": {
    guide: "Wybierz tryb zarządzania i ustawienia, a następnie kliknij Zastosuj przy urządzeniu. Zapisanie profilu lub krzywej nie powoduje ich zastosowania. Opuszczenie tej strony powoduje odrzucenie niezastosowanych ustawień.",
    channelsTitle: "Kanały zapisu", presentDevice: "Urządzenie jest podłączone. Odłącz je przed usunięciem jego wpisu.",
    unknownControl: "Ustawienie bez nazwy", unknownChannel: "Inny kanał", selectSetting: "Wybierz pozycję i wprowadź jej ustawienia przed zastosowaniem.", incompleteSetting: "Wprowadź wartość lub krzywą dla każdej wybranej pozycji.",
    channels: { nvapi: "Sterowanie procesorem graficznym za pośrednictwem sterownika NVIDIA.", "nvapi-drs": "Ustawienia profili sterownika NVIDIA.", nvml: "Interfejs zarządzania urządzeniami NVIDIA.",
      "oem-ec": "Sterowanie przez kontroler producenta komputera; ograniczenia zależą od oprogramowania układowego danego modelu.", "amd-smu": "Kontroler zasilania i wydajności procesora AMD.",
      "fan-core": "Regulacja prędkości obrotowej przez komponent sterowania wentylatorami.", igcl: "Interfejs sterowania układem graficznym Intel.", "intel-msr": "Rejestry sterujące procesora Intel.", adlx: "Interfejs sterowania sterownika graficznego AMD." }
  },
  "tr-TR": {
    guide: "Yönetim modunu ve ayarları seçin, ardından cihaz için Uygula düğmesine tıklayın. Bir profili veya eğriyi kaydetmek, onu uygulamaz. Bu sayfadan ayrıldığınızda uygulanmamış ayarlar silinir.",
    channelsTitle: "Yazma kanalları", presentDevice: "Cihaz bağlı. Kaydını silmeden önce cihazın bağlantısını kesin.",
    unknownControl: "Adsız denetim", unknownChannel: "Diğer kanal", selectSetting: "Uygulamadan önce bir öğe seçin ve ayarlarını girin.", incompleteSetting: "Seçilen her öğe için bir değer veya eğri girin.",
    channels: { nvapi: "NVIDIA sürücüsünün sağladığı GPU denetimleri.", "nvapi-drs": "NVIDIA sürücüsünün profil ayarları.", nvml: "NVIDIA cihaz yönetimi arayüzü.",
      "oem-ec": "Bilgisayar üreticisinin denetleyicisi üzerinden denetim; sınırlar modelin ürün yazılımına bağlıdır.", "amd-smu": "AMD işlemcisinin güç ve performans denetleyicisi.",
      "fan-core": "Fan denetim bileşeni üzerinden fan hızı ayarı.", igcl: "Intel grafik denetim arayüzü.", "intel-msr": "Intel işlemcisinin denetim yazmaçları.", adlx: "AMD grafik sürücüsünün denetim arayüzü." }
  },
  "it-IT": {
    guide: "Selezionare la modalità di gestione e le impostazioni, quindi fare clic su Applica per il dispositivo. Il salvataggio di un profilo o di una curva non ne comporta l’applicazione. Uscendo da questa pagina, le impostazioni non applicate vengono scartate.",
    channelsTitle: "Canali di scrittura", presentDevice: "Il dispositivo è collegato. Scollegarlo prima di eliminarne la registrazione.",
    unknownControl: "Controllo senza nome", unknownChannel: "Altro canale", selectSetting: "Selezionare una voce e specificarne le impostazioni prima di applicarle.", incompleteSetting: "Inserire un valore o una curva per ogni voce selezionata.",
    channels: { nvapi: "Controlli della GPU forniti dal driver NVIDIA.", "nvapi-drs": "Impostazioni dei profili del driver NVIDIA.", nvml: "Interfaccia di gestione dei dispositivi NVIDIA.",
      "oem-ec": "Controllo tramite il controller del produttore del computer; i limiti dipendono dal firmware del modello.", "amd-smu": "Controller di alimentazione e prestazioni del processore AMD.",
      "fan-core": "Regolazione della velocità tramite il componente di controllo delle ventole.", igcl: "Interfaccia di controllo della grafica Intel.", "intel-msr": "Registri di controllo del processore Intel.", adlx: "Interfaccia di controllo del driver grafico AMD." }
  },
  "nl-NL": {
    guide: "Selecteer de beheermodus en instellingen en klik vervolgens bij het apparaat op Toepassen. Een profiel of curve opslaan past deze niet toe. Bij het verlaten van deze pagina worden niet-toegepaste instellingen verwijderd.",
    channelsTitle: "Schrijfkanalen", presentDevice: "Dit apparaat is aangesloten. Koppel het los voordat u de registratie verwijdert.",
    unknownControl: "Naamloze instelling", unknownChannel: "Ander kanaal", selectSetting: "Selecteer een item en voer de instellingen in voordat u deze toepast.", incompleteSetting: "Voer voor elk geselecteerd item een waarde of curve in.",
    channels: { nvapi: "GPU-instellingen via het NVIDIA-stuurprogramma.", "nvapi-drs": "Profielinstellingen van het NVIDIA-stuurprogramma.", nvml: "Interface voor NVIDIA-apparaatbeheer.",
      "oem-ec": "Besturing via de controller van de computerfabrikant; de grenzen hangen af van de firmware van het model.", "amd-smu": "Controller voor energieverbruik en prestaties van de AMD-processor.",
      "fan-core": "Regeling van het toerental via het onderdeel voor ventilatorbesturing.", igcl: "Interface voor Intel-grafische besturing.", "intel-msr": "Besturingsregisters van de Intel-processor.", adlx: "Besturingsinterface van het AMD-grafische stuurprogramma." }
  },
  "sv-SE": {
    guide: "Välj hanteringsläge och inställningar och klicka sedan på Verkställ för enheten. När en profil eller kurva sparas verkställs den inte. När du lämnar sidan förkastas inställningar som inte har verkställts.",
    channelsTitle: "Skrivkanaler", presentDevice: "Enheten är ansluten. Koppla från den innan du tar bort dess post.",
    unknownControl: "Namnlös inställning", unknownChannel: "Annan kanal", selectSetting: "Välj en post och ange dess inställningar innan du verkställer dem.", incompleteSetting: "Ange ett värde eller en kurva för varje vald post.",
    channels: { nvapi: "GPU-styrning via NVIDIA-drivrutinen.", "nvapi-drs": "Profilinställningar i NVIDIA-drivrutinen.", nvml: "Gränssnitt för hantering av NVIDIA-enheter.",
      "oem-ec": "Styrning via datortillverkarens styrenhet; gränserna beror på modellens inbyggda programvara.", "amd-smu": "Styrenhet för AMD-processorns strömförsörjning och prestanda.",
      "fan-core": "Reglering av fläkthastighet via fläktstyrningskomponenten.", igcl: "Gränssnitt för Intel-grafikstyrning.", "intel-msr": "Styrregister i Intel-processorn.", adlx: "Styrgränssnitt i AMD-grafikdrivrutinen." }
  },
  "fi-FI": {
    guide: "Valitse hallintatapa ja asetukset ja napsauta sitten laitteen Käytä-painiketta. Profiilin tai käyrän tallentaminen ei ota sitä käyttöön. Kun poistut tältä sivulta, käyttöön ottamattomat asetukset hylätään.",
    channelsTitle: "Kirjoituskanavat", presentDevice: "Laite on yhdistetty. Irrota se ennen sen tietueen poistamista.",
    unknownControl: "Nimetön asetus", unknownChannel: "Muu kanava", selectSetting: "Valitse kohde ja anna sen asetukset ennen käyttöönottoa.", incompleteSetting: "Anna arvo tai käyrä jokaiselle valitulle kohteelle.",
    channels: { nvapi: "NVIDIA-ajurin tarjoamat grafiikkasuorittimen ohjaustoiminnot.", "nvapi-drs": "NVIDIA-ajurin profiiliasetukset.", nvml: "NVIDIA-laitteiden hallintarajapinta.",
      "oem-ec": "Ohjaus tietokoneen valmistajan ohjaimen kautta; rajat riippuvat mallin laiteohjelmistosta.", "amd-smu": "AMD-suorittimen virran- ja suorituskyvynhallintaohjain.",
      "fan-core": "Tuulettimen nopeudensäätö tuuletinohjauskomponentin kautta.", igcl: "Intel-grafiikan ohjausrajapinta.", "intel-msr": "Intel-suorittimen ohjausrekisterit.", adlx: "AMD-grafiikka-ajurin ohjausrajapinta." }
  },
  "da-DK": {
    guide: "Vælg administrationsform og indstillinger, og klik derefter på Anvend for enheden. Når en profil eller kurve gemmes, bliver den ikke anvendt. Når siden forlades, kasseres indstillinger, der ikke er anvendt.",
    channelsTitle: "Skrivekanaler", presentDevice: "Enheden er tilsluttet. Afbryd forbindelsen, før dens registrering slettes.",
    unknownControl: "Unavngiven indstilling", unknownChannel: "Anden kanal", selectSetting: "Vælg et element, og angiv dets indstillinger, før de anvendes.", incompleteSetting: "Angiv en værdi eller en kurve for hvert valgt element.",
    channels: { nvapi: "GPU-styring via NVIDIA-driveren.", "nvapi-drs": "Profilindstillinger i NVIDIA-driveren.", nvml: "Grænseflade til administration af NVIDIA-enheder.",
      "oem-ec": "Styring via computerproducentens controller; grænserne afhænger af modellens firmware.", "amd-smu": "Controller til strøm og ydeevne for AMD-processoren.",
      "fan-core": "Regulering af blæserhastighed via komponenten til blæserstyring.", igcl: "Grænseflade til Intel-grafikstyring.", "intel-msr": "Styreregistre i Intel-processoren.", adlx: "Styringsgrænseflade i AMD-grafikdriveren." }
  },
  "nb-NO": {
    guide: "Velg administrasjonsmåte og innstillinger, og klikk deretter på Bruk for enheten. Lagring av en profil eller kurve tar den ikke i bruk. Når siden forlates, forkastes innstillinger som ikke er tatt i bruk.",
    channelsTitle: "Skrivekanaler", presentDevice: "Enheten er tilkoblet. Koble den fra før oppføringen slettes.",
    unknownControl: "Innstilling uten navn", unknownChannel: "Annen kanal", selectSetting: "Velg et element og angi innstillingene før de tas i bruk.", incompleteSetting: "Angi en verdi eller en kurve for hvert valgt element.",
    channels: { nvapi: "GPU-styring via NVIDIA-driveren.", "nvapi-drs": "Profilinnstillinger i NVIDIA-driveren.", nvml: "Grensesnitt for administrasjon av NVIDIA-enheter.",
      "oem-ec": "Styring via datamaskinprodusentens kontroller; grensene avhenger av modellens fastvare.", "amd-smu": "Kontroller for strøm og ytelse i AMD-prosessoren.",
      "fan-core": "Regulering av viftehastighet via komponenten for viftestyring.", igcl: "Grensesnitt for Intel-grafikkstyring.", "intel-msr": "Styreregistre i Intel-prosessoren.", adlx: "Styringsgrensesnitt i AMD-grafikkdriveren." }
  },
  "cs-CZ": {
    guide: "Vyberte režim správy a nastavení a poté u zařízení klikněte na Použít. Uložení profilu nebo křivky je nepoužije. Při opuštění této stránky budou nepoužitá nastavení zahozena.",
    channelsTitle: "Kanály zápisu", presentDevice: "Zařízení je připojeno. Před odstraněním jeho záznamu je odpojte.",
    unknownControl: "Nepojmenované nastavení", unknownChannel: "Jiný kanál", selectSetting: "Před použitím vyberte položku a zadejte její nastavení.", incompleteSetting: "Pro každou vybranou položku zadejte hodnotu nebo křivku.",
    channels: { nvapi: "Ovládání grafického procesoru prostřednictvím ovladače NVIDIA.", "nvapi-drs": "Nastavení profilů ovladače NVIDIA.", nvml: "Rozhraní pro správu zařízení NVIDIA.",
      "oem-ec": "Ovládání prostřednictvím řadiče výrobce počítače; omezení závisejí na firmwaru daného modelu.", "amd-smu": "Řadič napájení a výkonu procesoru AMD.",
      "fan-core": "Regulace otáček prostřednictvím komponenty pro ovládání ventilátorů.", igcl: "Rozhraní pro ovládání grafiky Intel.", "intel-msr": "Řídicí registry procesoru Intel.", adlx: "Řídicí rozhraní grafického ovladače AMD." }
  },
  "hu-HU": {
    guide: "Válassza ki a kezelési módot és a beállításokat, majd kattintson az eszköz Alkalmaz gombjára. A profil vagy görbe mentése nem alkalmazza azt. Az oldal elhagyásakor a nem alkalmazott beállítások elvesznek.",
    channelsTitle: "Írási csatornák", presentDevice: "Az eszköz csatlakoztatva van. A bejegyzés törlése előtt válassza le.",
    unknownControl: "Névtelen vezérlő", unknownChannel: "Egyéb csatorna", selectSetting: "Alkalmazás előtt válasszon egy elemet, és adja meg a beállításait.", incompleteSetting: "Adjon meg egy értéket vagy görbét minden kiválasztott elemhez.",
    channels: { nvapi: "Az NVIDIA illesztőprogram által biztosított GPU-vezérlés.", "nvapi-drs": "Az NVIDIA illesztőprogram profilbeállításai.", nvml: "NVIDIA eszközkezelési felület.",
      "oem-ec": "Vezérlés a számítógép gyártójának vezérlőjén keresztül; a korlátokat a modell firmware-e határozza meg.", "amd-smu": "Az AMD processzor energiaellátási és teljesítményvezérlője.",
      "fan-core": "Fordulatszám-szabályozás a ventilátorvezérlő összetevőn keresztül.", igcl: "Intel grafikus vezérlőfelület.", "intel-msr": "Az Intel processzor vezérlőregiszterei.", adlx: "Az AMD grafikus illesztőprogram vezérlőfelülete." }
  },
  "ro-RO": {
    guide: "Selectați modul de gestionare și setările, apoi apăsați Aplicare pentru dispozitiv. Salvarea unui profil sau a unei curbe nu le aplică. La părăsirea acestei pagini, setările neaplicate sunt abandonate.",
    channelsTitle: "Canale de scriere", presentDevice: "Dispozitivul este conectat. Deconectați-l înainte de a-i șterge înregistrarea.",
    unknownControl: "Control fără nume", unknownChannel: "Alt canal", selectSetting: "Selectați un element și introduceți setările acestuia înainte de aplicare.", incompleteSetting: "Introduceți o valoare sau o curbă pentru fiecare element selectat.",
    channels: { nvapi: "Comenzi pentru GPU furnizate de driverul NVIDIA.", "nvapi-drs": "Setările profilurilor driverului NVIDIA.", nvml: "Interfața de gestionare a dispozitivelor NVIDIA.",
      "oem-ec": "Control prin controlerul producătorului computerului; limitele depind de firmware-ul modelului.", "amd-smu": "Controlerul de alimentare și performanță al procesorului AMD.",
      "fan-core": "Reglarea turației prin componenta de control al ventilatoarelor.", igcl: "Interfața de control al graficii Intel.", "intel-msr": "Registrele de control ale procesorului Intel.", adlx: "Interfața de control a driverului grafic AMD." }
  },
  "el-GR": {
    guide: "Επιλέξτε τρόπο διαχείρισης και ρυθμίσεις και, στη συνέχεια, πατήστε Εφαρμογή για τη συσκευή. Η αποθήκευση ενός προφίλ ή μιας καμπύλης δεν τα εφαρμόζει. Όταν φύγετε από αυτή τη σελίδα, οι ρυθμίσεις που δεν έχουν εφαρμοστεί απορρίπτονται.",
    channelsTitle: "Κανάλια εγγραφής", presentDevice: "Η συσκευή είναι συνδεδεμένη. Αποσυνδέστε την πριν διαγράψετε την καταχώρισή της.",
    unknownControl: "Ρύθμιση χωρίς όνομα", unknownChannel: "Άλλο κανάλι", selectSetting: "Επιλέξτε ένα στοιχείο και συμπληρώστε τις ρυθμίσεις του πριν από την εφαρμογή.", incompleteSetting: "Εισαγάγετε μια τιμή ή καμπύλη για κάθε επιλεγμένο στοιχείο.",
    channels: { nvapi: "Στοιχεία ελέγχου GPU που παρέχονται από το πρόγραμμα οδήγησης NVIDIA.", "nvapi-drs": "Ρυθμίσεις προφίλ του προγράμματος οδήγησης NVIDIA.", nvml: "Διεπαφή διαχείρισης συσκευών NVIDIA.",
      "oem-ec": "Έλεγχος μέσω του ελεγκτή του κατασκευαστή του υπολογιστή· τα όρια εξαρτώνται από το υλικολογισμικό του μοντέλου.", "amd-smu": "Ελεγκτής τροφοδοσίας και απόδοσης του επεξεργαστή AMD.",
      "fan-core": "Ρύθμιση ταχύτητας μέσω του στοιχείου ελέγχου ανεμιστήρων.", igcl: "Διεπαφή ελέγχου γραφικών Intel.", "intel-msr": "Καταχωρητές ελέγχου του επεξεργαστή Intel.", adlx: "Διεπαφή ελέγχου του προγράμματος οδήγησης γραφικών AMD." }
  },
  "he-IL": {
    guide: "יש לבחור את מצב הניהול ואת ההגדרות, ולאחר מכן ללחוץ על החלה עבור ההתקן. שמירת פרופיל או עקומה אינה מחילה אותם. בעת יציאה מדף זה, הגדרות שלא הוחלו יבוטלו.",
    channelsTitle: "ערוצי כתיבה", presentDevice: "ההתקן מחובר. יש לנתק אותו לפני מחיקת הרשומה שלו.",
    unknownControl: "פקד ללא שם", unknownChannel: "ערוץ אחר", selectSetting: "יש לבחור פריט ולהזין את הגדרותיו לפני ההחלה.", incompleteSetting: "יש להזין ערך או עקומה לכל פריט שנבחר.",
    channels: { nvapi: "פקדי מעבד גרפי שמספק מנהל ההתקן של NVIDIA.", "nvapi-drs": "הגדרות פרופיל של מנהל ההתקן של NVIDIA.", nvml: "ממשק לניהול התקני NVIDIA.",
      "oem-ec": "שליטה באמצעות הבקר של יצרן המחשב; המגבלות תלויות בקושחה של הדגם.", "amd-smu": "בקר צריכת החשמל והביצועים של מעבד AMD.",
      "fan-core": "בקרת מהירות באמצעות רכיב בקרת המאווררים.", igcl: "ממשק בקרת גרפיקה של Intel.", "intel-msr": "אוגרי הבקרה של מעבד Intel.", adlx: "ממשק הבקרה של מנהל ההתקן הגרפי של AMD." }
  },
  "ar-SA": {
    guide: "يُرجى اختيار وضع الإدارة والإعدادات، ثم النقر على «تطبيق» للجهاز. حفظ ملف تعريف أو منحنى لا يطبّقه. عند مغادرة هذه الصفحة، تُهمَل الإعدادات التي لم تُطبَّق.",
    channelsTitle: "قنوات الكتابة", presentDevice: "الجهاز متصل. يُرجى فصله قبل حذف سجله.",
    unknownControl: "عنصر تحكم بلا اسم", unknownChannel: "قناة أخرى", selectSetting: "يُرجى اختيار عنصر وإدخال إعداداته قبل تطبيقها.", incompleteSetting: "يُرجى إدخال قيمة أو منحنى لكل عنصر محدد.",
    channels: { nvapi: "عناصر تحكم في معالج الرسوميات يوفّرها برنامج تشغيل NVIDIA.", "nvapi-drs": "إعدادات ملفات التعريف في برنامج تشغيل NVIDIA.", nvml: "واجهة إدارة أجهزة NVIDIA.",
      "oem-ec": "التحكم عبر وحدة التحكم التابعة للشركة المصنّعة للحاسوب؛ تعتمد الحدود على البرنامج الثابت للطراز.", "amd-smu": "وحدة التحكم في الطاقة والأداء لمعالج AMD.",
      "fan-core": "ضبط سرعة الدوران عبر مكوّن التحكم في المراوح.", igcl: "واجهة التحكم في رسوميات Intel.", "intel-msr": "سجلات التحكم في معالج Intel.", adlx: "واجهة التحكم في برنامج تشغيل رسوميات AMD." }
  },
  "hi-IN": {
    guide: "प्रबंधन मोड और सेटिंग चुनें, फिर उस डिवाइस के लिए लागू करें पर क्लिक करें। प्रोफ़ाइल या वक्र सहेजने से वह लागू नहीं होता। इस पृष्ठ से बाहर जाने पर लागू नहीं की गई सेटिंग हटा दी जाएँगी।",
    channelsTitle: "लेखन चैनल", presentDevice: "डिवाइस कनेक्ट है। उसका रिकॉर्ड हटाने से पहले उसे डिस्कनेक्ट करें।",
    unknownControl: "बिना नाम का नियंत्रण", unknownChannel: "अन्य चैनल", selectSetting: "लागू करने से पहले कोई मद चुनें और उसकी सेटिंग दर्ज करें।", incompleteSetting: "चुनी गई प्रत्येक मद के लिए मान या वक्र दर्ज करें।",
    channels: { nvapi: "NVIDIA ड्राइवर द्वारा प्रदान किए गए GPU नियंत्रण।", "nvapi-drs": "NVIDIA ड्राइवर की प्रोफ़ाइल सेटिंग।", nvml: "NVIDIA डिवाइस प्रबंधन इंटरफ़ेस।",
      "oem-ec": "कंप्यूटर निर्माता के नियंत्रक के माध्यम से नियंत्रण; सीमाएँ मॉडल के फ़र्मवेयर पर निर्भर करती हैं।", "amd-smu": "AMD प्रोसेसर का ऊर्जा और प्रदर्शन नियंत्रक।",
      "fan-core": "पंखा नियंत्रण घटक के माध्यम से घूर्णन गति का समायोजन।", igcl: "Intel ग्राफ़िक्स नियंत्रण इंटरफ़ेस।", "intel-msr": "Intel प्रोसेसर के नियंत्रण रजिस्टर।", adlx: "AMD ग्राफ़िक्स ड्राइवर का नियंत्रण इंटरफ़ेस।" }
  },
  "id-ID": {
    guide: "Pilih mode pengelolaan dan pengaturan, lalu klik Terapkan pada perangkat. Menyimpan profil atau kurva tidak menerapkannya. Saat halaman ini ditinggalkan, pengaturan yang belum diterapkan akan dibuang.",
    channelsTitle: "Kanal penulisan", presentDevice: "Perangkat masih terhubung. Putuskan sambungannya sebelum menghapus catatannya.",
    unknownControl: "Kontrol tanpa nama", unknownChannel: "Kanal lain", selectSetting: "Pilih item dan isi pengaturannya sebelum menerapkannya.", incompleteSetting: "Masukkan nilai atau kurva untuk setiap item yang dipilih.",
    channels: { nvapi: "Kontrol GPU yang disediakan oleh pengandar NVIDIA.", "nvapi-drs": "Pengaturan profil pengandar NVIDIA.", nvml: "Antarmuka pengelolaan perangkat NVIDIA.",
      "oem-ec": "Kontrol melalui pengendali produsen komputer; batasnya bergantung pada firmware model tersebut.", "amd-smu": "Pengendali daya dan kinerja prosesor AMD.",
      "fan-core": "Pengaturan kecepatan putaran melalui komponen kontrol kipas.", igcl: "Antarmuka kontrol grafis Intel.", "intel-msr": "Register kontrol prosesor Intel.", adlx: "Antarmuka kontrol pengandar grafis AMD." }
  },
  "vi-VN": {
    guide: "Chọn chế độ quản lý và các thiết lập, sau đó nhấn Áp dụng cho thiết bị. Việc lưu cấu hình hoặc đường cong không áp dụng chúng. Khi rời khỏi trang này, các thiết lập chưa áp dụng sẽ bị hủy bỏ.",
    channelsTitle: "Kênh ghi", presentDevice: "Thiết bị đang kết nối. Hãy ngắt kết nối trước khi xóa bản ghi của thiết bị.",
    unknownControl: "Mục điều khiển chưa đặt tên", unknownChannel: "Kênh khác", selectSetting: "Chọn một mục và nhập các thiết lập trước khi áp dụng.", incompleteSetting: "Nhập giá trị hoặc đường cong cho từng mục đã chọn.",
    channels: { nvapi: "Các chức năng điều khiển GPU do trình điều khiển NVIDIA cung cấp.", "nvapi-drs": "Thiết lập cấu hình của trình điều khiển NVIDIA.", nvml: "Giao diện quản lý thiết bị NVIDIA.",
      "oem-ec": "Điều khiển qua bộ điều khiển của nhà sản xuất máy tính; giới hạn phụ thuộc vào phần sụn của mẫu máy.", "amd-smu": "Bộ điều khiển nguồn điện và hiệu năng của bộ xử lý AMD.",
      "fan-core": "Điều chỉnh tốc độ quay qua thành phần điều khiển quạt.", igcl: "Giao diện điều khiển đồ họa Intel.", "intel-msr": "Các thanh ghi điều khiển của bộ xử lý Intel.", adlx: "Giao diện điều khiển của trình điều khiển đồ họa AMD." }
  },
  "th-TH": {
    guide: "เลือกโหมดการจัดการและการตั้งค่า แล้วคลิกนำไปใช้สำหรับอุปกรณ์นั้น การบันทึกโปรไฟล์หรือเส้นโค้งจะยังไม่นำไปใช้ เมื่อออกจากหน้านี้ การตั้งค่าที่ยังไม่ได้นำไปใช้จะถูกละทิ้ง",
    channelsTitle: "ช่องทางการเขียน", presentDevice: "อุปกรณ์ยังเชื่อมต่ออยู่ กรุณายกเลิกการเชื่อมต่อก่อนลบระเบียนของอุปกรณ์",
    unknownControl: "รายการควบคุมที่ไม่มีชื่อ", unknownChannel: "ช่องทางอื่น", selectSetting: "กรุณาเลือกรายการและระบุการตั้งค่าก่อนนำไปใช้", incompleteSetting: "กรุณาระบุค่าหรือเส้นโค้งสำหรับแต่ละรายการที่เลือก",
    channels: { nvapi: "ส่วนควบคุม GPU ที่ไดรเวอร์ NVIDIA จัดเตรียมให้", "nvapi-drs": "การตั้งค่าโปรไฟล์ของไดรเวอร์ NVIDIA", nvml: "อินเทอร์เฟซการจัดการอุปกรณ์ NVIDIA",
      "oem-ec": "ควบคุมผ่านตัวควบคุมของผู้ผลิตคอมพิวเตอร์ โดยขีดจำกัดขึ้นอยู่กับเฟิร์มแวร์ของรุ่นนั้น", "amd-smu": "ตัวควบคุมพลังงานและประสิทธิภาพของหน่วยประมวลผล AMD",
      "fan-core": "ปรับความเร็วรอบผ่านองค์ประกอบควบคุมพัดลม", igcl: "อินเทอร์เฟซควบคุมกราฟิก Intel", "intel-msr": "รีจิสเตอร์ควบคุมของหน่วยประมวลผล Intel", adlx: "อินเทอร์เฟซควบคุมของไดรเวอร์กราฟิก AMD" }
  },
  "ru-RU": {
    guide: "Выберите способ управления и настройки, затем нажмите «Применить» для устройства. Сохранение профиля или кривой не применяет их. При выходе со страницы неприменённые настройки сбрасываются.",
    channelsTitle: "Каналы записи", presentDevice: "Устройство подключено. Отключите его перед удалением записи.",
    unknownControl: "Настройка без названия", unknownChannel: "Другой канал", selectSetting: "Выберите настройку и введите её значения перед применением.", incompleteSetting: "Введите значение или кривую для каждой выбранной настройки.",
    channels: { nvapi: "Управление GPU через драйвер NVIDIA.", "nvapi-drs": "Настройки профилей драйвера NVIDIA.", nvml: "Интерфейс управления устройствами NVIDIA.",
      "oem-ec": "Управление через контроллер производителя компьютера; ограничения зависят от прошивки модели.", "amd-smu": "Контроллер питания и производительности процессора AMD.",
      "fan-core": "Управление оборотами через компонент управления вентиляторами.", igcl: "Интерфейс управления графикой Intel.", "intel-msr": "Регистры управления процессором Intel.", adlx: "Интерфейс управления графическим драйвером AMD." }
  }
};
