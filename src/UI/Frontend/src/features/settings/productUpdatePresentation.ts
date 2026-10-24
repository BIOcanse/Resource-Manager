import { resolveLanguageMode } from "../../i18n/settingsLanguages.ts";

export interface ProductUpdateState {
  stage: string;
  targetVersion?: string | null;
  errorCode?: string | null;
  errorParameters?: Record<string, string> | null;
  lastCheckedAt?: string | null;
  checkResult?: "noUpdate" | "available" | "failed" | "checking" | "checked" | null;
  failedVersion?: string | null;
}

const languages = ["zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES", "ru-RU"] as const;
const copy = {
  interval: ["检查间隔（小时）", "檢查間隔（小時）", "Check interval (hours)", "確認間隔（時間）", "확인 간격(시간)", "Intervalle de vérification (heures)", "Prüfintervall (Stunden)", "Intervalo de búsqueda (horas)", "Интервал проверки (часы)"],
  apply: ["立即应用", "立即套用", "Apply now", "今すぐ適用", "지금 적용", "Appliquer maintenant", "Jetzt anwenden", "Aplicar ahora", "Применить сейчас"],
  pending: ["更新包已准备好；关闭所有桌面会话中的资源管理器界面后自动应用，或点击立即应用。", "更新套件已準備好；關閉所有桌面工作階段中的資源管理器介面後自動套用，或點選立即套用。", "Update package ready. Close Resource Manager windows in all desktop sessions for automatic application, or choose Apply now.", "更新パッケージの準備ができました。すべてのデスクトップセッションで Resource Manager の画面を閉じるか、今すぐ適用を選択してください。", "업데이트 패키지가 준비되었습니다. 모든 데스크톱 세션에서 Resource Manager 창을 닫거나 지금 적용을 선택하세요.", "Le paquet est prêt. Fermez les fenêtres Resource Manager dans toutes les sessions de bureau ou choisissez Appliquer maintenant.", "Das Update-Paket ist bereit. Schließen Sie die Resource-Manager-Fenster in allen Desktop-Sitzungen oder wählen Sie Jetzt anwenden.", "El paquete está listo. Cierre las ventanas de Resource Manager en todas las sesiones de escritorio o seleccione Aplicar ahora.", "Пакет готов. Закройте окна Resource Manager во всех сеансах рабочего стола или выберите «Применить сейчас»."],
  unavailable: ["更新状态暂不可用，正在重试。", "更新狀態暫不可用，正在重試。", "Update status unavailable; retrying.", "更新状態を取得できません。再試行中です。", "업데이트 상태를 확인할 수 없습니다. 다시 시도 중입니다.", "État de mise à jour indisponible ; nouvelle tentative en cours.", "Update-Status nicht verfügbar; erneuter Versuch läuft.", "Estado de actualización no disponible; reintentando.", "Статус обновления недоступен; повторная попытка."],
  stale: ["无法刷新更新状态；上次状态已过期，正在重试。", "無法重新整理更新狀態；上次狀態已過期，正在重試。", "Unable to refresh update status; the previous status is stale. Retrying.", "更新状態を更新できません。以前の状態は古くなっています。再試行中です。", "업데이트 상태를 새로 고칠 수 없습니다. 이전 상태는 오래되었습니다. 다시 시도 중입니다.", "Actualisation impossible ; l’état précédent est périmé. Nouvelle tentative en cours.", "Update-Status konnte nicht aktualisiert werden; der vorherige Status ist veraltet. Erneuter Versuch läuft.", "No se pudo actualizar el estado; el anterior está desactualizado. Reintentando.", "Не удалось обновить статус; прежний статус устарел. Повторная попытка."],
  noUpdate: ["未发现新版本。", "未發現新版本。", "No new version found.", "新しいバージョンはありません。", "새 버전이 없습니다.", "Aucune nouvelle version trouvée.", "Keine neue Version gefunden.", "No se encontraron versiones nuevas.", "Новая версия не найдена."],
  available: ["发现可用更新。", "發現可用更新。", "An update is available.", "更新が利用可能です。", "업데이트가 있습니다.", "Une mise à jour est disponible.", "Ein Update ist verfügbar.", "Hay una actualización disponible.", "Доступно обновление."],
} satisfies Record<string, readonly string[]>;

