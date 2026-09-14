import lock from "../../package-lock.json" with { type: "json" };
import managed from "./managedDependencyAcknowledgements.json" with { type: "json" };

interface LockedPackage {
  version?: string;
  license?: string;
  dev?: boolean;
  optional?: boolean;
}

const frontendDependencies = Object.entries(lock.packages)
  .filter(([path]) => path !== "")
  .map(([path, raw]) => {
    const value = raw as LockedPackage;
    return {
      path,
      name: path.slice(path.lastIndexOf("node_modules/") + "node_modules/".length),
      version: value.version ?? "",
      license: value.license ?? "See upstream notice",
      packageUrl: `https://www.npmjs.com/package/${path.slice(path.lastIndexOf("node_modules/") + "node_modules/".length)}/v/${value.version ?? ""}`,
      packageSource: "npm",
      kind: value.optional ? "optional" : value.dev ? "build" : "dependency"
    };
  });

export const dependencyAcknowledgements = [
  ...frontendDependencies,
  ...managed.map((item) => ({ ...item, path: `nuget/${item.name}/${item.version}`,
    packageUrl: `https://www.nuget.org/packages/${item.name}/${item.version}`, packageSource: "NuGet" }))
];
