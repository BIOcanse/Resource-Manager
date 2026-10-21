using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Http;
using ResourceManager.App.Domain.Indexing;
using ResourceManager.App.Infrastructure.Indexing;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsFileIndexCallerAccessTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rm-index-caller-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> deniedFiles = [];

    [Fact]
    public void SearchChecksFilePermissionInsteadOfPublishingEveryIndexedPath()
    {
        Directory.CreateDirectory(root);
        using var caller = WindowsIdentity.GetCurrent();
        var allowed = WriteFile("allowed.txt");
        var denied = WriteFile("private.txt");
        DenyFileRead(denied, caller.User!);
        var results = WindowsFileIndexCallerAccess.FilterSearch(caller.AccessToken, [Row(allowed), Row(denied), Row(Path.Combine(root, "gone.txt"))]);
        Assert.Equal(allowed, Assert.Single(results).FullPath);
    }

    [Fact]
    public void SnapshotPathFilterRequiresReadableRoots()
    {
        Directory.CreateDirectory(root);
        using var caller = WindowsIdentity.GetCurrent();
        var readable = Directory.CreateDirectory(Path.Combine(root, "public")).FullName;
        var denied = Directory.CreateDirectory(Path.Combine(root, "private")).FullName;
        var security = new DirectoryInfo(denied).GetAccessControl();
        security.AddAccessRule(new(caller.User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        new DirectoryInfo(denied).SetAccessControl(security);
        try
        {
            var filter = WindowsFileIndexCallerAccess.CreatePathFilter(caller.AccessToken);
            Assert.True(filter(readable, null));
            Assert.False(filter(denied, null));
        }
        finally
        {
            security.RemoveAccessRuleSpecific(new(caller.User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
            new DirectoryInfo(denied).SetAccessControl(security);
        }
    }

    [Fact]
    public void HeadersCannotSupplyCallerIdentity()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Windows-Sid"] = "S-1-5-18";
        Assert.False(WindowsFileIndexCallerAccess.TryOpenCallerToken(context, out var token));
        token.Dispose();
    }

    [Fact]
    public void ReadableFileInNonListableDirectoryDoesNotRevealItsIndexedName()
    {
        Directory.CreateDirectory(root);
        using var caller = WindowsIdentity.GetCurrent();
        var child = Directory.CreateDirectory(Path.Combine(root, "private")).FullName;
        var file = Path.Combine(child, "secret-name.txt");
        File.WriteAllText(file, "owned test data");
        var security = new DirectoryInfo(child).GetAccessControl();
        var deny = new FileSystemAccessRule(caller.User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        security.AddAccessRule(deny);
        new DirectoryInfo(child).SetAccessControl(security);
        try
        {
            using (File.OpenRead(file)) { } // ReadData is allowed; only folder listing is denied.
            Assert.Empty(WindowsFileIndexCallerAccess.FilterSearch(caller.AccessToken,
                [Row(file) with { RelativePath = Path.GetRelativePath(root, file) }]));
        }
        finally
        {
            security.RemoveAccessRuleSpecific(deny);
            new DirectoryInfo(child).SetAccessControl(security);
        }
    }

    [Fact]
    public void FilterRejectsRootedAndEscapingIndexEntries()
    {
        Directory.CreateDirectory(root);
        var file = WriteFile("allowed.txt");
        using var caller = WindowsIdentity.GetCurrent();
        var filter = WindowsFileIndexCallerAccess.CreatePathFilter(caller.AccessToken);
        Assert.True(filter(root, "allowed.txt"));
        Assert.False(filter(root, file));
        Assert.False(filter(root, "..\\other.txt"));
    }

    private string WriteFile(string name)
    { var path = Path.Combine(root, name); File.WriteAllText(path, "owned test data"); return path; }
    private SoftwareFileSearchResult Row(string path)
        => new(1, "test", "owned", root, Path.GetFileName(path), path, Path.GetFileName(path), ".txt", 15, DateTimeOffset.UtcNow);
    private void DenyFileRead(string path, SecurityIdentifier sid)
    {
        var security = new FileInfo(path).GetAccessControl();
        security.AddAccessRule(new(sid, FileSystemRights.ReadData, AccessControlType.Deny));
        new FileInfo(path).SetAccessControl(security);
        deniedFiles.Add(path);
    }
    public void Dispose()
    {
        using var caller = WindowsIdentity.GetCurrent();
        foreach (var path in deniedFiles)
        {
            var security = new FileInfo(path).GetAccessControl();
            security.RemoveAccessRuleSpecific(new(caller.User!, FileSystemRights.ReadData, AccessControlType.Deny));
            new FileInfo(path).SetAccessControl(security);
        }
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
