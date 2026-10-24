using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.Indexing;
using ResourceManager.App.Infrastructure.Persistence;
using ResourceManager.App.Infrastructure.Security;

namespace Resource_Manager_APP.Tests;

public sealed partial class LoopbackApiAccessTests
{
    [Fact]
    public async Task ActualIndexHttpFiltersCallerPathsAndTotalsThenRechecksNewRequests()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-index-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var workspace = NativeFileQueryTestWorkspaceFactory.Create();
        using var caller = WindowsIdentity.GetCurrent();
        var visibleRoot = Directory.CreateDirectory(Path.Combine(root, "visible")).FullName;
        var hiddenRoot = Directory.CreateDirectory(Path.Combine(root, "hidden")).FullName;
        var privateDirectory = Directory.CreateDirectory(Path.Combine(visibleRoot, "private")).FullName;
        var allowedFile = Path.Combine(visibleRoot, "public.bin");
        var deniedFile = Path.Combine(visibleRoot, "denied.bin");
        var goneFile = Path.Combine(visibleRoot, "gone.bin");
        await File.WriteAllBytesAsync(allowedFile, new byte[7]);
        await File.WriteAllBytesAsync(deniedFile, new byte[13]);
        await File.WriteAllBytesAsync(goneFile, new byte[11]);
        await File.WriteAllBytesAsync(Path.Combine(privateDirectory, "secret.bin"), new byte[29]);
        await File.WriteAllBytesAsync(Path.Combine(hiddenRoot, "root-secret.bin"), new byte[17]);
        var fileAccess = new FileInfo(deniedFile).GetAccessControl();
        var directoryAccess = new DirectoryInfo(privateDirectory).GetAccessControl();
        var rootAccess = new DirectoryInfo(hiddenRoot).GetAccessControl();
        void RestorePermissions()
        {
            // A descriptor read from disk has no modified sections. Mark the
            // saved access section explicitly before applying the original ACL.
            fileAccess.SetSecurityDescriptorBinaryForm(fileAccess.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            directoryAccess.SetSecurityDescriptorBinaryForm(directoryAccess.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            rootAccess.SetSecurityDescriptorBinaryForm(rootAccess.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            new FileInfo(deniedFile).SetAccessControl(fileAccess);
            new DirectoryInfo(privateDirectory).SetAccessControl(directoryAccess);
            new DirectoryInfo(hiddenRoot).SetAccessControl(rootAccess);
        }
        try
        {
            var databaseRoot = Directory.CreateDirectory(Path.Combine(root, "database")).FullName;
            var index = new SqliteSoftwareFileIndex(new ResourceManagerDatabase(new IndexHttpEnvironment(databaseRoot)),
                workspace, NullLogger<SqliteSoftwareFileIndex>.Instance);
            await index.GetOrRefreshAsync(new("http-app", "HTTP App", [new(visibleRoot, "Program"), new(hiddenRoot, "Data")]),
                TimeSpan.Zero, true, default);
            File.Delete(goneFile);
            var denied = new FileSystemAccessRule(caller.User!, FileSystemRights.ReadData, AccessControlType.Deny);
            var noListing = new FileSystemAccessRule(caller.User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
            var restrictedFile = new FileInfo(deniedFile).GetAccessControl(); restrictedFile.AddAccessRule(denied);
            var restrictedDirectory = new DirectoryInfo(privateDirectory).GetAccessControl(); restrictedDirectory.AddAccessRule(noListing);
            var restrictedRoot = new DirectoryInfo(hiddenRoot).GetAccessControl(); restrictedRoot.AddAccessRule(noListing);
            new FileInfo(deniedFile).SetAccessControl(restrictedFile);
            new DirectoryInfo(privateDirectory).SetAccessControl(restrictedDirectory);
            new DirectoryInfo(hiddenRoot).SetAccessControl(restrictedRoot);

            await using var fixture = await Fixture.StartAsync(fileIndex: index);
            fixture.Administrator.ThrowIfCalled = true;
            fixture.Client.DefaultRequestHeaders.Add(LoopbackApiAccessToken.HeaderName, fixture.Token);
            foreach (var path in IndexHttpPaths)
            {
                using var self = await fixture.Client.GetAsync(path);
                Assert.Equal(System.Net.HttpStatusCode.Forbidden, self.StatusCode);
            }
            var deniedResults = await RequestIndexFromOwnedProcessAsync(fixture.Client.BaseAddress!, fixture.Token);
            AssertIndexResponses(deniedResults, allowedFile, 1, 7, 1);
            Assert.DoesNotContain("denied.bin", deniedResults.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret.bin", deniedResults.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gone.bin", deniedResults.GetRawText(), StringComparison.OrdinalIgnoreCase);
            var limitedResults = await RequestIndexFromOwnedProcessAsync(fixture.Client.BaseAddress!, fixture.Token,
                ["/api/index/files/search?query=bin&limit=1", "/api/public/v1/index/files/search?query=bin&limit=1"]);
            Assert.Equal(2, limitedResults.GetArrayLength());
            foreach (var response in limitedResults.EnumerateArray())
                Assert.Equal(allowedFile, Assert.Single(response.EnumerateArray()).GetProperty("fullPath").GetString());
            var serviceSnapshot = (await index.GetSnapshotAsync("http-app", default))!;
            Assert.Equal(5, serviceSnapshot.FileCount);
            Assert.Equal(77, serviceSnapshot.TotalBytes);

            RestorePermissions();
            var readableResults = await RequestIndexFromOwnedProcessAsync(fixture.Client.BaseAddress!, fixture.Token);
            AssertIndexResponses(readableResults, allowedFile, 4, 66, 2);
            Assert.Equal(7, fixture.PublicAccess.Evaluations);
            Assert.Equal(7, fixture.PublicAccess.Completions);
            Assert.Equal(0, fixture.Administrator.Calls);
            Assert.Null(fixture.LastRequestError);
        }
        finally
        {
            RestorePermissions();
            SqliteConnection.ClearAllPools();
            var pending = new Queue<string>(); pending.Enqueue(root);
            var directories = new List<string>();
            var entries = 0;
            while (pending.TryDequeue(out var directory))
            {
                Assert.True(++entries <= 128);
                Assert.False(File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint));
                directories.Add(directory);
                foreach (var file in Directory.GetFiles(directory)) { Assert.True(++entries <= 128); File.Delete(file); }
                foreach (var child in Directory.GetDirectories(directory)) pending.Enqueue(child);
            }
            for (var i = directories.Count - 1; i >= 0; i--) Directory.Delete(directories[i]);
        }
    }

    private static readonly string[] IndexHttpPaths =
    ["/api/index/files/search?query=bin&limit=20", "/api/index/software/http-app",
     "/api/public/v1/index/files/search?query=bin&limit=20", "/api/public/v1/index/software/http-app"];

    private static void AssertIndexResponses(JsonElement responses, string allowedFile, int files, int bytes, int roots)
    {
        Assert.Equal(4, responses.GetArrayLength());
        for (var i = 0; i < responses.GetArrayLength(); i++)
        {
            var body = responses[i];
            if (i % 2 == 0)
            {
                Assert.Equal(files, body.GetArrayLength());
                Assert.Contains(body.EnumerateArray(), row => row.GetProperty("fullPath").GetString() == allowedFile);
            }
            else
            {
                Assert.Equal(files, body.GetProperty("fileCount").GetInt64());
                Assert.Equal(bytes, body.GetProperty("totalBytes").GetInt64());
                Assert.Equal(roots, body.GetProperty("roots").GetArrayLength());
            }
        }
    }

    private static async Task<JsonElement> RequestIndexFromOwnedProcessAsync(Uri url, string token, string[]? requestPaths = null)
    {
        var paths = string.Join(",", (requestPaths ?? IndexHttpPaths).Select(path => "'" + path + "'"));
        var command = "$ErrorActionPreference='Stop'; $bodies=@(); foreach($path in @(" + paths + ")) { "
            + "$request=[Net.WebRequest]::Create('" + url.AbsoluteUri.TrimEnd('/') + "'+$path); $request.Proxy=$null; $request.Timeout=5000; "
            + "$request.Headers.Add('" + LoopbackApiAccessToken.HeaderName + "','" + token + "'); "
            + "$request.Headers.Add('X-Windows-Sid','S-1-5-18'); $response=$request.GetResponse(); "
            + "$reader=[IO.StreamReader]::new($response.GetResponseStream()); try { $bodies+=,$reader.ReadToEnd() } "
            + "finally { $reader.Dispose(); $response.Dispose() } }; [Console]::Out.Write('['+($bodies -join ',')+']');";
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }
        })!;
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)); }
        finally
        {
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        Assert.True(child.ExitCode == 0, $"Owned client exited {child.ExitCode}: {await errors}");
        Assert.Equal(string.Empty, await errors);
        using var document = JsonDocument.Parse(await output);
        return document.RootElement.Clone();
    }

    private sealed class IndexHttpEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "IndexHttp.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