// Failure prose belongs to the UI; the service supplies only codes and parameters.
export const updateErrors = {
  "update.versionNotNewer": ["只能更新到比当前安装版本更高的版本。", "只能更新至比目前安裝版本更新的版本。", "Only a version newer than the installed version can be applied.", "インストール済みのバージョンより新しいバージョンにのみ更新できます。", "설치된 버전보다 최신 버전으로만 업데이트할 수 있습니다.", "Seule une version plus récente que la version installée peut être appliquée.", "Es kann nur eine neuere Version als die installierte angewendet werden.", "Solo se puede aplicar una versión posterior a la instalada.", "Можно установить только версию новее установленной."],
  "update.repairVersionMismatch": ["修复包必须与当前安装版本一致。", "修復套件必須與目前安裝版本相符。", "The repair package must match the installed version.", "修復パッケージはインストール済みのバージョンと一致する必要があります。", "복구 패키지는 설치된 버전과 일치해야 합니다.", "Le paquet de réparation doit correspondre à la version installée.", "Das Reparaturpaket muss der installierten Version entsprechen.", "El paquete de reparación debe coincidir con la versión instalada.", "Пакет восстановления должен соответствовать установленной версии."],
  "update.choiceRequired": ["请选择版本。", "請選擇版本。", "Choose a version.", "バージョンを選択してください。", "버전을 선택하세요.", "Choisissez une version.", "Wählen Sie eine Version.", "Seleccione una versión.", "Выберите версию."],
  "update.busy": ["更新正在进行。", "更新正在進行。", "An update is already in progress.", "更新が進行中です。", "업데이트가 진행 중입니다.", "Une mise à jour est déjà en cours.", "Ein Update läuft bereits.", "Ya hay una actualización en curso.", "Обновление уже выполняется."],
  "update.serviceRequired": ["更新需要已安装的服务。", "更新需要已安裝的服務。", "Updating requires the installed service.", "更新にはインストール済みのサービスが必要です。", "업데이트하려면 설치된 서비스가 필요합니다.", "La mise à jour nécessite le service installé.", "Das Update erfordert den installierten Dienst.", "La actualización requiere el servicio instalado.", "Для обновления требуется установленная служба."],
  "update.packageRequired": ["需要更新包。", "需要更新套件。", "An update package is required.", "更新パッケージが必要です。", "업데이트 패키지가 필요합니다.", "Un paquet de mise à jour est requis.", "Ein Update-Paket ist erforderlich.", "Se requiere un paquete de actualización.", "Требуется пакет обновления."],
  "update.targetUnavailable": ["目标版本不可用。", "目標版本不可用。", "The target version is unavailable.", "対象バージョンを利用できません。", "대상 버전을 사용할 수 없습니다.", "La version cible est indisponible.", "Die Zielversion ist nicht verfügbar.", "La versión de destino no está disponible.", "Целевая версия недоступна."],
  "update.catalogUnavailable": ["版本目录不可用。", "版本目錄不可用。", "The version catalog is unavailable.", "バージョン一覧を取得できません。", "버전 목록을 사용할 수 없습니다.", "Le catalogue des versions est indisponible.", "Der Versionskatalog ist nicht verfügbar.", "El catálogo de versiones no está disponible.", "Каталог версий недоступен."],
  "update.installRootUnavailable": ["找不到安装目录。", "找不到安裝目錄。", "The installation directory is unavailable.", "インストール先を利用できません。", "설치 디렉터리를 사용할 수 없습니다.", "Le dossier d’installation est indisponible.", "Das Installationsverzeichnis ist nicht verfügbar.", "El directorio de instalación no está disponible.", "Каталог установки недоступен."],
  "update.versionMismatch": ["更新包版本不匹配。", "更新套件版本不符。", "The package version does not match.", "パッケージのバージョンが一致しません。", "패키지 버전이 일치하지 않습니다.", "La version du paquet ne correspond pas.", "Die Paketversion stimmt nicht überein.", "La versión del paquete no coincide.", "Версия пакета не совпадает."],
  "update.managerUnavailable": ["更新管理器不可用。", "更新管理員不可用。", "The update manager is unavailable.", "更新マネージャーを利用できません。", "업데이트 관리자를 사용할 수 없습니다.", "Le gestionnaire de mise à jour est indisponible.", "Die Update-Verwaltung ist nicht verfügbar.", "El gestor de actualizaciones no está disponible.", "Менеджер обновлений недоступен."],
  "update.managerStartFailed": ["无法启动更新管理器。", "無法啟動更新管理員。", "Unable to start the update manager.", "更新マネージャーを起動できません。", "업데이트 관리자를 시작할 수 없습니다.", "Impossible de démarrer le gestionnaire de mise à jour.", "Die Update-Verwaltung konnte nicht gestartet werden.", "No se pudo iniciar el gestor de actualizaciones.", "Не удалось запустить менеджер обновлений."],
  "update.checksumInvalid": ["校验文件无效。", "校驗檔案無效。", "The checksum file is invalid.", "チェックサムファイルが無効です。", "체크섬 파일이 잘못되었습니다.", "Le fichier de contrôle est invalide.", "Die Prüfsummendatei ist ungültig.", "El archivo de suma de comprobación no es válido.", "Файл контрольной суммы недействителен."],
  "update.packageTooLarge": ["更新包超过大小限制。", "更新套件超過大小限制。", "The update package exceeds the size limit.", "更新パッケージがサイズ制限を超えています。", "업데이트 패키지가 크기 제한을 초과했습니다.", "Le paquet dépasse la taille maximale.", "Das Update-Paket überschreitet die Größenbegrenzung.", "El paquete supera el límite de tamaño.", "Пакет превышает допустимый размер."],
  "update.checksumMismatch": ["更新包校验失败。", "更新套件校驗失敗。", "The package checksum does not match.", "パッケージのチェックサムが一致しません。", "패키지 체크섬이 일치하지 않습니다.", "La somme de contrôle du paquet ne correspond pas.", "Die Paketprüfsumme stimmt nicht überein.", "La suma de comprobación del paquete no coincide.", "Контрольная сумма пакета не совпадает."],
  "update.archiveInvalid": ["更新压缩包无效。", "更新壓縮套件無效。", "The update archive is invalid.", "更新アーカイブが無効です。", "업데이트 압축 파일이 잘못되었습니다.", "L’archive de mise à jour est invalide.", "Das Update-Archiv ist ungültig.", "El archivo de actualización no es válido.", "Архив обновления недействителен."],
  "update.preparationFailed": ["无法准备更新。", "無法準備更新。", "Unable to prepare the update.", "更新を準備できません。", "업데이트를 준비할 수 없습니다.", "Impossible de préparer la mise à jour.", "Das Update konnte nicht vorbereitet werden.", "No se pudo preparar la actualización.", "Не удалось подготовить обновление."],
  "update.applyFailed": ["无法应用更新。", "無法套用更新。", "Unable to apply the update.", "更新を適用できません。", "업데이트를 적용할 수 없습니다.", "Impossible d’appliquer la mise à jour.", "Das Update konnte nicht angewendet werden.", "No se pudo aplicar la actualización.", "Не удалось применить обновление."],
  "update.rolledBack": ["更新失败，已恢复原版本。", "更新失敗，已還原原版本。", "The update failed; the previous version was restored.", "更新に失敗しました。以前のバージョンに復元しました。", "업데이트에 실패하여 이전 버전으로 복원했습니다.", "La mise à jour a échoué ; la version précédente a été restaurée.", "Das Update ist fehlgeschlagen; die vorherige Version wurde wiederhergestellt.", "La actualización falló; se restauró la versión anterior.", "Обновление не удалось; предыдущая версия восстановлена."],
  "update.recoveryRequired": ["更新失败，需要恢复。", "更新失敗，需要復原。", "The update failed; recovery is required.", "更新に失敗しました。復旧が必要です。", "업데이트에 실패했습니다. 복구가 필요합니다.", "La mise à jour a échoué ; une récupération est nécessaire.", "Das Update ist fehlgeschlagen; eine Wiederherstellung ist erforderlich.", "La actualización falló; se requiere recuperación.", "Обновление не удалось; требуется восстановление."],
  "update.statusUnavailable": copy.unavailable,
} satisfies Record<string, readonly string[]>;

export function productUpdateCopy(language: string) {
  const index = languages.indexOf(resolveLanguageMode(language) as typeof languages[number]);
  if (index < 0) throw new Error(`Unsupported update language: ${language}`);
  const result = Object.fromEntries(Object.entries(copy).map(([key, values]) => [key, values[index]])) as Record<keyof typeof copy, string>;
  return {
    ...result,
    error(code: string | null | undefined, parameters?: Record<string, string> | null) {
      const values = updateErrors[code as keyof typeof updateErrors] ?? updateErrors["update.preparationFailed"];
      const message = values[index];
      return parameters?.targetVersion ? `${message} (${parameters.targetVersion})` : message;
    }
  };
}

export function updateFailure(error: unknown, fallback: string): ProductUpdateState {
  const payload = (error as { payload?: { errorCode?: string; errorParameters?: Record<string, string> } } | null)?.payload;
  return { stage: "error", errorCode: payload?.errorCode ?? fallback, errorParameters: payload?.errorParameters };
}

export function isUpdateBusy(stage?: string) {
  return ["checking", "downloading", "verifying", "waitingForExit"].includes(stage ?? "");
}
