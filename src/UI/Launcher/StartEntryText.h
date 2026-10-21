#ifndef RESOURCE_MANAGER_START_ENTRY_TEXT_H
#define RESOURCE_MANAGER_START_ENTRY_TEXT_H

/* The bootstrap can report a missing managed payload without loading it.
   Installed language preferences are read by the managed launcher. */
typedef struct StartEntryErrorCopy
{
    const wchar_t *missingLauncher;
    const wchar_t *createFailed;
    const wchar_t *recovery;
} StartEntryErrorCopy;

static const StartEntryErrorCopy StartEntryCopies[] = {
    { L"发行包内的启动器不存在。请重新解压完整发行包。", L"无法启动 Resource Manager。", L"安装中的启动器缺失。是否打开独立更新管理器进行恢复或修复？" },
    { L"發行包內的啟動器不存在。請重新解壓縮完整發行包。", L"無法啟動 Resource Manager。", L"安裝中的啟動器遺失。是否開啟獨立更新管理員進行復原或修復？" },
    { L"The launcher is missing from the release package. Extract the complete release package again.", L"Unable to start Resource Manager.", L"The installed launcher is missing. Open the independent update manager to recover or repair?" },
    { L"リリースパッケージにランチャーがありません。完全なパッケージを再度展開してください。", L"Resource Manager を起動できません。", L"インストール済みランチャーがありません。独立した更新マネージャーを開いて復旧または修復しますか？" },
    { L"배포 패키지에 실행기가 없습니다. 전체 배포 패키지를 다시 압축 해제하세요.", L"Resource Manager를 시작할 수 없습니다.", L"설치된 실행기가 없습니다. 독립 업데이트 관리자를 열어 복구하거나 수정하시겠습니까?" },
    { L"Le lanceur est absent du paquet de distribution. Extrayez à nouveau le paquet complet.", L"Impossible de démarrer Resource Manager.", L"Le lanceur installé est absent. Ouvrir le gestionnaire indépendant pour restaurer ou réparer ?" },
    { L"Das Startprogramm fehlt im Veröffentlichungspaket. Entpacken Sie das vollständige Paket erneut.", L"Resource Manager konnte nicht gestartet werden.", L"Das installierte Startprogramm fehlt. Unabhängigen Update-Manager zur Wiederherstellung oder Reparatur öffnen?" },
    { L"Falta el lanzador en el paquete de distribución. Vuelva a extraer el paquete completo.", L"No se pudo iniciar Resource Manager.", L"Falta el lanzador instalado. ¿Abrir el gestor independiente para recuperar o reparar?" },
    { L"В пакете выпуска отсутствует программа запуска. Распакуйте полный пакет заново.", L"Не удалось запустить Resource Manager.", L"Установленная программа запуска отсутствует. Открыть независимый менеджер для восстановления или исправления?" }
};

static const StartEntryErrorCopy *StartEntryTextForLanguage(LANGID language)
{
    switch (PRIMARYLANGID(language))
    {
        case LANG_CHINESE:
            switch (SUBLANGID(language))
            {
                case SUBLANG_CHINESE_TRADITIONAL:
                case SUBLANG_CHINESE_HONGKONG:
                case SUBLANG_CHINESE_MACAU:
                    return &StartEntryCopies[1];
                default: return &StartEntryCopies[0];
            }
        case LANG_ENGLISH: return &StartEntryCopies[2];
        case LANG_JAPANESE: return &StartEntryCopies[3];
        case LANG_KOREAN: return &StartEntryCopies[4];
        case LANG_FRENCH: return &StartEntryCopies[5];
        case LANG_GERMAN: return &StartEntryCopies[6];
        case LANG_SPANISH: return &StartEntryCopies[7];
        case LANG_RUSSIAN: return &StartEntryCopies[8];
        default: return &StartEntryCopies[2];
    }
}

#endif
