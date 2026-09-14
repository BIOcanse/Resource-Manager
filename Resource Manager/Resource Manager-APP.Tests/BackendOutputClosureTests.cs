using System.Xml.Linq;

namespace Resource_Manager_APP.Tests;

public sealed class BackendOutputClosureTests
{
    [Fact]
    public void ReleasePublishProjectsExcludePortableDebugSymbols()
    {
        var appRoot = FindAppRoot();
        var backendProfile = XDocument.Load(Path.Combine(
            appRoot,
            "Properties",
            "PublishProfiles",
            "win-x64-self-contained.pubxml"));
        var nativeUiProject = XDocument.Load(Path.Combine(
            appRoot,
            "NativeUi",
            "ResourceManager.NativeUi.csproj"));

        Assert.Equal(
            "false",
            Assert.Single(
                backendProfile.Descendants(),
                element => element.Name.LocalName ==
                    "CopyOutputSymbolsToPublishDirectory").Value);
        var nativeUiSetting = Assert.Single(
            nativeUiProject.Descendants(),
            element => element.Name.LocalName ==
                "CopyOutputSymbolsToPublishDirectory");
        Assert.Equal("false", nativeUiSetting.Value);
        Assert.Equal(
            "'$(Configuration)' == 'Release'",
            (string?)nativeUiSetting.Attribute("Condition"));
        foreach (var project in new[]
        {
            XDocument.Load(Path.Combine(appRoot, "ResourceManager.App.csproj")),
            nativeUiProject
        })
        {
            var target = Assert.Single(
                project.Descendants(),
                element => (string?)element.Attribute("Name") ==
                    "RemoveReleasePortableDebugSymbolsFromPublish");
            Assert.Equal(
                "ComputeFilesToPublish",
                (string?)target.Attribute("AfterTargets"));
            Assert.Equal(
                "CopyFilesToPublishDirectory",
                (string?)target.Attribute("BeforeTargets"));
            var removal = Assert.Single(
                target.Descendants(),
                element => element.Name.LocalName == "ResolvedFileToPublish");
            Assert.Equal("@(ResolvedFileToPublish)", (string?)removal.Attribute("Remove"));
            Assert.Equal(
                "'%(ResolvedFileToPublish.Extension)' == '.pdb'",
                (string?)removal.Attribute("Condition"));
        }
    }

    [Theory]
    [InlineData(@"NativeUi\**\*")]
    [InlineData(@"Shared\**\*")]
    public void BackendProjectExcludesSourceSubprojectTreeFromDefaultOutputItems(
        string excludedTree)
    {
        var projectPath = Path.Combine(FindAppRoot(), "ResourceManager.App.csproj");
        var document = XDocument.Load(projectPath);
        var removals = document
            .Descendants()
            .Where(element => element.Name.LocalName is "Content" or "None")
            .Select(element => new
            {
                ItemType = element.Name.LocalName,
                Remove = (string?)element.Attribute("Remove")
            })
            .Where(item => item.Remove is not null)
            .ToArray();

        Assert.Contains(
            removals,
            item => item.ItemType == "Content"
                && item.Remove == excludedTree);
        Assert.Contains(
            removals,
            item => item.ItemType == "None"
                && item.Remove == excludedTree);
    }

    private static string FindAppRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath)!;
        var appRoot = Path.GetFullPath(Path.Combine(
            sourceDirectory,
            "..",
            "Resource Manager-APP"));
        return File.Exists(Path.Combine(appRoot, "ResourceManager.App.csproj"))
            ? appRoot
            : throw new DirectoryNotFoundException(
                "Could not locate Resource Manager-APP from the test source path.");
    }
}
