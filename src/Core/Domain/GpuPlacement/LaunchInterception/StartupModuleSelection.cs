using ResourceManager.Shared.Packages;
using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.GpuPlacement;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StartupModuleSelection(string SoftwareId, IReadOnlyList<string> ModuleIds);
public sealed record StartupModuleSettingsDocument(int Version, IReadOnlyList<StartupModuleSelection> Software);
public sealed record StartupModulePlan(string PackageRoot, PackageStartupModule Module);
