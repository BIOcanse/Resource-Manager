// Backend reasons select localized explanations. Internal identifiers are not user-facing text.
export type GpuVfReasonCategory = "unavailable" | "binding" | "range" | "input" | "missing" | "limit" | "inUse" | "defaults" | "restore" | "legacy" | "session";
type Reasons = Record<GpuVfReasonCategory, string>;
export const gpuVfCurveReasons: Record<string, Reasons> = {
  "zh-CN": {
    session: "无法建立曲线编辑会话，请关闭曲线库后重新打开。",
    unavailable: "曲线数据或操作不可用。", binding: "曲线与设备或电压点序列不匹配，请重新读取。", range: "偏移超出后端允许的范围。", input: "曲线请求不符合后端的输入要求。",
    missing: "曲线配置或编辑草稿已不存在，请重新打开。", limit: "已达到曲线库、草稿或名称的限制。", inUse: "曲线正在被引用，无法删除。", defaults: "没有可验证的硬件默认值或基准频率。",
    restore: "尚未验证此设备的曲线恢复方式，暂不能应用。仍可读取、编辑和保存曲线。", legacy: "旧版频率偏移无法迁移为曲线配置。"
  },
  "en-US": {
    session: "Could not establish a curve editing session. Close the curve library and reopen it.",
    unavailable: "Curve data or operation is unavailable.", binding: "The curve does not match the device or voltage-point sequence. Read it again.", range: "The offset exceeds the range allowed by the backend.", input: "The curve request does not meet the backend input requirements.",
    missing: "The curve preset or editing draft no longer exists. Open it again.", limit: "A curve-library, draft, or name limit has been reached.", inUse: "The curve is referenced and cannot be deleted.", defaults: "Verified hardware defaults or reference frequencies are unavailable.",
    restore: "Safe restoration of the hardware curve has not been verified, so Apply is unavailable. Reading, editing and saving curves remain available.", legacy: "The legacy frequency offset could not be migrated to a curve preset."
  },
  "zh-TW": {
    session: "無法建立曲線編輯工作階段，請關閉曲線庫後重新開啟。",
    unavailable: "曲線資料或操作無法使用。", binding: "曲線與裝置或電壓點序列不相符，請重新讀取。", range: "偏移超出後端允許的範圍。", input: "曲線請求不符合後端的輸入要求。",
    missing: "曲線設定或編輯草稿已不存在，請重新開啟。", limit: "已達曲線庫、草稿或名稱的限制。", inUse: "曲線仍被參照，無法刪除。", defaults: "沒有可驗證的硬體預設值或基準頻率。",
    restore: "尚未驗證此裝置的曲線還原方式，暫時無法套用。仍可讀取、編輯及儲存曲線。", legacy: "無法將舊版頻率偏移移轉為曲線設定。"
  },
  "de-DE": {
    session: "Die Kurvenbearbeitung konnte nicht gestartet werden. Schließen Sie die Kurvenbibliothek und öffnen Sie sie erneut.",
    unavailable: "Kurvendaten oder Vorgang nicht verfügbar.", binding: "Die Kurve passt nicht zum Gerät oder zur Spannungspunktfolge. Lesen Sie sie erneut ein.", range: "Der Versatz überschreitet den vom Backend erlaubten Bereich.", input: "Die Kurvenanforderung erfüllt die Eingabeanforderungen des Backends nicht.",
    missing: "Kurvenprofil oder Entwurf nicht mehr vorhanden. Öffnen Sie ihn erneut.", limit: "Eine Grenze für Kurvenbibliothek, Entwürfe oder Namen wurde erreicht.", inUse: "Die Kurve wird referenziert und kann nicht gelöscht werden.", defaults: "Verifizierte Hardware-Standardwerte oder Referenzfrequenzen sind nicht verfügbar.",
    restore: "Die sichere Wiederherstellung der Hardwarekurve ist nicht bestätigt. Anwenden ist daher nicht verfügbar; Lesen, Bearbeiten und Speichern bleiben möglich.", legacy: "Der bisherige Frequenzversatz konnte nicht in ein Kurvenprofil migriert werden."
  },
  "es-ES": {
    session: "No se pudo iniciar una sesión de edición. Cierre la biblioteca de curvas y vuelva a abrirla.",
    unavailable: "Los datos de la curva o la operación no están disponibles.", binding: "La curva no coincide con el dispositivo o la secuencia de tensión. Vuelva a leerla.", range: "El desplazamiento supera el intervalo permitido por el servidor.", input: "La solicitud de curva no cumple los requisitos de entrada del servidor.",
    missing: "La configuración o el borrador ya no existe. Ábralo de nuevo.", limit: "Se ha alcanzado un límite de la biblioteca, los borradores o los nombres.", inUse: "La curva está referenciada y no se puede eliminar.", defaults: "No hay valores predeterminados ni frecuencias de referencia del hardware verificados.",
    restore: "No se ha verificado la restauración segura de la curva del hardware, por lo que no se puede aplicar. Puede leer, editar y guardar curvas.", legacy: "No se pudo migrar el desplazamiento de frecuencia anterior a una configuración de curva."
  },
  "fr-FR": {
    session: "Impossible de démarrer la session de modification. Fermez la bibliothèque de courbes et rouvrez-la.",
    unavailable: "Les données de courbe ou l’opération sont indisponibles.", binding: "La courbe ne correspond pas à l’appareil ou à la séquence de tension. Relisez-la.", range: "Le décalage dépasse la plage autorisée par le serveur.", input: "La demande de courbe ne respecte pas les exigences de saisie du serveur.",
    missing: "Le préréglage ou le brouillon n’existe plus. Ouvrez-le à nouveau.", limit: "Une limite de bibliothèque, de brouillons ou de noms a été atteinte.", inUse: "La courbe est référencée et ne peut pas être supprimée.", defaults: "Aucune valeur matérielle par défaut ni fréquence de référence vérifiée n’est disponible.",
    restore: "La restauration sûre de la courbe matérielle n’a pas été vérifiée ; l’application est indisponible. La lecture, la modification et l’enregistrement restent possibles.", legacy: "L’ancien décalage de fréquence n’a pas pu être migré vers un préréglage de courbe."
  },
  "ja-JP": {
    session: "カーブの編集セッションを開始できません。ライブラリを閉じて開き直してください。",
    unavailable: "カーブのデータまたは操作を利用できません。", binding: "カーブがデバイスまたは電圧点の配列と一致しません。再度読み取ってください。", range: "オフセットがバックエンドの許容範囲を超えています。", input: "カーブの要求がバックエンドの入力要件を満たしていません。",
    missing: "カーブ設定または編集ドラフトは存在しません。再度開いてください。", limit: "カーブライブラリ、ドラフト、または名前の上限に達しました。", inUse: "このカーブは参照されているため削除できません。", defaults: "検証済みのハードウェア既定値または基準周波数がありません。",
    restore: "ハードウェアのカーブを安全に復元できることが未確認のため、適用できません。読み取り・編集・保存は利用できます。", legacy: "従来の周波数オフセットをカーブ設定へ移行できませんでした。"
  },
  "ko-KR": {
    session: "곡선 편집 세션을 시작할 수 없습니다. 곡선 라이브러리를 닫았다가 다시 여십시오.",
    unavailable: "곡선 데이터 또는 작업을 사용할 수 없습니다.", binding: "곡선이 장치 또는 전압 점 배열과 일치하지 않습니다. 다시 읽으십시오.", range: "오프셋이 백엔드의 허용 범위를 초과합니다.", input: "곡선 요청이 백엔드 입력 요구 사항을 충족하지 않습니다.",
    missing: "곡선 설정 또는 편집 초안이 더 이상 존재하지 않습니다. 다시 여십시오.", limit: "곡선 라이브러리, 초안 또는 이름 제한에 도달했습니다.", inUse: "곡선이 참조되고 있어 삭제할 수 없습니다.", defaults: "검증된 하드웨어 기본값 또는 기준 주파수가 없습니다.",
    restore: "하드웨어 곡선의 안전한 복원이 검증되지 않아 적용할 수 없습니다. 곡선 읽기, 편집, 저장은 계속 사용할 수 있습니다.", legacy: "기존 주파수 오프셋을 곡선 설정으로 이전하지 못했습니다."
  },
  "ru-RU": {
    session: "Не удалось начать сеанс редактирования. Закройте библиотеку кривых и откройте её снова.",
    unavailable: "Данные кривой или операция недоступны.", binding: "Кривая не соответствует устройству или последовательности точек напряжения. Выполните чтение заново.", range: "Смещение превышает разрешённый сервером диапазон.", input: "Запрос кривой не соответствует требованиям сервера к входным данным.",
    missing: "Профиль кривой или черновик больше не существует. Откройте его заново.", limit: "Достигнуто ограничение библиотеки кривых, черновиков или названий.", inUse: "На кривую имеются ссылки, поэтому её нельзя удалить.", defaults: "Проверенные аппаратные значения по умолчанию или опорные частоты недоступны.",
    restore: "Безопасное восстановление аппаратной кривой не проверено, поэтому применение недоступно. Чтение, редактирование и сохранение кривых доступны.", legacy: "Не удалось перенести прежнее смещение частоты в профиль кривой."
  }
};
