export const zhBackendMessagesCopy = {
  backendMessage: {
    unknown: "状态已更新。",
    dependency: {
      managedRootRemovable: "删除该依赖的受管 Dependencies 根目录。",
      managedRootEmpty: "该依赖当前没有可清理的受管安装内容。",
      categoryTag: "组件依赖",
      uninstallAction: "卸载",
      installedInManagedRoot: "已安装在 Dependencies 软件根目录。",
      installerCached: "安装器已在 Misc 安装器缓存中，可安装到 Dependencies。",
      downloadableWithVersionChoice: "可从官方发布页下载安装器，可选已验证版本或最新版本。",
      downloadable: "可从官方来源下载安装器。",
      manualAcquisition: "打开官方来源页，并将安装器放入 Misc 安装器缓存。",
      reusingExternalInstall: (value: string) => `检测到系统已有安装，直接复用：${value}`,
      providerVerified: "Provider 已通过实时指标验证。",
      providerRuntimeUnverified: "检测到系统已安装的 Provider 运行库，但还没有通过实时读数验证。",
      componentFilesUnverified: "组件文件已就绪，但 Provider 还没有通过实时读数验证。",
      runtimeAvailableBridgePending: "运行库可用，但 Provider 桥接或实时读数验证尚未完成。",
      providerBridgeMissing: "Provider 桥接尚未接入，或当前硬件/驱动未返回可验证读数。",
      bundledVerified: "内置组件已通过实时指标验证。",
      bundledUnverified: "内置组件已安装；当前硬件或 OEM 运行库未返回可验证读数。",
      alreadyInstalledReuse: "组件已安装，直接复用现有安装。",
      installerDownloaded: "安装器已下载到托管依赖目录。",
      installerDownloadedVersion: (value: string) => `已下载 ${value} 的安装器到托管依赖目录。`,
      sharedRuntimeInstalled: "共享 WebView2 Runtime 已安装并通过检测。",
      installerLaunched: "安装器已可见启动。使用该 Provider 前需要完成厂商安装提示。"
    },
    gpuPlacement: {
      singleAdapter: "这台机器只有一个显卡，GPU 调度没有可选目标。"
    },
    metric: {
      notExposed: "当前硬件或 Provider 未暴露这个读数。",
      needsComponent: (value: string) => `当前 Provider 未返回有效读数，需要更完整的 ${value} 或对应硬件/OEM 组件。`,
      noValidReading: "当前硬件或 Provider 未返回有效读数。"
    },
    software: {
      hasUninstallEntry: "有卸载入口",
      missingUninstallEntry: "缺少卸载入口",
      uninstallAction: "卸载",
      cannotUninstallAction: "不可卸载",
      windowsUninstallerDescription: "启动 Windows 注册表提供的官方卸载器。",
      noUninstallUnknownRoot: "缺少卸载入口，只能跳转或手动处理，Resource Manager 不删除未知软件根目录。",
      manualClassificationNoUninstall: "手动分类/补录项不直接代表卸载器；需要从软件详情或原始软件入口处理。",
      controlledNoUninstall: "受控软件卸载策略需要独立记录；当前只支持数据迁移恢复。",
      selfNoUninstall: "资源管理器自身不能从这里卸载或迁移。",
      adaptedNoUninstall: "适配软件需要通过注册/manifest 声明卸载策略后才能由 Resource Manager 执行。",
      legacyControlledNoUninstall: "L0 受控注册没有卸载策略。",
      portableNoUninstall: "便携软件没有卸载器；删除原文件后刷新软件列表会自动移除记录。"
    }
  }
};
