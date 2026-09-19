export const zhShellCopy = {
  common: {
    saving: "保存中",
    saveFailed: "保存失败"
  },
  feedback: {
    confirm: "确认",
    cancel: "取消",
    close: "关闭",
    success: "操作完成",
    error: "操作失败",
    warning: "需要处理",
    info: "提示"
  },
  page: {
    monitor: "监视控制台",
    components: "组件与软件",
    optimization: "性能优化",
    details: "详细信息",
    settings: "设置",
    diskUsage: "磁盘占用",
    control: "控制面"
  },
  control: {
    title: "控制面",
    intro: "风扇、显卡、处理器的调节都在这一页。控不了的也列出来，并说明差什么。",
    loadFailed: "读不到可控对象。",
    empty: "这台机器上还没认出可以调节的对象。",
    ready: "可调节",
    needsComponent: (name: string) => `需要 ${name}`,
    forgetFailed: "禁止删除",
    instances: {
      title: "识别到过的设备",
      present: "在场",
      absent: "不在场",
      refresh: "重新检测",
      forget: "删除记录",
      firstSeen: (at: string) => `首次见到 ${at}`,
      open: "实例管理"
    },
    actual: "当前 ",
    presets: {
      save: "存为配置",
      namePlaceholder: "配置名",
      remove: "删除配置"
    },
    overclock: {
      title: "Intel 核显调节授权",
      body: "Intel 的核显控制库要求先取得你的同意才允许改动频率，"
        + "负向偏移也不例外。这是它那一侧的硬性要求，和其他显卡无关。",
      accept: "同意",
      revoke: "收回同意",
      accepted: "已同意"
    },
    ownership: {
      label: "控制归属",
      firmware: "固件自动管理",
      app: "软件管理",
      firmwareNote: "归固件管，下面的设定不生效。"
    },
    /*
     * 第一次进控制页说的两屏。
     *
     * 先说保修，再说风险。**不叫"超频免责声明"** —— 这一页大部分事情不是超频：
     * 降压、降频、往下收功耗墙都在厂商设定的范围之内。把它们和超频写成一句话，
     * 用户就无法判断自己正在做的到底是哪一种。
     */
    notice: {
      warrantyTitle: "关于保修",
      warrantyBody: [
        "厂商条款通常把任何超出规格的运行都算作改动，不分方向 —— 按字面说，降压也在内。",
        "实际差别在留不留痕迹：降压、降频、往下收功耗墙断电即失效，处理器里不留记录；"
          + "正向超频和加电压会置位处理器里的标记，送修可查。",
        "笔记本上不少限制是整机厂商在 BIOS 里设的，不是芯片厂商的默认值，"
          + "所以这一页能调到多少也由整机厂商决定。"
      ],
      riskTitle: "开始之前",
      next: "下一步",
      accept: "我已知悉"
    },
    accessLevel: {
      title: "调节权限",
      intro: "决定控制页里哪些项可以调。切到更高一档不会改动任何已有设定，"
        + "只是把更多项放出来。",
      name: { normal: "普通", root: "root" },
      summary: {
        normal: "功耗、电流、风扇及常规频率调节。设置不当可能造成运行不稳定。",
        root: "放开没有兜底的项：直接写电压、改基准时钟。平时用不到。"
      },
      hint: {
        normal: "调错了机器会不稳定，重启能恢复，硬件不受损。要先在设置里切到普通档。",
        root: "这一项没有硬件保护兜底，写错可能开不了机。要先在设置里切到 root 档。"
      },
      consequences: {
        normal: [
          "电压调得过低会突然关机，频率偏移过头会花屏或算错 —— 都是重启就好，不伤硬件。",
          "温度墙调高是把过热保护往外挪，长期这么跑会加速老化。"
        ],
        root: [
          "直接写电压和改基准时钟没有任何软件保护，写错一个数可能开不了机。",
          "这一档的项平时用不到；要调的话先确认你知道那个数字的含义。"
        ]
      },
      /*
       * 免责声明。**两档各一句，用户给的原话，不改写。**
       * 普通档那句在第一次进控制页时就要说 —— 默认这一档也是在动硬件配置。
       */
      disclaimer: {
        normal: "某些设置可能会导致电脑不稳定，对于本就有设计问题的电脑可能会有一定损伤风险，"
          + "调整硬件配置造成的一切后果本软件概不负责。",
        root: "root 模式下某些设置调整极度危险，极大可能造成系统不稳定或者永久性硬件损伤，"
          + "调整硬件配置造成的一切后果本软件概不负责。"
      },
      confirmTitle: (name: string) => `切到${name}档？`,
      confirmAction: "切过去",
      cancel: "取消",
      saveFailed: "切换失败，还在原来那一档。"
    },
    channel: {
      nvapi: "NVAPI",
      "nvapi-drs": "驱动 Profile",
      nvml: "NVML",
      "oem-ec": "整机固件",
      "amd-smu": "AMD SMU",
      "fan-core": "风扇核心",
      igcl: "Intel 显卡库",
      adlx: "AMD ADLX"
    } as Record<string, string>,
    channelHint: "这一项实际走哪条链路写下去。",
    detect: "重新检测硬件",
    detecting: "检测中",
    readings: "只读数值",
    curveExecutionLabel: "曲线交给谁执行",
    takeoverCoversOthers: (others: string) =>
      `这台机器上「交给软件管」是整机一个开关：选了软件接管，${others} 也会一起脱离固件自动控制，停在当时的转速上，除非也给它设一条曲线。`,
    curveExecutionFirmware: "写入固件",
    curveExecutionFirmwareHint: "固件自己按表调速。关掉本程序、重启之后依然有效。",
    curveExecutionSoftware: "软件接管",
    curveExecutionSoftwareHint: "本程序每 0.1 秒算一次并写下去。程序不在时风扇交还固件。",
    createCurve: "新建曲线",
    readingFirmwareCurve: "正在读取固件当前曲线…",
    fixedStepsCurveNote: "这台机器的固件表只让改每一档的转速，温度断点是固件定死的，改不了。",
    curvePreview: "曲线预览",
    saveFailed: "保存失败",
    apply: "应用",
    discard: "撤销改动",
    term: {
      portable: "笔记本电脑",
      fixed: "桌面主机",
      cpu: "CPU风扇",
      "curve-firmware": "固件跑曲线",
      "curve-software": "软件跑曲线",
      "curve-fixed-steps": "固件档位表",
      gpu: "显卡风扇",
      intake: "内吹风扇",
      case: "机箱风扇"
    } as Record<string, string>,
    attachment: {
      integrated: "核显",
      discrete: "独显",
      unknown: "接法未知"
    },
    status: {
      unset: "未设定",
      edited: "已改动，未应用",
      applying: "正在应用",
      applied: "已应用",
      unsupported: "这台机器上控不了",
      failed: "没应用上"
    },
    kind: {
      gpu: "显卡",
      cpu: "处理器",
      fan: "风扇"
    }
  },
  diskUsage: {
    title: "磁盘占用",
    intro: "扫描后按真实大小把文件摆成方格图，一眼看出空间去哪了。",
    scopeLabel: "扫描范围",
    scopeAllVolumes: "全部磁盘",
    scopeVolume: "指定磁盘",
    scopeFolder: "指定文件夹",
    modeLabel: "扫描方式",
    modeFast: "快速扫描",
    modeFastHint: "只适用于 NTFS，需要管理员权限。",
    modeFull: "全扫描",
    modeFullHint: "逐级遍历目录。慢，但任何磁盘都能扫。",
    chooseFolder: "选择文件夹",
    folderNotChosen: "还没选文件夹",
    scan: "开始扫描",
    rescan: "重新扫描",
    cancel: "取消扫描",
    scanning: "正在扫描",
    volumeColumnLabel: "磁盘",
    noVolumes: "没有找到可以扫描的磁盘。",
    notReady: "未就绪",
    freeOfTotal: (free: string, total: string) => `可用 ${free} / 共 ${total}`,
    volumeKind: {
      physical: "物理磁盘",
      virtual: "虚拟磁盘",
      removable: "可移动",
      network: "网络位置",
      optical: "光驱",
      unknown: "来源未知"
    },
    fastUnsupported: "这个磁盘没有可读的文件系统索引，快速扫描扫不到它；用全扫描。",
    skipped: "本次没扫到的目标",
    skipReason: {
      noFileSystemIndex: "文件系统没有可读的索引",
      needsElevation: "读索引需要管理员权限",
      volumeNotReady: "磁盘未就绪",
      targetUnavailable: "路径不存在或打不开"
    },
    scanKind: {
      masterFileTable: "读文件系统索引",
      directoryWalk: "逐级遍历目录"
    },
    navigateUp: "上一层",
    resetView: "复位视图",
    collapseSetup: "收起设置",
    expandSetup: "重新设置",
    scanTotals: (size: string, files: number, folders: number) =>
      `${size} · ${files} 个文件 · ${folders} 个文件夹`,
    fileCount: (count: number) => `${count} 个文件`,
    omitted: (count: number) =>
      `还有 ${count} 个方格这会儿太小或者不在视野里，放大就会出现。`,
    copied: "已复制",
    menuOpenLocation: "打开所在位置",
    menuProperties: "属性",
    menuCopyPath: "复制完整路径",
    menuDrillDown: "只看这一层",
    menuSize: "大小",
    menuPath: "路径",
    menuKindFile: "文件",
    menuKindDirectory: "文件夹",
    emptyTitle: "还没有扫描结果",
    emptyDetail: "选好范围和方式，点「开始扫描」。"
  },
  shell: {
    productName: "资源管理器",
    documentTitle: (page: string, product: string) => `${page} · ${product}`,
    pageNav: "页面切换",
    currentPage: "当前页面",
    runtimeCapability: "运行能力",
    taskCenter: "任务中心",
    taskCenterActive: (count: number) => `任务中心，${count} 项正在进行`,
    taskCenterSyncing: "任务中心 · 后端任务正在同步",
    taskCenterDisconnected: "任务中心 · 本机服务正在重新同步",
    taskCenterUnavailable: "任务中心 · 后端任务状态不可用",
    minimize: "最小化",
    maximize: "最大化",
    closeWindow: "关闭"
  }
};
