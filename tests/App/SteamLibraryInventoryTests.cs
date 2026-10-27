using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Software;

namespace Resource_Manager_APP.Tests;

public sealed class SteamLibraryInventoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rm-steam-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LibraryManifestsDiscoverGamesWithoutUninstallKeysAndCorrectSharedClientRoots()
    {
        var client = Path.Combine(root, "Steam");
        var library = Path.Combine(root, "Library");
        var steamapps = Directory.CreateDirectory(Path.Combine(client, "steamapps")).FullName;
        Directory.CreateDirectory(Path.Combine(library, "steamapps"));
        var game = Directory.CreateDirectory(Path.Combine(library, "steamapps", "common", "Counter-Strike Global Offensive")).FullName;
        File.WriteAllText(Path.Combine(steamapps, "libraryfolders.vdf"),
            "\"libraryfolders\" { \"1\" { \"path\" \"" + library.Replace("\\", "\\\\") + "\" \"apps\" { \"730\" \"123\" } } }");
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_730.acf"),
            "\"AppState\" { \"appid\" \"730\" \"name\" \"Counter-Strike 2\" \"installdir\" \"Counter-Strike Global Offensive\" }");
        InstalledSoftwareEntry[] inputs = [
            new("windows-installed:localmachine-registry32-software-microsoft-windows-currentversion-uninstall-steam", "Steam", null, "Valve", client, [client], null, @"HKLM\Uninstall\Steam"),
            new("windows-installed:localmachine-registry64-software-microsoft-windows-currentversion-uninstall-steam-app-2807960", "战地风云™ 6", null, null, client, [client], null, @"HKLM\Uninstall\Steam App 2807960")
        ];
        var entries = WindowsInstalledSoftwareInventory.ReadSteamLibraries(inputs, default);
        var cs2 = Assert.Single(entries, entry => entry.Id == "steam-app-730");
        Assert.Equal("Counter-Strike 2", cs2.Name);
        Assert.Equal([game], cs2.RootPaths);
        Assert.EndsWith("appmanifest_730.acf", cs2.RegistryPath);
        Assert.Null(cs2.UninstallString);
        Assert.Empty(entries.Single(entry => entry.Id == inputs[1].Id).RootPaths);
        Assert.Equal([client], entries.Single(entry => entry.Id == inputs[0].Id).RootPaths);
        // A manifest supplements an existing uninstall record rather than adding a duplicate.
        var registered = inputs.Append(cs2 with { Id = inputs[1].Id.Replace("2807960", "730"), RootPaths = [client] }).ToArray();
        var merged = WindowsInstalledSoftwareInventory.ReadSteamLibraries(registered, default);
        Assert.Equal(3, merged.Count);
        Assert.Equal([game], merged.Single(entry => entry.Id == registered[2].Id).RootPaths);
    }

    [Theory]
    [InlineData("../outside", "730")]
    [InlineData("Valid", "731")]
    [InlineData("Missing", "730")]
    public void InvalidOrAbsentGameFoldersDoNotBecomeInventory(string installDir, string appId)
    {
        var steamapps = Directory.CreateDirectory(Path.Combine(root, "steamapps")).FullName;
        Directory.CreateDirectory(Path.Combine(steamapps, "common", "Valid"));
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_730.acf"),
            $"\"AppState\" {{ \"appid\" \"{appId}\" \"name\" \"Fixture\" \"installdir\" \"{installDir}\" }}");
        InstalledSoftwareEntry steam = new("windows-installed:localmachine-registry32-software-microsoft-windows-currentversion-uninstall-steam", "Steam", null, null, root, [root], null, @"HKLM\Uninstall\Steam");
        Assert.Single(WindowsInstalledSoftwareInventory.ReadSteamLibraries([steam], default));
    }

    public void Dispose()
    {
        // Temporary fixtures are owned by this test and bounded to two directories.
        if (!Directory.Exists(root)) return;
        var pending = new Stack<string>();
        var visited = new List<string>();
        pending.Push(root);
        while (pending.Count > 0 && visited.Count < 100)
        {
            var directory = pending.Pop();
            visited.Add(directory);
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            foreach (var child in Directory.EnumerateDirectories(directory)) pending.Push(child);
        }
        foreach (var directory in visited.AsEnumerable().Reverse()) Directory.Delete(directory);
    }
}
