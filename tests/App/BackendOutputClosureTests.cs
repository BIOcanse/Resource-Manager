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
        Assert.Equal(
            @"$(MSBuildProjectDirectory)\..\..\artifacts\local-publish\ResourceManager\",
            Assert.Single(
                backendProfile.Descendants(),
                element => element.Name.LocalName == "PublishDir").Value);
        var nativeUiProject = XDocument.Load(Path.Combine(
            Path.GetFullPath(Path.Combine(appRoot, "..", "..")),
            "src", "UI", "Core",
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
    [InlineData("NativeUi", "UI/Core/ResourceManager.NativeUi.csproj")]
    [InlineData("Shared", "Shared/ResourceManager.Shared.csproj")]
    public void BackendSourceSubprojectsAreOutsideCore(
        string oldChildDirectory,
        string projectPath)
    {
        var appRoot = FindAppRoot();
        var repositoryRoot = Path.GetFullPath(Path.Combine(appRoot, "..", ".."));
        Assert.False(Directory.Exists(Path.Combine(appRoot, oldChildDirectory)));
        Assert.True(File.Exists(Path.Combine(
            repositoryRoot,
            "src",
            projectPath.Replace('/', Path.DirectorySeparatorChar))));
    }

    private static string FindAppRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath)!;
        var appRoot = Path.GetFullPath(Path.Combine(
            sourceDirectory,
            "..",
            "..",
            "src",
            "Core"));
        return File.Exists(Path.Combine(appRoot, "ResourceManager.App.csproj"))
            ? appRoot
            : throw new DirectoryNotFoundException(
                "Could not locate Resource Manager-APP from the test source path.");
    }
}
