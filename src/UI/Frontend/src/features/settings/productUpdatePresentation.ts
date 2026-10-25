import { resolveLanguageMode } from "../../i18n/settingsLanguages.ts";

export interface ProductUpdateState {
  stage: string;
  targetVersion?: string | null;
  errorCode?: string | null;
  errorParameters?: Record<string, string> | null;
  lastCheckedAt?: string | null;
  /** 上次检查的结果（noUpdate、available、failed…）；其他值不显示。 */
  checkResult?: string | null;
  /** 自动检查现在处在哪一步；“正在等待联网”看这里，不看 checkResult。 */
  automaticCheckStatus?: ProductAutomaticCheckStatus | null;
  /** 状态更新时间（UTC）。 */
  updatedAt?: string | null;
  failedVersion?: string | null;
  /** 上次成功检查得出的同渠道、严格更高的版本；没有为 null。 */
  availableVersion?: string | null;
  /** 按当前联网状态推算的下次自动检查时间（UTC）；断网或未开间隔检查时为 null。 */
  nextCheckAt?: string | null;
  /** 自上次检查起已累计的联网时长（秒）；间隔只计联网的时间。 */
  onlineTimeSinceLastCheckSeconds?: number | null;
}

export const productAutomaticCheckStatuses = ["disabled", "waitingForInternet", "checking", "scheduled", "idle"] as const;
export type ProductAutomaticCheckStatus = typeof productAutomaticCheckStatuses[number];

// 每条文案按语言键写全九种语言；少写或多写一种，类型检查就报错。
const languages = ["zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES", "ru-RU"] as const;
type UpdateLanguage = typeof languages[number];
type UpdateCopyEntry = Readonly<Record<UpdateLanguage, string>>;
const copy = {
  apply: { "zh-CN": "立即应用", "zh-TW": "立即套用", "en-US": "Apply now", "ja-JP": "今すぐ適用", "ko-KR": "지금 적용", "fr-FR": "Appliquer maintenant", "de-DE": "Jetzt anwenden", "es-ES": "Aplicar ahora", "ru-RU": "Применить сейчас" },
  pending: { "zh-CN": "更新包已准备好；关闭所有桌面会话中的资源管理器界面后自动应用，或点击立即应用。", "zh-TW": "更新套件已準備好；關閉所有桌面工作階段中的資源管理器介面後自動套用，或點選立即套用。", "en-US": "Update package ready. Close Resource Manager windows in all desktop sessions for automatic application, or choose Apply now.", "ja-JP": "更新パッケージの準備ができました。すべてのデスクトップセッションで Resource Manager の画面を閉じるか、今すぐ適用を選択してください。", "ko-KR": "업데이트 패키지가 준비되었습니다. 모든 데스크톱 세션에서 Resource Manager 창을 닫거나 지금 적용을 선택하세요.", "fr-FR": "Le paquet est prêt. Fermez les fenêtres Resource Manager dans toutes les sessions de bureau ou choisissez Appliquer maintenant.", "de-DE": "Das Update-Paket ist bereit. Schließen Sie die Resource-Manager-Fenster in allen Desktop-Sitzungen oder wählen Sie Jetzt anwenden.", "es-ES": "El paquete está listo. Cierre las ventanas de Resource Manager en todas las sesiones de escritorio o seleccione Aplicar ahora.", "ru-RU": "Пакет готов. Закройте окна Resource Manager во всех сеансах рабочего стола или выберите «Применить сейчас»." },
  unavailable: { "zh-CN": "更新状态暂不可用，正在重试。", "zh-TW": "更新狀態暫不可用，正在重試。", "en-US": "Update status unavailable; retrying.", "ja-JP": "更新状態を取得できません。再試行中です。", "ko-KR": "업데이트 상태를 확인할 수 없습니다. 다시 시도 중입니다.", "fr-FR": "État de mise à jour indisponible ; nouvelle tentative en cours.", "de-DE": "Update-Status nicht verfügbar; erneuter Versuch läuft.", "es-ES": "Estado de actualización no disponible; reintentando.", "ru-RU": "Статус обновления недоступен; повторная попытка." },
  stale: { "zh-CN": "无法刷新更新状态；上次状态已过期，正在重试。", "zh-TW": "無法重新整理更新狀態；上次狀態已過期，正在重試。", "en-US": "Unable to refresh update status; the previous status is stale. Retrying.", "ja-JP": "更新状態を更新できません。以前の状態は古くなっています。再試行中です。", "ko-KR": "업데이트 상태를 새로 고칠 수 없습니다. 이전 상태는 오래되었습니다. 다시 시도 중입니다.", "fr-FR": "Actualisation impossible ; l’état précédent est périmé. Nouvelle tentative en cours.", "de-DE": "Update-Status konnte nicht aktualisiert werden; der vorherige Status ist veraltet. Erneuter Versuch läuft.", "es-ES": "No se pudo actualizar el estado; el anterior está desactualizado. Reintentando.", "ru-RU": "Не удалось обновить статус; прежний статус устарел. Повторная попытка." },
  noUpdate: { "zh-CN": "未发现新版本。", "zh-TW": "未發現新版本。", "en-US": "No new version found.", "ja-JP": "新しいバージョンはありません。", "ko-KR": "새 버전이 없습니다.", "fr-FR": "Aucune nouvelle version trouvée.", "de-DE": "Keine neue Version gefunden.", "es-ES": "No se encontraron versiones nuevas.", "ru-RU": "Новая версия не найдена." },
  available: { "zh-CN": "发现可用更新。", "zh-TW": "發現可用更新。", "en-US": "An update is available.", "ja-JP": "更新が利用可能です。", "ko-KR": "업데이트가 있습니다.", "fr-FR": "Une mise à jour est disponible.", "de-DE": "Ein Update ist verfügbar.", "es-ES": "Hay una actualización disponible.", "ru-RU": "Доступно обновление." },
} satisfies Record<string, UpdateCopyEntry>;

