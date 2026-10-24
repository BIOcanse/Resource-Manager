using System.Reflection;
using ResourceManager.Updater;

namespace Resource_Manager_APP.Tests;

public sealed class PackageSwitchFanCoreTests
{
    [Fact]
    public void UpdateCopiesRecoveryStateAndBridgeButRetiresTheFanExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-fan-update-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "installed");
        var target = Path.Combine(root, "next");
        var fan = Path.Combine("Dependencies", "fan-control-core");
        var bridge = Path.Combine("Dependencies", "hardware-bridge");
        var state = Path.Combine("UserData", "Control", "FanControlCore");
        try
        {
            foreach (var directory in new[] { fan, bridge, state }) Directory.CreateDirectory(Path.Combine(source, directory));
            File.WriteAllText(Path.Combine(source, fan, "FanControlCore.exe"), "retired helper");
            File.WriteAllText(Path.Combine(source, bridge, "ResourceManager.HardwareBridge.exe"), "bridge");
            File.WriteAllText(Path.Combine(source, state, "temporary-takeovers.json"), "durable recovery state");
            var copy = typeof(PackageSwitchTransaction).GetMethod("CopyMutableData", BindingFlags.Static | BindingFlags.NonPublic)!;
            copy.Invoke(null, [source, target, true]);
            Assert.False(Directory.Exists(Path.Combine(target, fan)));
            Assert.Equal("bridge", File.ReadAllText(Path.Combine(target, bridge, "ResourceManager.HardwareBridge.exe")));
            Assert.Equal("durable recovery state", File.ReadAllText(Path.Combine(target, state, "temporary-takeovers.json")));
            Assert.True(File.Exists(Path.Combine(source, fan, "FanControlCore.exe"))); // Rollback source remains.
        }
        finally
        {
            foreach (var tree in new[] { target, source })
            {
                if (File.Exists(Path.Combine(tree, fan, "FanControlCore.exe"))) File.Delete(Path.Combine(tree, fan, "FanControlCore.exe"));
                if (File.Exists(Path.Combine(tree, bridge, "ResourceManager.HardwareBridge.exe"))) File.Delete(Path.Combine(tree, bridge, "ResourceManager.HardwareBridge.exe"));
                if (File.Exists(Path.Combine(tree, state, "temporary-takeovers.json"))) File.Delete(Path.Combine(tree, state, "temporary-takeovers.json"));
                foreach (var directory in new[] { fan, bridge, state, Path.Combine("UserData", "Control"), "UserData", "Dependencies", "" })
                {
                    var path = Path.Combine(tree, directory);
                    if (Directory.Exists(path)) Directory.Delete(path);
                }
            }
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }
}
