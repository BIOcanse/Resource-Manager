export interface GpuVfCurveCopy {
  title: string; open: string; loading: string; newCurve: string; save: string; duplicate: string; rename: string; remove: string;
  name: string; selectAll: string; clearSelection: string; alignLeft: string; alignRight: string; undo: string; redo: string;
  reset: string; uniformOffset: string; voltage: string; defaultFrequency: string; currentFrequency: string; editedFrequency: string;
  offset: string; selection: string; deviceBinding: string; modelBinding: string; snapshotNote: string; editNote: string;
  noFrequency: string; allVoltages: string; references: string; noCurve: string; close: string;
}
export const gpuVfCurveCopy: Record<string, GpuVfCurveCopy> = {
  "zh-CN": {
    title: "电压–频率曲线", open: "打开曲线库", loading: "正在读取曲线…", newCurve: "新建曲线", save: "保存曲线", duplicate: "复制", rename: "重命名", remove: "删除",
    name: "曲线名称", selectAll: "全选", clearSelection: "清除选择", alignLeft: "对齐最左点", alignRight: "对齐最右点", undo: "撤销", redo: "重做",
    reset: "恢复硬件默认曲线", uniformOffset: "统一偏移 (MHz)", voltage: "电压 (mV)", defaultFrequency: "默认频率 (MHz)", currentFrequency: "当前频率 (MHz)", editedFrequency: "编辑频率 (MHz)",
    offset: "偏移 (MHz)", selection: "选择曲线点", deviceBinding: "仅此设备", modelBinding: "相同型号与电压表", snapshotNote: "对齐基于此次读取的频率快照，不保证所有温度和负载下等频。",
    editNote: "在空白处拖动框选；拖动选中点可整体上下移动。↑/↓ 细调，Shift+↑/↓ 大步调整。保存曲线不会写入硬件；在控制面明确应用后才生效。",
    noFrequency: "没有可用的基准频率，无法绘制频率曲线。仍可编辑偏移。", allVoltages: "所有电压", references: "正在使用此曲线", noCurve: "未选择曲线", close: "关闭编辑器"
  },
  "en-US": {
    title: "Voltage–frequency curve", open: "Open curve library", loading: "Reading curve…", newCurve: "New curve", save: "Save curve", duplicate: "Duplicate", rename: "Rename", remove: "Delete",
    name: "Curve name", selectAll: "Select all", clearSelection: "Clear selection", alignLeft: "Align to leftmost", alignRight: "Align to rightmost", undo: "Undo", redo: "Redo",
    reset: "Restore hardware default curve", uniformOffset: "Uniform offset (MHz)", voltage: "Voltage (mV)", defaultFrequency: "Default frequency (MHz)", currentFrequency: "Current frequency (MHz)", editedFrequency: "Edited frequency (MHz)",
    offset: "Offset (MHz)", selection: "Select curve points", deviceBinding: "This device only", modelBinding: "Matching model and voltage table", snapshotNote: "Alignment uses this frequency snapshot; equal frequency is not guaranteed at every temperature or load.",
    editNote: "Drag on empty space to select a region; drag a selected point to move the group vertically. ↑/↓ adjusts finely; Shift+↑/↓ uses coarse steps. Saving a curve does not write hardware; apply explicitly in the control workspace.",
    noFrequency: "No reference frequencies are available to plot. Offsets can still be edited.", allVoltages: "All voltages", references: "This curve is in use", noCurve: "No curve selected", close: "Close editor"
  },
  "zh-TW": {
    title: "電壓–頻率曲線", open: "開啟曲線庫", loading: "正在讀取曲線…", newCurve: "新增曲線", save: "儲存曲線", duplicate: "複製", rename: "重新命名", remove: "刪除",
    name: "曲線名稱", selectAll: "全選", clearSelection: "清除選取", alignLeft: "對齊最左點", alignRight: "對齊最右點", undo: "復原", redo: "重做",
    reset: "還原硬體預設曲線", uniformOffset: "統一偏移 (MHz)", voltage: "電壓 (mV)", defaultFrequency: "預設頻率 (MHz)", currentFrequency: "目前頻率 (MHz)", editedFrequency: "編輯頻率 (MHz)",
    offset: "偏移 (MHz)", selection: "選取曲線點", deviceBinding: "僅此裝置", modelBinding: "相同型號與電壓表", snapshotNote: "對齊以此次讀取的頻率快照為準，不保證所有溫度與負載下等頻。",
    editNote: "在空白處拖曳以框選；拖曳選取點可整組上下移動。↑/↓ 微調，Shift+↑/↓ 大步調整。儲存曲線不會寫入硬體；請在控制面明確套用。",
    noFrequency: "沒有可用的基準頻率，無法繪製頻率曲線。仍可編輯偏移。", allVoltages: "所有電壓", references: "正在使用此曲線", noCurve: "尚未選取曲線", close: "關閉編輯器"
  },
  "de-DE": {
    title: "Spannungs-Frequenz-Kurve", open: "Kurvenbibliothek öffnen", loading: "Kurve wird gelesen…", newCurve: "Neue Kurve", save: "Kurve speichern", duplicate: "Duplizieren", rename: "Umbenennen", remove: "Löschen",
    name: "Kurvenname", selectAll: "Alle auswählen", clearSelection: "Auswahl aufheben", alignLeft: "Am linken Punkt ausrichten", alignRight: "Am rechten Punkt ausrichten", undo: "Rückgängig", redo: "Wiederholen",
    reset: "Hardware-Standardkurve wiederherstellen", uniformOffset: "Einheitlicher Versatz (MHz)", voltage: "Spannung (mV)", defaultFrequency: "Standardfrequenz (MHz)", currentFrequency: "Aktuelle Frequenz (MHz)", editedFrequency: "Bearbeitete Frequenz (MHz)",
    offset: "Versatz (MHz)", selection: "Kurvenpunkte auswählen", deviceBinding: "Nur dieses Gerät", modelBinding: "Gleiches Modell und gleiche Spannungstabelle", snapshotNote: "Die Ausrichtung verwendet diese Frequenzaufnahme; gleiche Frequenzen sind nicht bei jeder Temperatur und Last gewährleistet.",
    editNote: "Ziehen Sie im freien Bereich, um Punkte auszuwählen. Ziehen Sie einen ausgewählten Punkt, um die Gruppe vertikal zu verschieben. ↑/↓ passt fein an; Shift+↑/↓ verwendet große Schritte. Speichern schreibt keine Hardware; wenden Sie die Kurve ausdrücklich im Steuerbereich an.",
    noFrequency: "Keine Referenzfrequenzen zum Zeichnen verfügbar. Versätze können weiterhin bearbeitet werden.", allVoltages: "Alle Spannungen", references: "Diese Kurve wird verwendet", noCurve: "Keine Kurve ausgewählt", close: "Editor schließen"
  },
  "es-ES": {
    title: "Curva de tensión y frecuencia", open: "Abrir biblioteca de curvas", loading: "Leyendo curva…", newCurve: "Nueva curva", save: "Guardar curva", duplicate: "Duplicar", rename: "Cambiar nombre", remove: "Eliminar",
    name: "Nombre de la curva", selectAll: "Seleccionar todo", clearSelection: "Borrar selección", alignLeft: "Alinear con el punto izquierdo", alignRight: "Alinear con el punto derecho", undo: "Deshacer", redo: "Rehacer",
    reset: "Restaurar curva predeterminada del hardware", uniformOffset: "Desplazamiento uniforme (MHz)", voltage: "Tensión (mV)", defaultFrequency: "Frecuencia predeterminada (MHz)", currentFrequency: "Frecuencia actual (MHz)", editedFrequency: "Frecuencia editada (MHz)",
    offset: "Desplazamiento (MHz)", selection: "Seleccionar puntos", deviceBinding: "Solo este dispositivo", modelBinding: "Mismo modelo y tabla de tensión", snapshotNote: "La alineación utiliza esta instantánea; no se garantiza la misma frecuencia con cualquier temperatura o carga.",
    editNote: "Arrastre en un espacio vacío para seleccionar puntos; arrastre un punto seleccionado para mover el grupo verticalmente. ↑/↓ ajusta con precisión; Shift+↑/↓ utiliza pasos grandes. Guardar no escribe en el hardware; aplique la curva explícitamente en el panel de control.",
    noFrequency: "No hay frecuencias de referencia para dibujar. Puede seguir editando los desplazamientos.", allVoltages: "Todas las tensiones", references: "Esta curva está en uso", noCurve: "Ninguna curva seleccionada", close: "Cerrar editor"
  },
  "fr-FR": {
    title: "Courbe tension–fréquence", open: "Ouvrir la bibliothèque de courbes", loading: "Lecture de la courbe…", newCurve: "Nouvelle courbe", save: "Enregistrer la courbe", duplicate: "Dupliquer", rename: "Renommer", remove: "Supprimer",
    name: "Nom de la courbe", selectAll: "Tout sélectionner", clearSelection: "Effacer la sélection", alignLeft: "Aligner sur le point gauche", alignRight: "Aligner sur le point droit", undo: "Annuler", redo: "Rétablir",
    reset: "Restaurer la courbe matérielle par défaut", uniformOffset: "Décalage uniforme (MHz)", voltage: "Tension (mV)", defaultFrequency: "Fréquence par défaut (MHz)", currentFrequency: "Fréquence actuelle (MHz)", editedFrequency: "Fréquence modifiée (MHz)",
    offset: "Décalage (MHz)", selection: "Sélectionner les points", deviceBinding: "Cet appareil uniquement", modelBinding: "Même modèle et même table de tension", snapshotNote: "L’alignement utilise cet instantané ; une fréquence identique n’est pas garantie pour toute température ou charge.",
    editNote: "Faites glisser dans une zone vide pour sélectionner les points ; déplacez un point sélectionné pour déplacer le groupe verticalement. ↑/↓ ajuste finement ; Shift+↑/↓ utilise de grands pas. Enregistrer ne modifie pas le matériel ; appliquez explicitement la courbe dans le panneau de contrôle.",
    noFrequency: "Aucune fréquence de référence à tracer. Les décalages restent modifiables.", allVoltages: "Toutes les tensions", references: "Cette courbe est utilisée", noCurve: "Aucune courbe sélectionnée", close: "Fermer l’éditeur"
  },
  "ja-JP": {
    title: "電圧・周波数カーブ", open: "カーブライブラリを開く", loading: "カーブを読み取り中…", newCurve: "新規カーブ", save: "カーブを保存", duplicate: "複製", rename: "名前を変更", remove: "削除",
    name: "カーブ名", selectAll: "すべて選択", clearSelection: "選択を解除", alignLeft: "左端の点に揃える", alignRight: "右端の点に揃える", undo: "元に戻す", redo: "やり直す",
    reset: "ハードウェアの既定カーブに戻す", uniformOffset: "一律オフセット (MHz)", voltage: "電圧 (mV)", defaultFrequency: "既定の周波数 (MHz)", currentFrequency: "現在の周波数 (MHz)", editedFrequency: "編集後の周波数 (MHz)",
    offset: "オフセット (MHz)", selection: "カーブ上の点を選択", deviceBinding: "このデバイスのみ", modelBinding: "同じモデルと電圧テーブル", snapshotNote: "整列には今回の周波数スナップショットを使用します。すべての温度や負荷で同じ周波数になるとは限りません。",
    editNote: "空白部分をドラッグして範囲選択し、選択した点をドラッグして全体を上下に移動します。↑/↓で微調整、Shift+↑/↓で大きく調整します。保存ではハードウェアに書き込みません。コントロール画面で明示的に適用してください。",
    noFrequency: "基準周波数がないため描画できません。オフセットは編集できます。", allVoltages: "すべての電圧", references: "このカーブは使用中です", noCurve: "カーブ未選択", close: "エディターを閉じる"
  },
  "ko-KR": {
    title: "전압–주파수 곡선", open: "곡선 라이브러리 열기", loading: "곡선을 읽는 중…", newCurve: "새 곡선", save: "곡선 저장", duplicate: "복제", rename: "이름 변경", remove: "삭제",
    name: "곡선 이름", selectAll: "모두 선택", clearSelection: "선택 해제", alignLeft: "가장 왼쪽 점에 정렬", alignRight: "가장 오른쪽 점에 정렬", undo: "실행 취소", redo: "다시 실행",
    reset: "하드웨어 기본 곡선 복원", uniformOffset: "균일 오프셋 (MHz)", voltage: "전압 (mV)", defaultFrequency: "기본 주파수 (MHz)", currentFrequency: "현재 주파수 (MHz)", editedFrequency: "편집한 주파수 (MHz)",
    offset: "오프셋 (MHz)", selection: "곡선 점 선택", deviceBinding: "이 장치만", modelBinding: "동일 모델 및 전압 표", snapshotNote: "정렬은 이번 주파수 스냅샷을 사용하며, 모든 온도와 부하에서 같은 주파수를 보장하지 않습니다.",
    editNote: "빈 영역을 드래그하여 선택하고, 선택한 점을 드래그하여 그룹을 위아래로 이동합니다. ↑/↓는 미세 조정, Shift+↑/↓는 큰 단위 조정입니다. 저장은 하드웨어에 쓰지 않습니다. 제어 화면에서 명시적으로 적용하십시오.",
    noFrequency: "기준 주파수가 없어 그래프를 그릴 수 없습니다. 오프셋은 계속 편집할 수 있습니다.", allVoltages: "모든 전압", references: "이 곡선은 사용 중입니다", noCurve: "선택한 곡선 없음", close: "편집기 닫기"
  },
  "ru-RU": {
    title: "Кривая напряжения и частоты", open: "Открыть библиотеку кривых", loading: "Чтение кривой…", newCurve: "Новая кривая", save: "Сохранить кривую", duplicate: "Дублировать", rename: "Переименовать", remove: "Удалить",
    name: "Название кривой", selectAll: "Выбрать все", clearSelection: "Снять выделение", alignLeft: "Выровнять по левой точке", alignRight: "Выровнять по правой точке", undo: "Отменить", redo: "Повторить",
    reset: "Восстановить аппаратную кривую по умолчанию", uniformOffset: "Единое смещение (МГц)", voltage: "Напряжение (мВ)", defaultFrequency: "Частота по умолчанию (МГц)", currentFrequency: "Текущая частота (МГц)", editedFrequency: "Изменённая частота (МГц)",
    offset: "Смещение (МГц)", selection: "Выбрать точки кривой", deviceBinding: "Только это устройство", modelBinding: "Та же модель и таблица напряжений", snapshotNote: "Выравнивание использует этот снимок частот; одинаковая частота при любой температуре и нагрузке не гарантируется.",
    editNote: "Перетащите по пустому месту для выделения; перетаскивание выбранной точки перемещает группу вертикально. ↑/↓ — точная настройка, Shift+↑/↓ — крупный шаг. Сохранение не изменяет оборудование; явно примените кривую на панели управления.",
    noFrequency: "Нет опорных частот для построения графика. Смещения по-прежнему можно изменять.", allVoltages: "Все напряжения", references: "Эта кривая используется", noCurve: "Кривая не выбрана", close: "Закрыть редактор"
  }
};
