import type { AppCopy } from "../zh/index.ts";

export const enBackendMessagesCopy: Pick<AppCopy, "backendMessage"> = {
  backendMessage: {
    unknown: "The state was updated.",
    dependency: {
      managedRootRemovable: "Deletes the managed Dependencies root of this dependency.",
      managedRootEmpty: "This dependency has no managed installation to clean up.",
      categoryTag: "Component dependency",
      uninstallAction: "Uninstall",
      installedInManagedRoot: "Installed in the Dependencies software root.",
      installerCached: "The installer is in the Misc installer cache and can be installed into Dependencies.",
      downloadableWithVersionChoice: "The installer can be downloaded from the official release page, with a verified or latest version.",
      downloadable: "The installer can be downloaded from the official source.",
      manualAcquisition: "Open the official source page and put the installer into the Misc installer cache.",
      reusingExternalInstall: (value: string) => `An existing system installation was found and is reused: ${value}`,
      providerVerified: "The provider is verified by live metrics.",
      providerRuntimeUnverified: "A provider runtime installed on this system was found, but it is not verified by live readings yet.",
      componentFilesUnverified: "The component files are in place, but the provider is not verified by live readings yet.",
      runtimeAvailableBridgePending: "The runtime is available, but the provider bridge or live-reading verification is not complete.",
      providerBridgeMissing: "The provider bridge is not connected, or the current hardware or driver returns no verifiable readings.",
      bundledVerified: "The bundled component is verified by live metrics.",
      bundledUnverified: "The bundled component is installed; the current hardware or OEM runtime returns no verifiable readings.",
      alreadyInstalledReuse: "The component is already installed and the existing installation is reused.",
      installerDownloaded: "The installer was downloaded into the managed dependency directory.",
      installerDownloadedVersion: (value: string) => `The installer for ${value} was downloaded into the managed dependency directory.`,
      sharedRuntimeInstalled: "The shared WebView2 runtime is installed and detected.",
      installerLaunched: "The installer started visibly. Finish the vendor's installation prompts before using this provider."
    },
    gpuPlacement: {
      singleAdapter: "This machine has a single GPU, so GPU scheduling has no target to choose."
    },
    metric: {
      notExposed: "The current hardware or provider does not expose this reading.",
      needsComponent: (value: string) => `The current provider returns no valid reading; a more complete ${value} or the matching hardware or OEM component is needed.`,
      noValidReading: "The current hardware or provider returns no valid reading."
    }
  }
};