// Failure prose belongs to the UI; the service supplies only codes and parameters.
export const updateErrors = {
  "update.versionNotNewer": { "zh-CN": "只能更新到比当前安装版本更高的版本。", "zh-TW": "只能更新至比目前安裝版本更新的版本。", "en-US": "Only a version newer than the installed version can be applied.", "ja-JP": "インストール済みのバージョンより新しいバージョンにのみ更新できます。", "ko-KR": "설치된 버전보다 최신 버전으로만 업데이트할 수 있습니다.", "fr-FR": "Seule une version plus récente que la version installée peut être appliquée.", "de-DE": "Es kann nur eine neuere Version als die installierte angewendet werden.", "es-ES": "Solo se puede aplicar una versión posterior a la instalada.", "ru-RU": "Можно установить только версию новее установленной." },
  "update.repairVersionMismatch": { "zh-CN": "修复包必须与当前安装版本一致。", "zh-TW": "修復套件必須與目前安裝版本相符。", "en-US": "The repair package must match the installed version.", "ja-JP": "修復パッケージはインストール済みのバージョンと一致する必要があります。", "ko-KR": "복구 패키지는 설치된 버전과 일치해야 합니다.", "fr-FR": "Le paquet de réparation doit correspondre à la version installée.", "de-DE": "Das Reparaturpaket muss der installierten Version entsprechen.", "es-ES": "El paquete de reparación debe coincidir con la versión instalada.", "ru-RU": "Пакет восстановления должен соответствовать установленной версии." },
  "update.choiceRequired": { "zh-CN": "请选择版本。", "zh-TW": "請選擇版本。", "en-US": "Choose a version.", "ja-JP": "バージョンを選択してください。", "ko-KR": "버전을 선택하세요.", "fr-FR": "Choisissez une version.", "de-DE": "Wählen Sie eine Version.", "es-ES": "Seleccione una versión.", "ru-RU": "Выберите версию." },
  "update.busy": { "zh-CN": "更新正在进行。", "zh-TW": "更新正在進行。", "en-US": "An update is already in progress.", "ja-JP": "更新が進行中です。", "ko-KR": "업데이트가 진행 중입니다.", "fr-FR": "Une mise à jour est déjà en cours.", "de-DE": "Ein Update läuft bereits.", "es-ES": "Ya hay una actualización en curso.", "ru-RU": "Обновление уже выполняется." },
  "update.serviceRequired": { "zh-CN": "更新需要已安装的服务。", "zh-TW": "更新需要已安裝的服務。", "en-US": "Updating requires the installed service.", "ja-JP": "更新にはインストール済みのサービスが必要です。", "ko-KR": "업데이트하려면 설치된 서비스가 필요합니다.", "fr-FR": "La mise à jour nécessite le service installé.", "de-DE": "Das Update erfordert den installierten Dienst.", "es-ES": "La actualización requiere el servicio instalado.", "ru-RU": "Для обновления требуется установленная служба." },
  "update.packageRequired": { "zh-CN": "需要更新包。", "zh-TW": "需要更新套件。", "en-US": "An update package is required.", "ja-JP": "更新パッケージが必要です。", "ko-KR": "업데이트 패키지가 필요합니다.", "fr-FR": "Un paquet de mise à jour est requis.", "de-DE": "Ein Update-Paket ist erforderlich.", "es-ES": "Se requiere un paquete de actualización.", "ru-RU": "Требуется пакет обновления." },
  "update.targetUnavailable": { "zh-CN": "目标版本不可用。", "zh-TW": "目標版本不可用。", "en-US": "The target version is unavailable.", "ja-JP": "対象バージョンを利用できません。", "ko-KR": "대상 버전을 사용할 수 없습니다.", "fr-FR": "La version cible est indisponible.", "de-DE": "Die Zielversion ist nicht verfügbar.", "es-ES": "La versión de destino no está disponible.", "ru-RU": "Целевая версия недоступна." },
  "update.catalogUnavailable": { "zh-CN": "版本目录不可用。", "zh-TW": "版本目錄不可用。", "en-US": "The version catalog is unavailable.", "ja-JP": "バージョン一覧を取得できません。", "ko-KR": "버전 목록을 사용할 수 없습니다.", "fr-FR": "Le catalogue des versions est indisponible.", "de-DE": "Der Versionskatalog ist nicht verfügbar.", "es-ES": "El catálogo de versiones no está disponible.", "ru-RU": "Каталог версий недоступен." },
  "update.installRootUnavailable": { "zh-CN": "找不到安装目录。", "zh-TW": "找不到安裝目錄。", "en-US": "The installation directory is unavailable.", "ja-JP": "インストール先を利用できません。", "ko-KR": "설치 디렉터리를 사용할 수 없습니다.", "fr-FR": "Le dossier d’installation est indisponible.", "de-DE": "Das Installationsverzeichnis ist nicht verfügbar.", "es-ES": "El directorio de instalación no está disponible.", "ru-RU": "Каталог установки недоступен." },
  "update.versionMismatch": { "zh-CN": "更新包版本不匹配。", "zh-TW": "更新套件版本不符。", "en-US": "The package version does not match.", "ja-JP": "パッケージのバージョンが一致しません。", "ko-KR": "패키지 버전이 일치하지 않습니다.", "fr-FR": "La version du paquet ne correspond pas.", "de-DE": "Die Paketversion stimmt nicht überein.", "es-ES": "La versión del paquete no coincide.", "ru-RU": "Версия пакета не совпадает." },
  "update.managerUnavailable": { "zh-CN": "更新管理器不可用。", "zh-TW": "更新管理員不可用。", "en-US": "The update manager is unavailable.", "ja-JP": "更新マネージャーを利用できません。", "ko-KR": "업데이트 관리자를 사용할 수 없습니다.", "fr-FR": "Le gestionnaire de mise à jour est indisponible.", "de-DE": "Die Update-Verwaltung ist nicht verfügbar.", "es-ES": "El gestor de actualizaciones no está disponible.", "ru-RU": "Менеджер обновлений недоступен." },
  "update.managerStartFailed": { "zh-CN": "无法启动更新管理器。", "zh-TW": "無法啟動更新管理員。", "en-US": "Unable to start the update manager.", "ja-JP": "更新マネージャーを起動できません。", "ko-KR": "업데이트 관리자를 시작할 수 없습니다.", "fr-FR": "Impossible de démarrer le gestionnaire de mise à jour.", "de-DE": "Die Update-Verwaltung konnte nicht gestartet werden.", "es-ES": "No se pudo iniciar el gestor de actualizaciones.", "ru-RU": "Не удалось запустить менеджер обновлений." },
  "update.checksumInvalid": { "zh-CN": "校验文件无效。", "zh-TW": "校驗檔案無效。", "en-US": "The checksum file is invalid.", "ja-JP": "チェックサムファイルが無効です。", "ko-KR": "체크섬 파일이 잘못되었습니다.", "fr-FR": "Le fichier de contrôle est invalide.", "de-DE": "Die Prüfsummendatei ist ungültig.", "es-ES": "El archivo de suma de comprobación no es válido.", "ru-RU": "Файл контрольной суммы недействителен." },
  "update.packageTooLarge": { "zh-CN": "更新包超过大小限制。", "zh-TW": "更新套件超過大小限制。", "en-US": "The update package exceeds the size limit.", "ja-JP": "更新パッケージがサイズ制限を超えています。", "ko-KR": "업데이트 패키지가 크기 제한을 초과했습니다.", "fr-FR": "Le paquet dépasse la taille maximale.", "de-DE": "Das Update-Paket überschreitet die Größenbegrenzung.", "es-ES": "El paquete supera el límite de tamaño.", "ru-RU": "Пакет превышает допустимый размер." },
  "update.checksumMismatch": { "zh-CN": "更新包校验失败。", "zh-TW": "更新套件校驗失敗。", "en-US": "The package checksum does not match.", "ja-JP": "パッケージのチェックサムが一致しません。", "ko-KR": "패키지 체크섬이 일치하지 않습니다.", "fr-FR": "La somme de contrôle du paquet ne correspond pas.", "de-DE": "Die Paketprüfsumme stimmt nicht überein.", "es-ES": "La suma de comprobación del paquete no coincide.", "ru-RU": "Контрольная сумма пакета не совпадает." },
  "update.archiveInvalid": { "zh-CN": "更新压缩包无效。", "zh-TW": "更新壓縮套件無效。", "en-US": "The update archive is invalid.", "ja-JP": "更新アーカイブが無効です。", "ko-KR": "업데이트 압축 파일이 잘못되었습니다.", "fr-FR": "L’archive de mise à jour est invalide.", "de-DE": "Das Update-Archiv ist ungültig.", "es-ES": "El archivo de actualización no es válido.", "ru-RU": "Архив обновления недействителен." },
  "update.preparationFailed": { "zh-CN": "无法准备更新。", "zh-TW": "無法準備更新。", "en-US": "Unable to prepare the update.", "ja-JP": "更新を準備できません。", "ko-KR": "업데이트를 준비할 수 없습니다.", "fr-FR": "Impossible de préparer la mise à jour.", "de-DE": "Das Update konnte nicht vorbereitet werden.", "es-ES": "No se pudo preparar la actualización.", "ru-RU": "Не удалось подготовить обновление." },
  "update.applyFailed": { "zh-CN": "无法应用更新。", "zh-TW": "無法套用更新。", "en-US": "Unable to apply the update.", "ja-JP": "更新を適用できません。", "ko-KR": "업데이트를 적용할 수 없습니다.", "fr-FR": "Impossible d’appliquer la mise à jour.", "de-DE": "Das Update konnte nicht angewendet werden.", "es-ES": "No se pudo aplicar la actualización.", "ru-RU": "Не удалось применить обновление." },
  "update.rolledBack": { "zh-CN": "更新失败，已恢复原版本。", "zh-TW": "更新失敗，已還原原版本。", "en-US": "The update failed; the previous version was restored.", "ja-JP": "更新に失敗しました。以前のバージョンに復元しました。", "ko-KR": "업데이트에 실패하여 이전 버전으로 복원했습니다.", "fr-FR": "La mise à jour a échoué ; la version précédente a été restaurée.", "de-DE": "Das Update ist fehlgeschlagen; die vorherige Version wurde wiederhergestellt.", "es-ES": "La actualización falló; se restauró la versión anterior.", "ru-RU": "Обновление не удалось; предыдущая версия восстановлена." },
  "update.recoveryRequired": { "zh-CN": "更新失败，需要恢复。", "zh-TW": "更新失敗，需要復原。", "en-US": "The update failed; recovery is required.", "ja-JP": "更新に失敗しました。復旧が必要です。", "ko-KR": "업데이트에 실패했습니다. 복구가 필요합니다.", "fr-FR": "La mise à jour a échoué ; une récupération est nécessaire.", "de-DE": "Das Update ist fehlgeschlagen; eine Wiederherstellung ist erforderlich.", "es-ES": "La actualización falló; se requiere recuperación.", "ru-RU": "Обновление не удалось; требуется восстановление." },
  "update.statusUnavailable": copy.unavailable,
} satisfies Record<string, UpdateCopyEntry>;

export function productUpdateCopy(language: string) {
  const mode = resolveLanguageMode(language);
  if (!(languages as readonly string[]).includes(mode)) throw new Error(`Unsupported update language: ${language}`);
  const resolved = mode as UpdateLanguage;
  const result = Object.fromEntries(Object.entries(copy).map(([key, values]) => [key, values[resolved]])) as Record<keyof typeof copy, string>;
  return {
    ...result,
    error(code: string | null | undefined, parameters?: Record<string, string> | null) {
      const values = updateErrors[code as keyof typeof updateErrors] ?? updateErrors["update.preparationFailed"];
      const message = values[resolved];
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
